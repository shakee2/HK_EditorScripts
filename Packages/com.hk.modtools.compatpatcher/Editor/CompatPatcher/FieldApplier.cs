using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Applies field-level Mass Change mutations to existing Patch ScriptableObjects via
    /// <see cref="SerializedObject"/> only (never assigns refs from staged/LiveObject sources).
    /// Success requires a verified read-back of the written value.
    /// </summary>
    public static class FieldApplier
    {
        static readonly Regex BracketSegRe = new Regex(
            @"^(?<name>[\w]+)(?:\[(?<key>[^=\]]+)=(?<val>[^\]]+)\])?$",
            RegexOptions.Compiled);

        public enum ApplyKind
        {
            Unsupported,
            AddRef,
            SetRef,
            SetLeaf,
        }

        public struct ParsedPath
        {
            public ApplyKind Kind;
            public string[] ParentSegments; // dotted Generic walk before leaf/array
            public string LeafOrArrayName;
            public string EntryKeyField;    // e.g. serializableElementName (AddRef)
            public string EntryName;        // AddRef / SetRef value
            public string UnsupportedReason;
        }

        public struct ApplyResult
        {
            public bool Ok;
            public bool Skipped;   // already had value / no-op
            public string Message;
        }

        /// <summary>
        /// Classify a Flatten diff path for Mass Change. Prefer MissingInWinner + bracket key for AddRef.
        /// </summary>
        public static ParsedPath Parse(string path, DiffKind kind, string sourceValue)
        {
            var result = new ParsedPath
            {
                Kind = ApplyKind.Unsupported,
                ParentSegments = Array.Empty<string>(),
                UnsupportedReason = "empty path",
            };
            if (string.IsNullOrEmpty(path)) return result;
            if (path.StartsWith("refs[", StringComparison.Ordinal))
            {
                result.UnsupportedReason = "Odin refs[] path — no field location";
                return result;
            }

            var rawSegs = SplitDotted(path);
            if (rawSegs.Length == 0)
            {
                result.UnsupportedReason = "could not split path";
                return result;
            }

            var parents = new List<string>();
            string leafName = null;
            string keyField = null;
            string keyVal = null;
            for (int i = 0; i < rawSegs.Length; i++)
            {
                var m = BracketSegRe.Match(rawSegs[i]);
                if (!m.Success)
                {
                    result.UnsupportedReason = "bad path segment: " + rawSegs[i];
                    return result;
                }
                string name = m.Groups["name"].Value;
                bool hasBracket = m.Groups["key"].Success && m.Groups["key"].Length > 0;
                if (i < rawSegs.Length - 1)
                {
                    if (hasBracket)
                    {
                        result.UnsupportedReason = "nested bracket key mid-path not supported: " + rawSegs[i];
                        return result;
                    }
                    parents.Add(name);
                }
                else
                {
                    leafName = name;
                    if (hasBracket)
                    {
                        keyField = m.Groups["key"].Value;
                        keyVal = m.Groups["val"].Value;
                    }
                }
            }

            result.ParentSegments = parents.ToArray();
            result.LeafOrArrayName = leafName;

            if (!string.IsNullOrEmpty(keyField) && keyField == "serializableElementName"
                && !string.IsNullOrEmpty(keyVal)
                && (kind == DiffKind.MissingInWinner || kind == DiffKind.Changed))
            {
                result.Kind = ApplyKind.AddRef;
                result.EntryKeyField = keyField;
                result.EntryName = keyVal;
                result.UnsupportedReason = null;
                return result;
            }

            if (!string.IsNullOrEmpty(keyField))
            {
                result.UnsupportedReason = "unsupported bracket key '" + keyField + "' (only serializableElementName)";
                return result;
            }

            // Leaf path: SetRef if source value is a non-empty name and property is a DatatableElementReference;
            // else SetLeaf for simple scalars. Final kind decided at apply time when we see the property.
            result.Kind = ApplyKind.SetLeaf; // may promote to SetRef in Apply
            result.EntryName = sourceValue;
            result.UnsupportedReason = null;
            if (kind == DiffKind.ExtraInWinner)
            {
                result.Kind = ApplyKind.Unsupported;
                result.UnsupportedReason = "winner-only diffs are informational";
            }
            return result;
        }

        public static bool IsSupported(ParsedPath parsed) =>
            parsed.Kind == ApplyKind.AddRef || parsed.Kind == ApplyKind.SetLeaf || parsed.Kind == ApplyKind.SetRef;

        /// <summary>
        /// Apply one field change to an existing Patch element. Does not import.
        /// </summary>
        public static ApplyResult Apply(UnityEngine.Object patchObj, string path, DiffKind kind, string sourceValue)
        {
            if (patchObj == null)
                return Fail("no Patch object");

            var parsed = Parse(path, kind, sourceValue);
            if (!IsSupported(parsed))
                return Fail(parsed.UnsupportedReason ?? "unsupported");

            var so = new SerializedObject(patchObj);
            so.Update();

            if (parsed.Kind == ApplyKind.AddRef)
                return ApplyAddRef(so, patchObj, parsed);

            // Leaf: detect DatatableElementReference vs scalar
            var leaf = FindProperty(so, parsed.ParentSegments, parsed.LeafOrArrayName);
            if (leaf == null)
                return Fail("property not found: " + path);

            var nameProp = leaf.FindPropertyRelative("serializableElementName");
            if (nameProp != null && nameProp.propertyType == SerializedPropertyType.String)
            {
                string want = ExtractRefName(sourceValue, parsed.EntryName);
                if (string.IsNullOrEmpty(want) || want == UnityYaml.Missing)
                    return Fail("no source ref name to set");
                if (nameProp.stringValue == want)
                {
                    return new ApplyResult { Ok = true, Skipped = true, Message = "already set" };
                }
                nameProp.stringValue = want;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(patchObj);
                so.Update();
                var check = FindProperty(so, parsed.ParentSegments, parsed.LeafOrArrayName)
                    ?.FindPropertyRelative("serializableElementName");
                if (check == null || check.stringValue != want)
                    return Fail("read-back failed after SetRef");
                return new ApplyResult { Ok = true, Message = "set ref " + want };
            }

            return ApplySetLeaf(so, patchObj, leaf, sourceValue, path);
        }

        static ApplyResult ApplyAddRef(SerializedObject so, UnityEngine.Object patchObj, ParsedPath parsed)
        {
            var arr = FindProperty(so, parsed.ParentSegments, parsed.LeafOrArrayName);
            if (arr == null || !arr.isArray)
                return Fail("array not found: " + string.Join(".", parsed.ParentSegments) + "." + parsed.LeafOrArrayName);

            string want = parsed.EntryName;
            for (int i = 0; i < arr.arraySize; i++)
            {
                var el = arr.GetArrayElementAtIndex(i);
                var np = el.FindPropertyRelative("serializableElementName");
                if (np != null && np.stringValue == want)
                    return new ApplyResult { Ok = true, Skipped = true, Message = "already present" };
            }

            int idx = arr.arraySize;
            arr.arraySize = idx + 1;
            var neu = arr.GetArrayElementAtIndex(idx);
            var nameProp = neu.FindPropertyRelative("serializableElementName");
            if (nameProp == null || nameProp.propertyType != SerializedPropertyType.String)
            {
                arr.arraySize = idx; // rollback empty/unsupported row
                so.ApplyModifiedPropertiesWithoutUndo();
                return Fail("array element has no serializableElementName (not a DatatableElementReference[])");
            }
            nameProp.stringValue = want;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(patchObj);

            // Verify
            so.Update();
            arr = FindProperty(so, parsed.ParentSegments, parsed.LeafOrArrayName);
            bool found = false;
            if (arr != null && arr.isArray)
            {
                for (int i = 0; i < arr.arraySize; i++)
                {
                    var np = arr.GetArrayElementAtIndex(i).FindPropertyRelative("serializableElementName");
                    if (np != null && np.stringValue == want) { found = true; break; }
                }
            }
            if (!found)
                return Fail("read-back failed after AddRef");
            return new ApplyResult { Ok = true, Message = "added " + want };
        }

        static ApplyResult ApplySetLeaf(SerializedObject so, UnityEngine.Object patchObj,
            SerializedProperty leaf, string sourceValue, string path)
        {
            if (sourceValue == null || sourceValue == UnityYaml.Missing)
                return Fail("no source value");

            if (!TryAssignLeaf(leaf, sourceValue, out string err))
                return Fail(err ?? "unsupported leaf type at " + path);

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(patchObj);
            so.Update();

            // Re-find and compare canon
            var check = FindProperty(so, SplitParents(path), LeafName(path));
            if (check == null)
                return Fail("read-back: property missing");
            if (!LeafMatches(check, sourceValue))
                return Fail("read-back mismatch at " + path);
            return new ApplyResult { Ok = true, Message = "set leaf" };
        }

        static bool TryAssignLeaf(SerializedProperty prop, string value, out string error)
        {
            error = null;
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
                        { error = "not an int: " + value; return false; }
                        prop.intValue = iv;
                        return true;
                    case SerializedPropertyType.Boolean:
                        prop.boolValue = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        return true;
                    case SerializedPropertyType.Float:
                        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv))
                        { error = "not a float: " + value; return false; }
                        prop.floatValue = fv;
                        return true;
                    case SerializedPropertyType.String:
                        prop.stringValue = value;
                        return true;
                    case SerializedPropertyType.Enum:
                        // Prefer underlying int (flags masks + non-contiguous values). Fall back to name.
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mask))
                        {
                            prop.intValue = mask;
                            return true;
                        }
                        for (int i = 0; i < prop.enumNames.Length; i++)
                        {
                            if (prop.enumNames[i] == value)
                            {
                                prop.enumValueIndex = i;
                                return true;
                            }
                        }
                        error = "enum value not found: " + value;
                        return false;
                    case SerializedPropertyType.Character:
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cv))
                        { error = "not a char code: " + value; return false; }
                        prop.intValue = cv;
                        return true;
                    default:
                        error = "unsupported property type " + prop.propertyType;
                        return false;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        static bool LeafMatches(SerializedProperty prop, string expected)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue.ToString(CultureInfo.InvariantCulture) == expected
                           || (int.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv)
                               && prop.intValue == iv);
                case SerializedPropertyType.Boolean:
                    bool want = expected == "1" || expected.Equals("true", StringComparison.OrdinalIgnoreCase);
                    return prop.boolValue == want;
                case SerializedPropertyType.Float:
                    if (!float.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv))
                        return false;
                    return Mathf.Abs(prop.floatValue - fv) < 0.0005f;
                case SerializedPropertyType.String:
                    return (prop.stringValue ?? "") == expected;
                case SerializedPropertyType.Enum:
                    if (int.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mask))
                        return prop.intValue == mask;
                    return prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumNames.Length
                           && prop.enumNames[prop.enumValueIndex] == expected;
                case SerializedPropertyType.Character:
                    return prop.intValue.ToString(CultureInfo.InvariantCulture) == expected;
                default:
                    return false;
            }
        }

        static SerializedProperty FindProperty(SerializedObject so, string[] parents, string leafName)
        {
            if (string.IsNullOrEmpty(leafName)) return null;
            SerializedProperty prop;
            if (parents == null || parents.Length == 0)
            {
                prop = so.FindProperty(leafName);
                return prop;
            }
            prop = so.FindProperty(parents[0]);
            if (prop == null) return null;
            for (int i = 1; i < parents.Length; i++)
            {
                prop = prop.FindPropertyRelative(parents[i]);
                if (prop == null) return null;
            }
            return prop.FindPropertyRelative(leafName);
        }

        static string[] SplitDotted(string path)
        {
            // Split on '.' that are not inside brackets
            var parts = new List<string>();
            int start = 0;
            int depth = 0;
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (c == '[') depth++;
                else if (c == ']') depth = Math.Max(0, depth - 1);
                else if (c == '.' && depth == 0)
                {
                    parts.Add(path.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < path.Length) parts.Add(path.Substring(start));
            return parts.ToArray();
        }

        static string[] SplitParents(string path)
        {
            var segs = SplitDotted(path);
            if (segs.Length <= 1) return Array.Empty<string>();
            var parents = new string[segs.Length - 1];
            for (int i = 0; i < parents.Length; i++)
            {
                var m = BracketSegRe.Match(segs[i]);
                parents[i] = m.Success ? m.Groups["name"].Value : segs[i];
            }
            return parents;
        }

        static string LeafName(string path)
        {
            var segs = SplitDotted(path);
            if (segs.Length == 0) return null;
            var m = BracketSegRe.Match(segs[segs.Length - 1]);
            return m.Success ? m.Groups["name"].Value : segs[segs.Length - 1];
        }

        /// <summary>
        /// Diff values for a single ref leaf may be the bare name, or a collapsed dict canon.
        /// Prefer the explicit entry name from a bracket path when present.
        /// </summary>
        static string ExtractRefName(string sourceValue, string parsedEntryName)
        {
            if (!string.IsNullOrEmpty(parsedEntryName) && parsedEntryName != UnityYaml.Missing)
                return parsedEntryName;
            if (string.IsNullOrEmpty(sourceValue) || sourceValue == UnityYaml.Missing)
                return null;
            // Canon of {serializableElementName:X} is like {serializableElementName:X} — take after last ':'
            if (sourceValue.StartsWith("{", StringComparison.Ordinal) && sourceValue.Contains("serializableElementName"))
            {
                int idx = sourceValue.IndexOf("serializableElementName:", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    string rest = sourceValue.Substring(idx + "serializableElementName:".Length).Trim();
                    int end = rest.IndexOfAny(new[] { ',', '}' });
                    return end >= 0 ? rest.Substring(0, end).Trim() : rest.Trim();
                }
            }
            return sourceValue;
        }

        static ApplyResult Fail(string msg) => new ApplyResult { Ok = false, Message = msg };

        /// <summary>
        /// Load the Patch SO for an element name (and optional type hint). Returns null if not in Patch.
        /// </summary>
        public static UnityEngine.Object FindPatchObject(string elementName, string typeHint,
            IReadOnlyDictionary<string, string> patchPathByName)
        {
            if (string.IsNullOrEmpty(elementName)) return null;
            string path = null;
            if (patchPathByName != null && patchPathByName.TryGetValue(elementName, out var p))
                path = p;
            if (string.IsNullOrEmpty(path))
            {
                // Fallback scan
                if (!System.IO.Directory.Exists(PatchBuilder.PatchDir)) return null;
                foreach (var f in System.IO.Directory.EnumerateFiles(PatchBuilder.PatchDir, "*.asset",
                             System.IO.SearchOption.AllDirectories))
                {
                    foreach (var o in AssetDatabase.LoadAllAssetsAtPath(f.Replace('\\', '/')))
                    {
                        if (o != null && o.name == elementName && o is Amplitude.Framework.IDatatableElement)
                        {
                            if (string.IsNullOrEmpty(typeHint) || o.GetType().Name == typeHint
                                || System.IO.Path.GetFileNameWithoutExtension(f).IndexOf(typeHint ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                                return o;
                        }
                    }
                }
                return null;
            }

            UnityEngine.Object best = null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (o == null || o.name != elementName || !(o is Amplitude.Framework.IDatatableElement)) continue;
                if (!string.IsNullOrEmpty(typeHint) && o.GetType().Name == typeHint)
                    return o;
                best ??= o;
            }
            return best;
        }
    }
}
