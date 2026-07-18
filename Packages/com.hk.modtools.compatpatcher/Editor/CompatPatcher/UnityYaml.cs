using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Minimal Unity-YAML reader, just enough to compare database elements.
    /// It parses one MonoBehaviour document's field block into nested
    /// Dictionary&lt;string,object&gt; / List&lt;object&gt; / string, and flattens that
    /// into path -> value leaves the way the standalone prototype does.
    ///
    /// It is deliberately NOT a general YAML parser: flow scalars ({...}/[...]) are
    /// kept raw, and anything it can't parse degrades to a scalar string so a caller
    /// never crashes on an odd asset.
    /// </summary>
    public static class UnityYaml
    {
        // fields that are editor/serialization bookkeeping, never gameplay data.
        // Key = IDatatableElementWithByteKey/ShortKey: Mod Tools locks it; mods often omit or zero it;
        // uniqueness is a vanilla-only load check — never a merge conflict.
        static readonly HashSet<string> VolatileKeys = new HashSet<string>
        {
            "m_ObjectHideFlags","m_CorrespondingSourceObject","m_PrefabInstance","m_PrefabAsset",
            "m_GameObject","m_Enabled","m_EditorHideFlags","m_Script","m_EditorClassIdentifier","m_Name",
            "Key",
        };

        // first present of these identifies a list entry across mods (else index).
        // EffectId first so SimulationEventEffect rows (Type|TargetID|refs) do not collide on
        // a shared TargetID=Empire alone.
        static readonly string[] EntryKeys =
            { "EffectId", "serializableElementName", "TargetProperty", "Name", "TargetID", "Type", "Descriptor",
              "SimulationEvent" };

        public const string Missing = "∅"; // ∅

        // ---- Line model -----------------------------------------------------
        struct Line
        {
            public int indent;
            public string text;     // trimmed content (for non-dash) or dash content
            public bool dash;       // was a "- ..." sequence entry
            public string dashContent;
        }

        static List<Line> Tokenize(IEnumerable<string> raw)
        {
            var outp = new List<Line>();
            foreach (var r in raw)
            {
                if (string.IsNullOrWhiteSpace(r)) continue;
                int ind = 0;
                while (ind < r.Length && r[ind] == ' ') ind++;
                string t = r.Substring(ind);
                if (t.StartsWith("- ") || t == "-")
                {
                    string content = t == "-" ? "" : t.Substring(2);
                    outp.Add(new Line { indent = ind, text = t, dash = true, dashContent = content });
                }
                else
                {
                    outp.Add(new Line { indent = ind, text = t, dash = false });
                }
            }
            return outp;
        }

        // ---- Public entry: parse a MonoBehaviour block into a dict ----------
        /// <summary>lines = the raw lines of one element document (the "MonoBehaviour:" mapping).</summary>
        public static Dictionary<string, object> ParseElementBody(IEnumerable<string> docLines)
        {
            try
            {
                var lines = Tokenize(docLines);
                // find "MonoBehaviour:" then parse its children
                int i = 0;
                while (i < lines.Count && lines[i].text != "MonoBehaviour:") i++;
                if (i >= lines.Count) return null;
                int baseIndent = lines[i].indent;
                i++;
                if (i >= lines.Count || lines[i].indent <= baseIndent) return null;
                int childIndent = lines[i].indent;
                var node = ParseNode(lines, ref i, childIndent) as Dictionary<string, object>;
                return node;
            }
            catch
            {
                return null; // degrade: caller treats as ref-only / opaque
            }
        }

        static object ParseNode(List<Line> lines, ref int idx, int indent)
        {
            if (idx >= lines.Count) return null;
            var ln = lines[idx];
            if (ln.dash && ln.indent == indent) return ParseList(lines, ref idx, indent);
            return ParseMap(lines, ref idx, indent);
        }

        static object ParseMap(List<Line> lines, ref int idx, int indent)
        {
            var map = new Dictionary<string, object>();
            while (idx < lines.Count && lines[idx].indent == indent && !lines[idx].dash)
            {
                string text = lines[idx].text;
                int c = text.IndexOf(':');
                if (c < 0) { idx++; continue; } // not a mapping line; skip defensively
                string key = text.Substring(0, c).Trim();
                string val = text.Substring(c + 1).Trim();
                idx++;
                if (val.Length == 0 && idx < lines.Count && lines[idx].dash && lines[idx].indent == indent)
                {
                    // block sequence: Unity writes the dashes at the SAME indent as the key
                    map[key] = ParseList(lines, ref idx, indent);
                }
                else if (val.Length == 0 && idx < lines.Count && lines[idx].indent > indent)
                {
                    map[key] = ParseNode(lines, ref idx, lines[idx].indent);
                }
                else
                {
                    map[key] = val.Length == 0 ? null : (object)val;
                }
            }
            return map;
        }

        static object ParseList(List<Line> lines, ref int idx, int indent)
        {
            var list = new List<object>();
            while (idx < lines.Count && lines[idx].indent == indent && lines[idx].dash)
            {
                string content = lines[idx].dashContent;
                idx++;
                var sub = new List<Line>();
                if (content.Length > 0)
                {
                    bool subDash = content.StartsWith("- ") || content == "-";
                    sub.Add(new Line
                    {
                        indent = indent + 2,
                        text = content,
                        dash = subDash,
                        dashContent = subDash ? (content == "-" ? "" : content.Substring(2)) : null
                    });
                }
                while (idx < lines.Count && lines[idx].indent > indent) { sub.Add(lines[idx]); idx++; }

                if (sub.Count == 0) { list.Add(null); continue; }
                if (sub.Count == 1 && !sub[0].dash && sub[0].text.IndexOf(':') < 0)
                {
                    list.Add(sub[0].text); // scalar list item, e.g. "- Territory"
                    continue;
                }
                int j = 0;
                list.Add(ParseNode(sub, ref j, indent + 2));
            }
            return list;
        }

        // ---- Flatten: nested body -> { path : canonical-value } ------------
        public static Dictionary<string, string> Flatten(Dictionary<string, object> body)
        {
            var outp = new Dictionary<string, string>();
            if (body != null) FlattenInto(body, "", outp);
            return outp;
        }

        static void FlattenInto(object v, string path, Dictionary<string, string> outp)
        {
            if (v is Dictionary<string, object> dict)
            {
                if (IsPropertyEffect(dict)) { outp[path] = FormatPropertyEffect(dict); return; }
                bool rpn = HasRpn(dict);
                if (rpn) outp[path.Length == 0 ? "formula" : path + ".formula"] = FormatRpn(dict);
                var kept = dict.Where(kv => !VolatileKeys.Contains(kv.Key) && kv.Key != "serializationData"
                                            && !(rpn && RpnFields.Contains(kv.Key)))
                               .ToList();
                if (kept.Count == 0) return; // empty object → not a difference
                foreach (var kv in kept)
                    FlattenInto(kv.Value, path.Length == 0 ? kv.Key : path + "." + kv.Key, outp);
            }
            else if (v is List<object> list && list.Any(x => x is Dictionary<string, object>))
            {
                // Pure DatatableElementReference[] → sorted name-set leaf + per-name paths for Mass Change.
                // Guarantees set inequality (15 vs 17 Improvements, Artemis present/absent) is never lost
                // to empty-map / index alignment accidents.
                if (TryFlattenRefNameList(list, path, outp))
                    return;

                var keys = new string[list.Count];
                bool allIndex = true;
                for (int i = 0; i < list.Count; i++)
                {
                    keys[i] = EntryKey(list[i], i);
                    if (!keys[i].StartsWith("#")) allIndex = false;
                }
                // Keyless list: collapse to one row only if every entry is "simple" (scalar, or a dict/list
                // of scalars — e.g. an RPN ConstantStack). If entries are structured (e.g. Effects, whose
                // items hold nested Path/PropertyEffects), align by index and descend so the diff pinpoints
                // the exact changed field instead of dumping the whole list.
                if (allIndex && list.All(IsSimpleEntry)) { outp[path] = Canon(v); return; }
                for (int i = 0; i < list.Count; i++)
                    FlattenInto(list[i], path + "[" + keys[i] + "]", outp);
            }
            else
            {
                var cv = Canon(v);
                if (!IsEmptyCanon(cv)) outp[path] = cv; // empty/null leaf → not a difference
            }
        }

        static bool IsEmptyCanon(string c) =>
            string.IsNullOrEmpty(c) || c == "null" || c == "[]" || c == "{}";

        /// <summary>
        /// True when every dict entry is a DatatableElementReference-shaped
        /// <c>{ serializableElementName: … }</c> (optionally empty name).
        /// </summary>
        static bool TryFlattenRefNameList(List<object> list, string path, Dictionary<string, string> outp)
        {
            if (list == null || list.Count == 0) return false;
            var names = new List<string>();
            foreach (var x in list)
            {
                if (x is not Dictionary<string, object> d) return false;
                // Allow only serializableElementName (ignore Type-only noise).
                if (d.Count == 0) return false;
                if (!d.TryGetValue("serializableElementName", out var nv)) return false;
                foreach (var k in d.Keys)
                    if (k != "serializableElementName") return false;
                string n = nv?.ToString() ?? "";
                if (n.Length > 0) names.Add(n);
            }
            // Stable set leaf — order-independent so reshuffles aren't false conflicts.
            names.Sort(StringComparer.Ordinal);
            outp[path] = names.Count == 0 ? "[]" : string.Join("|", names);
            foreach (var n in names)
                outp[path + "[serializableElementName=" + n + "].serializableElementName"] = n;
            return true;
        }

        // "simple" = holds no nested structure worth pinpointing: a scalar, or a dict/list whose values
        // are all scalars (e.g. {RawValue: N} entries of an RPN ConstantStack).
        static bool IsSimpleEntry(object x)
        {
            if (x is Dictionary<string, object> d) return d.Values.All(IsScalarish);
            if (x is List<object> l) return l.All(IsScalarish);
            return true;
        }
        static bool IsScalarish(object x)
        {
            if (x is Dictionary<string, object>) return false;
            if (x is List<object> l) return l.All(e => !(e is Dictionary<string, object>) && !(e is List<object>));
            return true;
        }

        // ---- PropertyEffect → readable formula (Amplitude.Framework.Simulation.Operation) --------
        // Collapses one PropertyEffect (TargetProperty + operation + ConstantStack/RpnOperationStack)
        // into a single line like "Add MoneyNet = 5" or "Sub ScienceNet = 3 * Target.Population",
        // matching what the descriptor inspector shows — instead of exploding into raw sub-fields.
        static readonly string[] OpNames = { "Add", "Sub", "Mult", "Div", "Percent", "Pow", "Max", "Min",
            "GetTarget", "GetSource", "GetConst", "GetVar", "GetWorld" };
        static readonly string[] OpSym = { "+", "-", "*", "/", "%", "^", "max", "min" };

        // RPN mechanism fields — collapsed into a single "formula" leaf, not shown raw.
        static readonly HashSet<string> RpnFields = new HashSet<string>
        { "RpnOperationStack", "ConstantStack", "PropertyLocalName", "PropertyLocalNameStack",
          "VariableNameStack", "DefinedVariables" };

        static bool IsPropertyEffect(Dictionary<string, object> d) =>
            d.ContainsKey("TargetProperty") && d.ContainsKey("ToTargetOperation");

        // Any object carrying an RPN (a bare RpnDefinition, a cost, etc.) — but not a PropertyEffect,
        // which is handled with its own "op target = formula" wrapper.
        public static bool HasRpn(Dictionary<string, object> d) =>
            d.ContainsKey("RpnOperationStack") || d.ContainsKey("ConstantStack");

        static string FormatPropertyEffect(Dictionary<string, object> d)
        {
            int op = d.TryGetValue("ToTargetOperation", out var o) && int.TryParse(o?.ToString(), out var n) ? n : 0;
            string opName = op >= 0 && op < OpNames.Length ? OpNames[op] : op.ToString();
            string target = d.TryGetValue("TargetProperty", out var t) ? t?.ToString() : "?";
            return $"{opName} {target} = {FormatRpn(d)}";
        }

        static string FormatRpn(Dictionary<string, object> d)
        {
            var ops = ParseRpnOps(d.TryGetValue("RpnOperationStack", out var r) ? r : null);
            var consts = ParseFixedConstants(d.TryGetValue("ConstantStack", out var c) ? c : null);
            var propNames = (d.TryGetValue("PropertyLocalName", out var p1) ? p1
                          : d.TryGetValue("PropertyLocalNameStack", out var p2) ? p2 : null) as List<object>;
            // Bare RpnDefinition uses DefinedVariables for GetVar names; nested RPNs use VariableNameStack.
            var varNames = (d.TryGetValue("VariableNameStack", out var vn) ? vn : null) as List<object>;
            if ((varNames == null || varNames.Count == 0)
                && d.TryGetValue("DefinedVariables", out var dv) && dv is List<object> defined)
                varNames = defined;
            return BuildFormula(ops, consts, propNames, varNames);
        }

        /// <summary>
        /// Live SerializedObject yields <c>Operation[]</c> as a list of int strings; YAML often stores
        /// a hex blob. Either must produce the same op stream for StructDiff.
        /// </summary>
        static List<int> ParseRpnOps(object rpn)
        {
            var ops = new List<int>();
            if (rpn == null) return ops;
            if (rpn is List<object> list)
            {
                foreach (var x in list)
                {
                    if (x == null) continue;
                    if (x is Dictionary<string, object>) continue; // unexpected
                    if (int.TryParse(x.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        ops.Add(n);
                }
                return ops;
            }
            string hex = rpn.ToString();
            if (string.IsNullOrEmpty(hex) || hex.StartsWith("System.", StringComparison.Ordinal)) return ops;
            // 4-byte little-endian ints in hex; op fits in first byte of each word
            for (int i = 0; i + 2 <= hex.Length; i += 8)
                if (int.TryParse(hex.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    ops.Add(b);
            return ops;
        }

        /// <summary>
        /// FixedPoint[] from live SO is list of {RawValue:n}; YAML may use the same or bare ints.
        /// </summary>
        static List<long> ParseFixedConstants(object constants)
        {
            var consts = new List<long>();
            if (constants is not List<object> list) return consts;
            foreach (var c in list)
            {
                if (c is Dictionary<string, object> cd)
                {
                    if (cd.TryGetValue("RawValue", out var rv)
                        && long.TryParse(rv?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
                        consts.Add(raw);
                    continue;
                }
                if (long.TryParse(c?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bare))
                    consts.Add(bare);
            }
            return consts;
        }

        static string BuildFormula(List<int> ops, List<long> consts, List<object> propNames, List<object> varNames)
        {
            var pn = ToStrList(propNames);
            var vn = ToStrList(varNames);

            if (ops == null || ops.Count == 0)
                return consts.Count > 0 ? string.Join(", ", consts.Select(FmtFixed)) : "0";

            var stack = new Stack<string>(); int ci = 0, pi = 0, vi = 0;
            foreach (var op in ops)
            {
                if (op == 10) stack.Push(ci < consts.Count ? FmtFixed(consts[ci++]) : "c" + ci++);
                else if (op == 8) stack.Push("Target." + Nm(pn, pi++));
                else if (op == 9) stack.Push("Source." + Nm(pn, pi++));
                else if (op == 12) stack.Push("World." + Nm(pn, pi++));
                else if (op == 11) stack.Push("Variable." + Nm(vn, vi++));
                else
                {
                    if (stack.Count < 2) { stack.Push("?"); continue; }
                    string b = stack.Pop(), a = stack.Pop();
                    string sym = op >= 0 && op < OpSym.Length ? OpSym[op] : "?";
                    stack.Push("(" + a + " " + sym + " " + b + ")");
                }
            }
            string res = stack.Count > 0 ? stack.Peek() : "?";
            if (res.Length > 1 && res[0] == '(' && res[res.Length - 1] == ')') res = res.Substring(1, res.Length - 2);
            return res;
        }

        static List<string> ToStrList(List<object> l)
        {
            var o = new List<string>();
            if (l != null) foreach (var x in l) if (x is string s) o.Add(s);
            return o;
        }
        static string Nm(List<string> names, int i) => i < names.Count ? names[i] : "p" + i;
        static string FmtFixed(long raw)
        {
            double v = raw / 1000.0;
            return v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture)
                                      : v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string EntryKey(object entry, int idx)
        {
            if (entry is Dictionary<string, object> d)
                foreach (var k in EntryKeys)
                    if (d.TryGetValue(k, out var val) && val != null && val.ToString().Length > 0)
                        return k + "=" + val;
            return "#" + idx.ToString(CultureInfo.InvariantCulture);
        }

        // ---- Canonical string (stable, order-independent for maps) ---------
        public static string Canon(object v)
        {
            var sb = new StringBuilder();
            CanonInto(v, sb);
            return sb.ToString();
        }

        static void CanonInto(object v, StringBuilder sb)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is Dictionary<string, object> d)
            {
                sb.Append('{');
                bool first = true;
                foreach (var k in d.Keys.OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(k).Append(':');
                    CanonInto(d[k], sb);
                }
                sb.Append('}');
            }
            else if (v is List<object> l)
            {
                sb.Append('[');
                for (int i = 0; i < l.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    CanonInto(l[i], sb);
                }
                sb.Append(']');
            }
            else sb.Append(v.ToString());
        }

        // ---- Reference + odin helpers (work on raw doc text) ---------------
        static readonly Regex RefClean = new Regex(@"serializableElementName:\s*(\S+)", RegexOptions.Compiled);
        static readonly Regex NodeNameRef = new Regex(@"^\s*-?\s*Name:\s*serializableElementName\s*$", RegexOptions.Compiled);
        static readonly Regex NodeData = new Regex(@"^\s*Data:\s*(.+?)\s*$", RegexOptions.Compiled);

        public static HashSet<string> CollectRefs(IEnumerable<string> docLines)
        {
            var refs = new HashSet<string>();
            bool expect = false;
            foreach (var l in docLines)
            {
                var m = RefClean.Match(l);
                if (m.Success) refs.Add(m.Groups[1].Value);
                if (NodeNameRef.IsMatch(l)) { expect = true; continue; }
                if (expect)
                {
                    var dm = NodeData.Match(l);
                    if (dm.Success) { refs.Add(dm.Groups[1].Value); expect = false; }
                }
            }
            return refs;
        }

        /// <summary>
        /// True when the object carries no gameplay fields — only m_* bookkeeping — i.e. it is a
        /// collection/container root, not a real element. (serializationData counts as payload,
        /// so an Odin element is not mistaken for a container.)
        /// </summary>
        public static bool IsContainerBody(Dictionary<string, object> body)
        {
            return body != null && !body.Keys.Any(k => !VolatileKeys.Contains(k));
        }

        /// <summary>True when the element's real payload lives in the Odin node stream, not plain fields.</summary>
        public static bool IsOdinPayload(Dictionary<string, object> body)
        {
            if (body == null) return false;
            if (!body.TryGetValue("serializationData", out var sdObj) || sdObj is not Dictionary<string, object> sd)
                return false;
            bool Has(string k) => sd.TryGetValue(k, out var val) && val != null &&
                                  !(val is List<object> lo && lo.Count == 0) && val.ToString().Length > 0;
            return Has("SerializationNodes") || Has("SerializedBytesString") || Has("SerializedBytes");
        }

        /// <summary>
        /// True when <paramref name="body"/> has at least one Unity-serialized gameplay field
        /// (not only <c>serializationData</c> / volatile keys). Hybrid Odin+Unity types (many units,
        /// techs) should StructDiff these fields — do not treat them as opaque RefDiff shells.
        /// </summary>
        public static bool HasGameplayKeys(Dictionary<string, object> body)
        {
            if (body == null || body.Count == 0) return false;
            foreach (var k in body.Keys)
            {
                if (VolatileKeys.Contains(k)) continue;
                if (k == "serializationData") continue;
                return true;
            }
            return false;
        }
    }
}
