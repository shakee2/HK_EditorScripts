using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Amplitude.Framework;
using UnityEditor;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Builds <see cref="HkElement"/> from a live mounted ScriptableObject (assetbundle path).
    /// Produces stable Flat/Refs without YAML local fileIDs — object refs become names /
    /// <c>serializableElementName</c>.
    /// </summary>
    public static class LiveElementBuilder
    {
        static readonly HashSet<string> VolatileKeys = new HashSet<string>
        {
            "m_ObjectHideFlags","m_CorrespondingSourceObject","m_PrefabInstance","m_PrefabAsset",
            "m_GameObject","m_Enabled","m_EditorHideFlags","m_Script","m_EditorClassIdentifier","m_Name",
            "Key", // definition identity key — see UnityYaml.VolatileKeys
        };

        public static HkElement Build(UnityEngine.Object live, string logicalPath, string collectionStem)
        {
            if (live == null) return null;
            string name = live.name;
            if (string.IsNullOrEmpty(name)) return null;

            string typeKey = ScriptTypeKey(live);
            var refs = new HashSet<string>();
            // Prefer SerializedObject field walk first. Many Amplitude types carry an Odin
            // serializationData blob *and* Unity-serialized gameplay fields — if we bail to
            // RefDiff solely because DetectOdin is true, StructDiff never runs and real
            // conflicts (constants, families, …) disappear.
            var body = BuildBody(live, refs) ?? new Dictionary<string, object>();
            // Odin-backed SimulationEventEffect trees are often invisible to SerializedObject
            // (hybrid tech) or the whole element is pure Odin (narrative). Merge via reflection
            // so StructDiff sees Amount / enums / etc., not only Refs.
            SimulationEventEffectFlattener.MergeInto(live, body, refs);
            // Pure Odin elements (DeedDefinition, …): SerializedObject often leaves Trigger/Evaluator
            // empty while the inspector shows live values — reflection fills those for StructDiff.
            ReflectionBodyMerge.MergeInto(live, body, refs);
            // Authoritative overlay for DatatableElementReference[] — SO array collapse / empty
            // relative names must not win over live field values (UnitAbility, Package04, …).
            OverlayDatatableRefArrays(live, body, refs);

            bool odin;
            Dictionary<string, string> flat = null;
            if (UnityYaml.HasGameplayKeys(body))
            {
                odin = false;
                flat = UnityYaml.Flatten(body);
            }
            else
            {
                odin = true;
                body = null;
                var kept = new HashSet<string>(refs);
                refs.Clear();
                CollectRefsOnly(live, refs);
                foreach (var r in kept) refs.Add(r);
            }

            // Roots are collection containers only. Never treat an empty/opaque body as a root —
            // that was marking every live element IsRoot and wiping the conflict list.
            bool isRoot = live is IDatatableElementCollection
                          || (!(live is IDatatableElement) && name == collectionStem);

            return new HkElement
            {
                Name = name,
                Type = typeKey,
                TypeHint = collectionStem ?? live.GetType().Name,
                SourcePath = logicalPath,
                IsRoot = isRoot,
                Body = body,
                Flat = flat,
                Odin = odin,
                DocLines = null,
                Refs = refs,
                LiveObject = live,
            };
        }

        public static string ScriptTypeKey(UnityEngine.Object live)
        {
            if (live is ScriptableObject so)
            {
                var ms = MonoScript.FromScriptableObject(so);
                if (ms != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ms, out string guid, out long localId))
                    return guid + ":" + localId;
            }
            // Fallback: type full name (still stable within a session; cross-format may not meet YAML keys)
            return live.GetType().FullName ?? live.GetType().Name;
        }

        [MenuItem("Tools/shakee's Tools/Debug/Compat Patcher/Dump Live Flatten Of Selection")]
        static void DumpLiveFlattenOfSelection()
        {
            var obj = Selection.activeObject;
            if (obj == null) { Debug.LogWarning("[CompatPatcher] Select a datatable element first."); return; }
            var el = Build(obj, AssetDatabase.GetAssetPath(obj) ?? obj.name, obj.GetType().Name);
            if (el == null) { Debug.LogWarning("[CompatPatcher] LiveElementBuilder.Build returned null."); return; }
            var sb = new StringBuilder();
            sb.AppendLine($"[CompatPatcher] Live Flatten — {el.Name} ({obj.GetType().Name}) Odin={el.Odin} Flat={(el.Flat?.Count ?? 0)} Refs={el.Refs?.Count ?? 0}");
            if (el.Body != null)
            {
                sb.AppendLine("-- Body keys --");
                foreach (var kv in el.Body.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    string preview = kv.Value is List<object> list
                        ? $"list[{list.Count}]"
                        : (kv.Value?.ToString() ?? "null");
                    if (preview.Length > 120) preview = preview.Substring(0, 117) + "...";
                    sb.AppendLine($"  {kv.Key} = {preview}");
                }
            }
            if (el.Flat != null)
            {
                sb.AppendLine("-- Flat --");
                foreach (var kv in el.Flat.OrderBy(k => k.Key, StringComparer.Ordinal))
                    sb.AppendLine($"  {kv.Key} = {kv.Value}");
            }
            Debug.Log(sb.ToString());
        }

        static void CollectRefsOnly(UnityEngine.Object live, HashSet<string> refs)
        {
            using var so = new SerializedObject(live);
            var it = so.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                enter = it.propertyType == SerializedPropertyType.Generic
                     || it.propertyType == SerializedPropertyType.ManagedReference;
                HarvestRef(it, refs);
            }
            // Reflection fallback for Odin-backed private fields not visible to SerializedObject
            HarvestRefsReflect(live, refs, 0);
        }

        static Dictionary<string, object> BuildBody(UnityEngine.Object live, HashSet<string> refs)
        {
            using var so = new SerializedObject(live);
            var root = so.FindProperty("m_Script");
            if (root == null) return null;

            // Walk sibling properties under the ScriptableObject (after m_Script)
            var body = new Dictionary<string, object>();
            var prop = so.GetIterator();
            if (!prop.NextVisible(true)) return body; // enter object
            // skip script
            while (prop.NextVisible(false))
            {
                if (VolatileKeys.Contains(prop.name)) continue;
                if (prop.name == "serializationData")
                {
                    // Do not deep-walk Odin serializationData — it can be megabytes of nodes and
                    // freezes Compare. Marker only; HasGameplayKeys ignores this key.
                    body["serializationData"] = new Dictionary<string, object> { ["_present"] = "1" };
                    continue;
                }
                body[prop.name] = ReadPropertyValue(prop, refs);
            }
            return body;
        }

        static object ReadPropertyValue(SerializedProperty prop, HashSet<string> refs)
        {
            HarvestRef(prop, refs);
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return prop.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return prop.boolValue ? "1" : "0";
                case SerializedPropertyType.Float:
                    return prop.floatValue.ToString("0.###", CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return prop.stringValue ?? "";
                case SerializedPropertyType.Enum:
                    // Ordinary enums: "0 = Tier1". Flags bitmasks stay bare ints (see EnumFlatValue).
                    return EnumFlatValue.Format(prop);
                case SerializedPropertyType.ObjectReference:
                    return prop.objectReferenceValue != null ? prop.objectReferenceValue.name : "";
                case SerializedPropertyType.ArraySize:
                    return prop.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Character:
                    return ((int)prop.intValue).ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.LayerMask:
                    return prop.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2:
                    return CanonVec(prop.vector2Value.x, prop.vector2Value.y);
                case SerializedPropertyType.Vector3:
                    return CanonVec(prop.vector3Value.x, prop.vector3Value.y, prop.vector3Value.z);
                case SerializedPropertyType.Vector4:
                    return CanonVec(prop.vector4Value.x, prop.vector4Value.y, prop.vector4Value.z, prop.vector4Value.w);
                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    return CanonVec(c.r, c.g, c.b, c.a);
                case SerializedPropertyType.Rect:
                    var r = prop.rectValue;
                    return CanonVec(r.x, r.y, r.width, r.height);
                case SerializedPropertyType.Quaternion:
                    var q = prop.quaternionValue;
                    return CanonVec(q.x, q.y, q.z, q.w);
                case SerializedPropertyType.ExposedReference:
                    return prop.exposedReferenceValue != null ? prop.exposedReferenceValue.name : "";
                case SerializedPropertyType.FixedBufferSize:
                    return prop.fixedBufferSize.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Generic:
                case SerializedPropertyType.ManagedReference:
                    return ReadGeneric(prop, refs);
                default:
                    return prop.hasVisibleChildren ? ReadGeneric(prop, refs) : prop.propertyType.ToString();
            }
        }

        /// <summary>
        /// Force <c>DatatableElementReference[]</c> fields from live reflection so Flatten sees
        /// real name-sets even when SerializedObject mis-reads the array.
        /// </summary>
        static void OverlayDatatableRefArrays(object live, Dictionary<string, object> body, HashSet<string> refs)
        {
            if (live == null || body == null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var f in live.GetType().GetFields(flags))
            {
                if (f.IsStatic || f.IsDefined(typeof(NonSerializedAttribute), inherit: true)) continue;
                if (!f.FieldType.IsArray) continue;
                var elemT = f.FieldType.GetElementType();
                if (elemT == null || elemT.Name != "DatatableElementReference") continue;
                object raw;
                try { raw = f.GetValue(live); } catch { continue; }
                if (raw is not Array arr) continue;

                var list = new List<object>();
                for (int i = 0; i < arr.Length; i++)
                {
                    object item = arr.GetValue(i);
                    string n = ReadDatatableRefName(item);
                    if (!string.IsNullOrEmpty(n)) refs.Add(n);
                    list.Add(new Dictionary<string, object> { ["serializableElementName"] = n ?? "" });
                }
                body[f.Name] = list;
            }
        }

        static string ReadDatatableRefName(object val)
        {
            if (val == null) return "";
            var t = val.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var c = t; c != null; c = c.BaseType)
            {
                try
                {
                    var field = c.GetField("serializableElementName", flags | BindingFlags.DeclaredOnly)
                                ?? c.GetField("SerializableElementName", flags | BindingFlags.DeclaredOnly);
                    if (field != null && field.GetValue(val) is string s && s.Length > 0) return s;
                }
                catch { /* try property */ }
            }
            for (var c = t; c != null; c = c.BaseType)
            {
                foreach (var propName in new[]
                         { "XmlSerializableElementName", "SerializableElementName", "ElementName" })
                {
                    try
                    {
                        var prop = c.GetProperty(propName, flags | BindingFlags.DeclaredOnly);
                        if (prop == null || !prop.CanRead) continue;
                        var pv = prop.GetValue(val);
                        if (pv is string ps && ps.Length > 0) return ps;
                        if (pv != null)
                        {
                            string ts = pv.ToString();
                            if (!string.IsNullOrEmpty(ts) && ts != pv.GetType().FullName
                                && !ts.StartsWith("Amplitude.", StringComparison.Ordinal)
                                && ts.IndexOf(' ') < 0)
                                return ts;
                        }
                    }
                    catch { /* next */ }
                }
            }
            return "";
        }

        static object ReadGeneric(SerializedProperty prop, HashSet<string> refs)
        {
            // Arrays first. isArray is normally true for T[]; also accept Generic+Array.size
            // (some drawers report propertyType Generic). Never call FindPropertyRelative
            // ("serializableElementName") on the array itself — that collapses the list to one
            // dict and makes every mod look Identical.
            if (IsSerializedArray(prop))
            {
                var list = new List<object>();
                for (int i = 0; i < prop.arraySize; i++)
                    list.Add(ReadPropertyValue(prop.GetArrayElementAtIndex(i), refs));
                return list;
            }

            // DatatableElementReference (single): emit { serializableElementName: n }.
            var nameProp = prop.FindPropertyRelative("serializableElementName");
            if (nameProp != null && nameProp.propertyType == SerializedPropertyType.String)
            {
                string n = nameProp.stringValue ?? "";
                if (!string.IsNullOrEmpty(n)) refs.Add(n);
                return new Dictionary<string, object> { ["serializableElementName"] = n };
            }

            // FixedPoint: RawValue is internal — NextVisible skips it; FindPropertyRelative still finds it.
            var rawProp = prop.FindPropertyRelative("RawValue");
            if (rawProp != null && rawProp.propertyType == SerializedPropertyType.Integer)
            {
                return new Dictionary<string, object>
                {
                    ["RawValue"] = rawProp.intValue.ToString(CultureInfo.InvariantCulture),
                };
            }

            var map = new Dictionary<string, object>();
            var child = prop.Copy();
            var end = prop.GetEndProperty();
            bool enter = true;
            // NextVisible (not Next): Next descends into every nested serialized byte of
            // Odin serializationData and can freeze Compare for minutes per element.
            // FixedPoint.RawValue is handled above via FindPropertyRelative.
            while (child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end))
            {
                enter = false;
                if (VolatileKeys.Contains(child.name)) continue;
                map[child.name] = ReadPropertyValue(child, refs);
            }
            // Leave PropertyEffect / RPN maps intact — UnityYaml.Flatten collapses them.
            return map;
        }

        static bool IsSerializedArray(SerializedProperty prop)
        {
            if (prop == null || prop.propertyType == SerializedPropertyType.String) return false;
            if (prop.isArray) return true;
            // Fallback: Unity stores arrays as Generic with an Array.size child.
            return prop.propertyType == SerializedPropertyType.Generic
                   && prop.FindPropertyRelative("Array.size") != null;
        }

        static void HarvestRef(SerializedProperty prop, HashSet<string> refs)
        {
            if (prop == null) return;
            if (prop.name == "serializableElementName" && prop.propertyType == SerializedPropertyType.String
                && !string.IsNullOrEmpty(prop.stringValue))
                refs.Add(prop.stringValue);
        }

        static void HarvestRefsReflect(object obj, HashSet<string> refs, int depth)
        {
            if (obj == null || depth > 6) return;
            var t = obj.GetType();
            if (t.IsPrimitive || obj is string) return;
            if (obj is UnityEngine.Object uo)
            {
                // Don't walk into arbitrary Unity assets
                if (!(uo is ScriptableObject) || depth > 0) return;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var f in t.GetFields(flags))
            {
                object val;
                try { val = f.GetValue(obj); } catch { continue; }
                if (val == null) continue;
                if (f.Name == "serializableElementName" || f.Name == "SerializableElementName")
                {
                    if (val is string s && !string.IsNullOrEmpty(s)) refs.Add(s);
                    continue;
                }
                if (val is string) continue;
                if (val is System.Collections.IEnumerable en && val is not string)
                {
                    foreach (var item in en)
                    {
                        if (item == null || item is string) continue;
                        TryAddSerializableName(item, refs);
                        if (item.GetType().IsValueType || !(item is ScriptableObject))
                            HarvestRefsReflect(item, refs, depth + 1);
                    }
                    continue;
                }
                TryAddSerializableName(val, refs);
                if (val.GetType().IsValueType || (!(val is ScriptableObject) && !(val is UnityEngine.Object)))
                    HarvestRefsReflect(val, refs, depth + 1);
            }
        }

        static void TryAddSerializableName(object val, HashSet<string> refs)
        {
            if (val == null) return;
            var f = val.GetType().GetField("serializableElementName",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
                f = val.GetType().GetField("SerializableElementName",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.GetValue(val) is string s && !string.IsNullOrEmpty(s))
                refs.Add(s);
            var p = val.GetType().GetProperty("SerializableElementName",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.GetValue(val) is string ps && !string.IsNullOrEmpty(ps))
                refs.Add(ps);
        }

        static string CanonVec(params float[] parts)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(parts[i].ToString("0.###", CultureInfo.InvariantCulture));
            }
            sb.Append('}');
            return sb.ToString();
        }
    }
}
