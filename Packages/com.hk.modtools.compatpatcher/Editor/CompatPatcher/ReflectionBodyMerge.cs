using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Reflection-fills an <see cref="HkElement"/> body from a live object when Unity
    /// <see cref="SerializedObject"/> misses Odin-backed gameplay (e.g. <c>DeedDefinition</c>
    /// Trigger / Evaluator). Without this, pure-Odin elements look Identical under RefDiff.
    /// </summary>
    public static class ReflectionBodyMerge
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const int MaxDepth = 8;

        static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "serializationData", "name", "hideFlags",
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
            "m_GameObject", "m_Enabled", "m_EditorHideFlags", "m_Script", "m_EditorClassIdentifier", "m_Name",
        };

        public static void MergeInto(object live, Dictionary<string, object> body, HashSet<string> refs)
        {
            if (live == null || body == null) return;
            refs ??= new HashSet<string>();
            foreach (var f in live.GetType().GetFields(All))
            {
                if (ShouldSkipField(f)) continue;
                object val;
                try { val = f.GetValue(live); } catch { continue; }
                if (val == null) continue;
                var converted = ConvertValue(val, refs, 0);
                if (converted != null)
                    body[f.Name] = converted;
            }
        }

        static bool ShouldSkipField(FieldInfo f)
        {
            if (f == null || f.IsStatic) return true;
            if (SkipNames.Contains(f.Name)) return true;
            if (f.Name.StartsWith("<", StringComparison.Ordinal)) return true; // backing fields
            if (f.IsDefined(typeof(NonSerializedAttribute), inherit: true)) return true;
            // Skip UnityEngine.Object references (other assets) — keep structs/classes/value data.
            if (typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType)) return true;
            return false;
        }

        static object ConvertValue(object val, HashSet<string> refs, int depth)
        {
            if (val == null || depth > MaxDepth) return null;
            var t = val.GetType();

            if (t.IsPrimitive || val is string || val is decimal)
                return Convert.ToString(val, CultureInfo.InvariantCulture) ?? "";

            if (t.IsEnum)
                return Convert.ToInt64(val).ToString(CultureInfo.InvariantCulture);

            // FixedPoint (and similar): prefer RawValue field, then Raw property.
            var rawF = t.GetField("RawValue", All);
            if (rawF != null && (rawF.FieldType == typeof(int) || rawF.FieldType == typeof(long)))
            {
                try
                {
                    return new Dictionary<string, object>
                    {
                        ["RawValue"] = Convert.ToString(rawF.GetValue(val), CultureInfo.InvariantCulture) ?? "0",
                    };
                }
                catch { /* fall through */ }
            }
            var rawP = t.GetProperty("Raw", All);
            if (rawP != null && rawP.CanRead
                && (rawP.PropertyType == typeof(int) || rawP.PropertyType == typeof(long)))
            {
                try
                {
                    return new Dictionary<string, object>
                    {
                        ["RawValue"] = Convert.ToString(rawP.GetValue(val), CultureInfo.InvariantCulture) ?? "0",
                    };
                }
                catch { /* fall through */ }
            }

            string refName = TryRefName(val, refs);
            if (refName != null)
                return new Dictionary<string, object> { ["serializableElementName"] = refName };

            if (val is System.Collections.IEnumerable en && val is not string)
            {
                var list = new List<object>();
                foreach (var item in en)
                {
                    if (item == null) continue;
                    if (item is UnityEngine.Object) continue;
                    var c = ConvertValue(item, refs, depth + 1);
                    if (c != null) list.Add(c);
                }
                return list;
            }

            if (t.IsValueType || t.IsClass)
            {
                var map = new Dictionary<string, object>();
                // Polymorphic class instances (DeedEvaluator, …): record concrete type.
                if (!t.IsValueType)
                    map["Type"] = t.Name;
                foreach (var f in t.GetFields(All))
                {
                    if (ShouldSkipField(f)) continue;
                    object fv;
                    try { fv = f.GetValue(val); } catch { continue; }
                    if (fv == null) continue;
                    var c = ConvertValue(fv, refs, depth + 1);
                    if (c != null) map[f.Name] = c;
                }
                return map.Count > 0 ? map : null;
            }

            return val.ToString();
        }

        static string TryRefName(object val, HashSet<string> refs)
        {
            if (val == null) return null;
            var t = val.GetType();
            if (t.Name != "DatatableElementReference"
                && !t.Name.StartsWith("DatatableElementReference", StringComparison.Ordinal))
                return null;
            var f = t.GetField("serializableElementName", All)
                    ?? t.GetField("SerializableElementName", All);
            string n = null;
            try
            {
                if (f != null) n = f.GetValue(val) as string;
            }
            catch { /* try property */ }
            if (string.IsNullOrEmpty(n))
            {
                var p = t.GetProperty("XmlSerializableElementName", All)
                        ?? t.GetProperty("SerializableElementName", All);
                try
                {
                    if (p != null) n = p.GetValue(val) as string;
                }
                catch { /* leave empty */ }
            }
            n ??= "";
            if (n.Length > 0) refs.Add(n);
            return n;
        }
    }
}
