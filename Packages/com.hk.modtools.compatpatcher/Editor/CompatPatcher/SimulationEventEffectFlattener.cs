using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Reflection-merges Odin-backed <c>SimulationEventEffect</c> trees into an
    /// <see cref="HkElement"/> body so <see cref="UnityYaml.Flatten"/> / StructDiff see
    /// Amount, enums, etc. — not only <see cref="HkElement.Refs"/>.
    /// </summary>
    /// <remarks>
    /// Carriers: root <c>SimulationEventEffects</c> / <c>Effects</c> / <c>EffectByLevels</c> /
    /// <c>RepeatingEffect</c>; <c>Choices[]</c> → civic <c>SimulationEventEffects</c> or narrative
    /// <c>NarrativeEventEffects[].SimulationEventEffect</c>.
    /// </remarks>
    public static class SimulationEventEffectFlattener
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly HashSet<string> SkipFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "SimulationEffectDescriptionOverride", "UIMapperOverride", "DlcPrerequisite",
            "GainValues", "name",
        };

        /// <summary>
        /// Overwrite effect-related keys on <paramref name="body"/> from live reflection.
        /// Harvests datatable element names into <paramref name="refs"/>.
        /// </summary>
        public static void MergeInto(object live, Dictionary<string, object> body, HashSet<string> refs)
        {
            if (live == null || body == null) return;
            refs ??= new HashSet<string>();

            MergeRootEffectArray(live, "SimulationEventEffects", body, refs);
            MergeRootEffectArray(live, "Effects", body, refs);
            MergeRootEffectArray(live, "EffectByLevels", body, refs);
            MergeRootSingleEffect(live, "RepeatingEffect", body, refs);
            MergeChoices(live, body, refs);
        }

        static void MergeRootEffectArray(object live, string field, Dictionary<string, object> body, HashSet<string> refs)
        {
            if (!TryGetFieldValue(live, field, out var raw) || raw == null) return;
            if (raw is not System.Collections.IEnumerable en || raw is string) return;
            // Descriptor (and similar) also have an `Effects` field — but of type Effect[], not
            // SimulationEventEffect[]. EffectToDict skips those → empty list. Never overwrite the
            // SerializedObject body in that case or PropertyEffect diffs vanish (false Identical).
            if (!IsSimulationEventEffectEnumerable(en, out bool anyNonNull)) return;
            var list = BuildEffectList(en, refs);
            if (list.Count == 0 && anyNonNull) return;
            body[field] = list;
        }

        /// <summary>
        /// True when every non-null element is a <c>SimulationEventEffect</c> (or the sequence is empty).
        /// False when the sequence holds a different element type (e.g. Descriptor <c>Effect</c>).
        /// </summary>
        static bool IsSimulationEventEffectEnumerable(System.Collections.IEnumerable en, out bool anyNonNull)
        {
            anyNonNull = false;
            foreach (var e in en)
            {
                if (e == null) continue;
                anyNonNull = true;
                if (!IsSimulationEventEffectType(e.GetType())) return false;
            }
            return true;
        }

        static void MergeRootSingleEffect(object live, string field, Dictionary<string, object> body, HashSet<string> refs)
        {
            if (!TryGetFieldValue(live, field, out var raw) || raw == null) return;
            var dict = EffectToDict(raw, refs);
            if (dict != null) body[field] = dict;
        }

        static void MergeChoices(object live, Dictionary<string, object> body, HashSet<string> refs)
        {
            if (!TryGetFieldValue(live, "Choices", out var raw) || raw == null) return;
            if (raw is not System.Collections.IEnumerable en || raw is string) return;

            var existing = body.TryGetValue("Choices", out var prev) ? prev as List<object> : null;
            var list = new List<object>();
            int i = 0;
            foreach (var choice in en)
            {
                Dictionary<string, object> dict;
                if (existing != null && i < existing.Count && existing[i] is Dictionary<string, object> ed)
                    dict = new Dictionary<string, object>(ed);
                else
                    dict = new Dictionary<string, object>();

                if (choice != null)
                {
                    PutStringField(dict, choice, "Name");
                    PutStringField(dict, choice, "Title");

                    if (TryGetFieldValue(choice, "SimulationEventEffects", out var sef) && sef != null
                        && sef is System.Collections.IEnumerable sefEn && sef is not string)
                        dict["SimulationEventEffects"] = BuildEffectList(sefEn, refs);

                    if (TryGetFieldValue(choice, "NarrativeEventEffects", out var nef) && nef != null
                        && nef is System.Collections.IEnumerable nefEn && nef is not string)
                        dict["NarrativeEventEffects"] = BuildNarrativeEffectList(nefEn, refs);
                }

                list.Add(dict);
                i++;
            }
            body["Choices"] = list;
        }

        static List<object> BuildEffectList(System.Collections.IEnumerable effects, HashSet<string> refs)
        {
            var list = new List<object>();
            foreach (var e in effects)
            {
                var d = EffectToDict(e, refs);
                if (d != null) list.Add(d);
            }
            return list;
        }

        static List<object> BuildNarrativeEffectList(System.Collections.IEnumerable wrappers, HashSet<string> refs)
        {
            var list = new List<object>();
            foreach (var w in wrappers)
            {
                if (w == null) continue;
                var wrap = new Dictionary<string, object>();
                if (TryGetFieldValue(w, "Reversible", out var rev) && rev is bool b)
                    wrap["Reversible"] = b ? "1" : "0";

                if (TryGetFieldValue(w, "SimulationEventEffect", out var sef) && sef != null)
                {
                    var d = EffectToDict(sef, refs);
                    if (d != null) wrap["SimulationEventEffect"] = d;
                }
                list.Add(wrap);
            }
            return list;
        }

        static Dictionary<string, object> EffectToDict(object effect, HashSet<string> refs)
        {
            if (effect == null) return null;
            var t = effect.GetType();
            // Only walk SimulationEventEffect hierarchy (and subclasses).
            if (!IsSimulationEventEffectType(t)) return null;

            string typeName = ShortEffectTypeName(t);
            var dict = new Dictionary<string, object> { ["Type"] = typeName };
            string targetId = null;
            var primaryRefs = new List<string>();

            foreach (var f in t.GetFields(All))
            {
                if (f.IsStatic || SkipFields.Contains(f.Name)) continue;
                object val;
                try { val = f.GetValue(effect); } catch { continue; }
                if (val == null) continue;

                if (f.Name == "TargetID" && val is string tid)
                {
                    targetId = tid;
                    if (!string.IsNullOrEmpty(tid)) dict["TargetID"] = tid;
                    continue;
                }

                if (TryWriteRef(dict, f.Name, val, refs, primaryRefs)) continue;
                if (TryWriteRefArray(dict, f.Name, val, refs, primaryRefs)) continue;
                if (TryWriteScalar(dict, f.Name, val)) continue;
                // Skip other nested objects (prerequisites, gains, …).
            }

            primaryRefs.Sort(StringComparer.Ordinal);
            dict["EffectId"] = typeName + "|" + (targetId ?? "") + "|" + string.Join("+", primaryRefs);
            return dict;
        }

        static bool TryWriteRef(Dictionary<string, object> dict, string field, object val,
            HashSet<string> refs, List<string> primaryRefs)
        {
            string n = GetRefName(val);
            if (n == null) return false;
            if (n.Length > 0)
            {
                refs.Add(n);
                primaryRefs.Add(n);
                dict[field] = n;
            }
            return true;
        }

        static bool TryWriteRefArray(Dictionary<string, object> dict, string field, object val,
            HashSet<string> refs, List<string> primaryRefs)
        {
            if (val is not System.Collections.IEnumerable en || val is string) return false;
            // Peek first non-null element — must look like DatatableElementReference.
            object first = null;
            foreach (var item in en) { if (item != null) { first = item; break; } }
            if (first == null) return false;
            if (GetRefName(first) == null && !LooksLikeDatatableElementReference(first)) return false;

            var names = new List<object>();
            foreach (var item in en)
            {
                string n = GetRefName(item);
                if (string.IsNullOrEmpty(n)) continue;
                refs.Add(n);
                primaryRefs.Add(n);
                names.Add(n);
            }
            dict[field] = names;
            return true;
        }

        static bool TryWriteScalar(Dictionary<string, object> dict, string field, object val)
        {
            switch (val)
            {
                case string s:
                    if (string.IsNullOrEmpty(s)) return true;
                    dict[field] = s;
                    return true;
                case bool b:
                    dict[field] = b ? "1" : "0";
                    return true;
                case byte or sbyte or short or ushort or int or uint or long or ulong:
                    dict[field] = Convert.ToString(val, CultureInfo.InvariantCulture);
                    return true;
                case float fl:
                    dict[field] = fl.ToString("0.###", CultureInfo.InvariantCulture);
                    return true;
                case double db:
                    dict[field] = db.ToString("0.###", CultureInfo.InvariantCulture);
                    return true;
                default:
                    if (val.GetType().IsEnum)
                    {
                        // Underlying int so Flags enums match LiveElementBuilder (not ToString names).
                        dict[field] = Convert.ToInt64(val).ToString(CultureInfo.InvariantCulture);
                        return true;
                    }
                    return false;
            }
        }

        static bool IsSimulationEventEffectType(Type t)
        {
            for (var c = t; c != null; c = c.BaseType)
            {
                if (c.Name == "SimulationEventEffect") return true;
            }
            return false;
        }

        static string ShortEffectTypeName(Type t)
        {
            string n = t.Name ?? "?";
            const string prefix = "SimulationEventEffect_";
            if (n.StartsWith(prefix, StringComparison.Ordinal)) return n.Substring(prefix.Length);
            return n;
        }

        /// <summary>
        /// Returns the element name if <paramref name="val"/> is a DatatableElementReference-like
        /// object; empty string if it is that type but unnamed; null if not a ref type.
        /// </summary>
        static string GetRefName(object val)
        {
            if (val == null) return null;
            if (!LooksLikeDatatableElementReference(val)) return null;
            var t = val.GetType();
            var f = t.GetField("serializableElementName", All)
                    ?? t.GetField("SerializableElementName", All);
            if (f != null && f.GetValue(val) is string s) return s ?? "";
            var p = t.GetProperty("XmlSerializableElementName", All)
                    ?? t.GetProperty("SerializableElementName", All)
                    ?? t.GetProperty("ElementName", All);
            if (p != null)
            {
                var pv = p.GetValue(val);
                if (pv is string ps) return ps ?? "";
                // StaticString — ToString often yields the name
                if (pv != null)
                {
                    string ts = pv.ToString();
                    if (!string.IsNullOrEmpty(ts) && ts != pv.GetType().FullName) return ts;
                }
            }
            return "";
        }

        static bool LooksLikeDatatableElementReference(object val)
        {
            if (val == null) return false;
            var t = val.GetType();
            if (t.Name == "DatatableElementReference" || t.Name.StartsWith("DatatableElementReference", StringComparison.Ordinal))
                return true;
            return t.GetField("serializableElementName", All) != null
                   || t.GetField("SerializableElementName", All) != null;
        }

        static bool TryGetFieldValue(object obj, string name, out object value)
        {
            value = null;
            if (obj == null) return false;
            var f = obj.GetType().GetField(name, All);
            if (f == null) return false;
            try { value = f.GetValue(obj); } catch { return false; }
            return true;
        }

        static void PutStringField(Dictionary<string, object> dict, object obj, string field)
        {
            if (!TryGetFieldValue(obj, field, out var v) || v is not string s || string.IsNullOrEmpty(s)) return;
            dict[field] = s;
        }
    }
}
