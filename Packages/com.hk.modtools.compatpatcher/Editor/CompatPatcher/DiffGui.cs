using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Shared IMGUI rendering for the per-element diff list. Used by both the main window's detail
    /// panel and the side-by-side Compare window so the two stay in lockstep.
    /// </summary>
    public static class DiffGui
    {
        // Foldout open-state for aggregated list-entry groups (path prefix → open).
        static readonly Dictionary<string, bool> s_groupOpen = new Dictionary<string, bool>();

        // First list-entry segment: SimulationEventEffects[EffectId=…] or Path[key=…]
        static readonly Regex ListEntryRe = new Regex(@"^(.+?\[[^\]]+\])", RegexOptions.Compiled);

        // Prefer the effect list entry so Hidden + GainValues.*.Importance share one card.
        static readonly Regex EffectEntryRe = new Regex(
            @"^((?:.*\.)?(?:SimulationEventEffects|Effects|EffectByLevels|NarrativeEventEffects)\[[^\]]+\])",
            RegexOptions.Compiled);

        /// <param name="unlockIndex">
        /// Optional: reverse unlock map + vanilla/mod tags for UnlockConstructible groups.
        /// </param>
        /// <param name="currentElementName">
        /// Optional: the tech/civic under review (for “only on this tech” / move annotations).
        /// </param>
        /// <param name="winnerRefsOnElement">
        /// Optional: classifiable ref names on the winner's version of this element. Used to
        /// relabel MISSING unlock EffectIds as redesigned when units were merged/moved within the tech.
        /// </param>
        public static void DrawTable(
            IEnumerable<Diff> diffs,
            string winner,
            bool odin,
            Action<Diff, string> onApplyPattern = null,
            Func<Diff, int> countInPatchForPath = null,
            bool hideExtraInWinner = false,
            Action<Diff> onResolveDiff = null,
            bool hideResolved = true,
            UnlockCarrierIndex unlockIndex = null,
            string currentElementName = null,
            HashSet<string> winnerRefsOnElement = null)
        {
            var raw = diffs?.ToList() ?? new List<Diff>();
            int winnerOnly = raw.Count(d => d.Kind == DiffKind.ExtraInWinner);
            int resolvedCount = raw.Count(d => d.Status == "carried");
            var list = raw.AsEnumerable();
            if (hideExtraInWinner)
                list = list.Where(d => d.Kind != DiffKind.ExtraInWinner);
            if (hideResolved)
                list = list.Where(d => d.Status != "carried");
            var visible = list.ToList();
            int diffCount = visible.Count;

            EditorGUILayout.LabelField(odin
                ? $"Differences — {diffCount} — Odin element (no Flatten body): references only, use Compare side-by-side for full fields:"
                : (onApplyPattern != null
                    ? $"Differences — {diffCount} (Mass Change available on supported rows):"
                    : $"Differences — {diffCount}:"),
                EditorStyles.miniBoldLabel);
            if (hideExtraInWinner && winnerOnly > 0)
            {
                EditorGUILayout.LabelField(
                    $"{winnerOnly} winner-only row(s) hidden (already in {winner} — not actionable).",
                    EditorStyles.miniLabel);
            }
            if (hideResolved && resolvedCount > 0)
            {
                // Parent owns the Show-resolved toggle; keep a quiet count here only when hidden.
                EditorGUILayout.LabelField(
                    $"{resolvedCount} resolved difference(s) hidden.",
                    EditorStyles.miniLabel);
            }
            if (diffCount == 0)
            {
                string empty = "No differences.";
                if (hideResolved && resolvedCount > 0)
                    empty = "No open differences (resolved rows hidden).";
                else if (hideExtraInWinner && winnerOnly > 0)
                    empty = "No actionable differences (winner-only rows hidden).";
                EditorGUILayout.LabelField(empty, EditorStyles.miniLabel);
                return;
            }

            foreach (var group in GroupForDisplay(visible))
            {
                bool redesigned = IsRedesignedMissingGroup(group, winnerRefsOnElement);
                if (group.Items.Count == 1 && string.IsNullOrEmpty(group.Items[0].Leaf))
                {
                    DrawSingleDiff(group.Items[0].Diff, winner, onApplyPattern, countInPatchForPath,
                        onResolveDiff, unlockIndex, currentElementName, winnerRefsOnElement);
                    continue;
                }

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                DrawGroupHeader(group, winner, onResolveDiff, redesigned);
                if (!s_groupOpen.TryGetValue(group.Key, out bool open)) open = true;
                if (open)
                {
                    DrawUnlockAnnotations(group.Key, unlockIndex, currentElementName, redesigned);
                    foreach (var item in group.Items)
                    {
                        EditorGUI.indentLevel++;
                        DrawGroupedLeaf(item, winner, onApplyPattern, countInPatchForPath, onResolveDiff,
                            unlockIndex, currentElementName);
                        EditorGUI.indentLevel--;
                    }
                }
                EditorGUILayout.EndVertical();
            }
        }

        static bool IsRedesignedMissingGroup(DiffGroup group, HashSet<string> winnerRefsOnElement)
        {
            if (group.Kind != DiffKind.MissingInWinner) return false;
            if (!UnlockCarrierIndex.TryGetEffectIdValue(group.Key, out string idVal)) return false;
            return UnlockCarrierIndex.IsRedesignedMissing(idVal, winnerRefsOnElement);
        }

        static void DrawUnlockAnnotations(
            string groupKey,
            UnlockCarrierIndex index,
            string currentElementName,
            bool redesignedMissing)
        {
            if (redesignedMissing)
            {
                EditorGUILayout.LabelField(
                    "Still on winner — merged/moved into another unlock on this tech (not deleted).",
                    EditorStyles.miniLabel);
            }

            if (index == null) return;
            if (!UnlockCarrierIndex.TryGetEffectIdValue(groupKey, out string idVal)) return;
            if (!UnlockCarrierIndex.IsClassifiableEffectId(idVal)) return;

            var names = UnlockCarrierIndex.ParseUnlockNamesFromEffectIdValue(idVal);
            if (names.Count == 0) return;

            string split = index.FormatVanillaModSplit(names);
            if (!string.IsNullOrEmpty(split))
                EditorGUILayout.LabelField(split, EditorStyles.miniLabel);

            foreach (var line in index.FormatInterestingAnnotations(names, currentElementName))
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
        }

        struct DiffItem
        {
            public Diff Diff;
            public string Leaf; // property path under the group, or "" if the diff is the entry itself
        }

        struct DiffGroup
        {
            public string Key;
            public string Title;
            public DiffKind Kind;
            public List<DiffItem> Items;
        }

        static List<DiffGroup> GroupForDisplay(List<Diff> visible)
        {
            // Preserve first-seen order of groups (StructDiff path order).
            var order = new List<string>();
            var map = new Dictionary<string, DiffGroup>(StringComparer.Ordinal);
            foreach (var d in visible)
            {
                string path = d.Path ?? "";
                string key = GroupKey(path);
                string leaf = LeafUnder(path, key);
                if (!map.TryGetValue(key, out var g))
                {
                    g = new DiffGroup
                    {
                        Key = key,
                        Title = FriendlyGroupTitle(key),
                        Kind = d.Kind,
                        Items = new List<DiffItem>(),
                    };
                    map[key] = g;
                    order.Add(key);
                }
                // Mixed kinds under one entry: prefer CHANGED label if any leaf is Changed.
                if (d.Kind == DiffKind.Changed) g.Kind = DiffKind.Changed;
                else if (g.Kind != DiffKind.Changed && d.Kind == DiffKind.MissingInWinner)
                    g.Kind = DiffKind.MissingInWinner;
                g.Items.Add(new DiffItem { Diff = d, Leaf = leaf });
            }
            return order.Select(k => map[k]).ToList();
        }

        static string GroupKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var effect = EffectEntryRe.Match(path);
            if (effect.Success) return effect.Groups[1].Value;
            var m = ListEntryRe.Match(path);
            return m.Success ? m.Groups[1].Value : path;
        }

        static string LeafUnder(string path, string groupKey)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(groupKey)) return path ?? "";
            if (path.Length > groupKey.Length && path.StartsWith(groupKey, StringComparison.Ordinal)
                && path[groupKey.Length] == '.')
                return path.Substring(groupKey.Length + 1);
            if (path == groupKey) return "";
            return path;
        }

        /// <summary>
        /// <c>SimulationEventEffects[EffectId=ApplyDescriptor|Empire|Ref]</c> →
        /// <c>SimulationEventEffects · ApplyDescriptor · Ref</c> (skip empty / ubiquitous Empire).
        /// </summary>
        static string FriendlyGroupTitle(string groupKey)
        {
            if (string.IsNullOrEmpty(groupKey)) return "?";

            // Prefer the EffectId=… bracket (may be nested under Choices/Loots).
            int effectIdAt = groupKey.LastIndexOf("[EffectId=", StringComparison.Ordinal);
            if (effectIdAt >= 0)
            {
                int rb = groupKey.IndexOf(']', effectIdAt);
                if (rb > effectIdAt)
                {
                    string listName = "SimulationEventEffects";
                    int listStart = groupKey.LastIndexOf('.', effectIdAt);
                    if (listStart >= 0 && listStart < effectIdAt)
                        listName = groupKey.Substring(listStart + 1, effectIdAt - listStart - 1);
                    else if (effectIdAt > 0)
                        listName = groupKey.Substring(0, effectIdAt);

                    string value = groupKey.Substring(effectIdAt + "[EffectId=".Length,
                        rb - (effectIdAt + "[EffectId=".Length));
                    var parts = value.Split('|');
                    var bits = new List<string> { listName };
                    if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0]))
                        bits.Add(parts[0]); // ApplyDescriptor / UnlockConstructible
                    // parts[1] = TargetID (usually Empire) — skip when unlock refs exist.
                    // parts[2] = ConstructibleReferences / Descriptor / … (the real identity).
                    bool haveUnlockRefs = false;
                    for (int i = 2; i < parts.Length; i++)
                    {
                        if (string.IsNullOrEmpty(parts[i])) continue;
                        if (int.TryParse(parts[i], out _)) continue;
                        // Expand A+B unlock lists into separate title bits.
                        foreach (var piece in parts[i].Split('+'))
                        {
                            if (string.IsNullOrEmpty(piece)) continue;
                            bits.Add(piece);
                            haveUnlockRefs = true;
                        }
                    }
                    if (!haveUnlockRefs && parts.Length > 1 && !string.IsNullOrEmpty(parts[1]))
                        bits.Add(parts[1]);
                    return string.Join(" · ", bits);
                }
            }

            int lb = groupKey.LastIndexOf('[');
            int rb2 = groupKey.LastIndexOf(']');
            if (lb < 0 || rb2 <= lb) return groupKey;

            string prefix = groupKey.Substring(0, lb);
            int dot = prefix.LastIndexOf('.');
            string list = dot >= 0 ? prefix.Substring(dot + 1) : prefix;
            string inside = groupKey.Substring(lb + 1, rb2 - lb - 1);
            int eq = inside.IndexOf('=');
            if (eq <= 0) return groupKey;
            return list + " · " + inside.Substring(eq + 1);
        }

        static void DrawGroupHeader(DiffGroup group, string winner, Action<Diff> onResolveDiff, bool redesignedMissing)
        {
            EditorGUILayout.BeginHorizontal();
            if (!s_groupOpen.TryGetValue(group.Key, out bool open)) open = true;
            open = EditorGUILayout.Foldout(open,
                KindTag(group.Kind, winner, redesignedMissing) + "   " + group.Title
                + (group.Items.Count > 1 ? $"  ({group.Items.Count} fields)" : ""),
                true);
            s_groupOpen[group.Key] = open;

            // Resolve-all for open groups when every leaf is unresolved.
            if (onResolveDiff != null)
            {
                var pending = group.Items.Where(i => i.Diff.Status != "carried").ToList();
                if (pending.Count > 1)
                {
                    if (GUILayout.Button(new GUIContent("Resolve all", "Dismiss every field under this entry"),
                            EditorStyles.miniButton, GUILayout.Width(80)))
                    {
                        foreach (var i in pending) onResolveDiff(i.Diff);
                    }
                }
                else if (pending.Count == 1)
                {
                    if (GUILayout.Button(new GUIContent("Resolve", "Dismiss this difference (saved to sidecar)"),
                            EditorStyles.miniButton, GUILayout.Width(64)))
                        onResolveDiff(pending[0].Diff);
                }
                else
                {
                    EditorGUILayout.LabelField("resolved", EditorStyles.miniLabel, GUILayout.Width(64));
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        static void DrawGroupedLeaf(
            DiffItem item,
            string winner,
            Action<Diff, string> onApplyPattern,
            Func<Diff, int> countInPatchForPath,
            Action<Diff> onResolveDiff,
            UnlockCarrierIndex unlockIndex,
            string currentElementName)
        {
            var d = item.Diff;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            string leafLabel = string.IsNullOrEmpty(item.Leaf) ? "(entry)" : item.Leaf;
            if (unlockIndex != null)
                leafLabel = TagConstructibleLeaf(leafLabel, unlockIndex);
            EditorGUILayout.LabelField(leafLabel, EditorStyles.miniBoldLabel);
            if (onResolveDiff != null && d.Status != "carried")
            {
                if (GUILayout.Button(new GUIContent("Resolve", "Dismiss this difference (saved to sidecar)"),
                        EditorStyles.miniButton, GUILayout.Width(64)))
                    onResolveDiff(d);
            }
            else if (d.Status == "carried")
            {
                EditorGUILayout.LabelField("resolved", EditorStyles.miniLabel, GUILayout.Width(64));
            }
            EditorGUILayout.EndHorizontal();

            DrawValueRows(d, winner, unlockIndex, currentElementName);
            DrawMassChangeRow(d, onApplyPattern, countInPatchForPath);
            EditorGUILayout.EndVertical();
        }

        static void DrawSingleDiff(
            Diff d,
            string winner,
            Action<Diff, string> onApplyPattern,
            Func<Diff, int> countInPatchForPath,
            Action<Diff> onResolveDiff,
            UnlockCarrierIndex unlockIndex,
            string currentElementName,
            HashSet<string> winnerRefsOnElement)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            string titlePath = d.Path ?? "";
            string gk = GroupKey(titlePath);
            string shown = FriendlyGroupTitle(gk);
            bool redesigned = d.Kind == DiffKind.MissingInWinner
                && UnlockCarrierIndex.TryGetEffectIdValue(gk, out string idVal)
                && UnlockCarrierIndex.IsRedesignedMissing(idVal, winnerRefsOnElement);
            if (shown != gk || titlePath == gk)
                EditorGUILayout.LabelField($"{KindTag(d.Kind, winner, redesigned)}   {shown}", EditorStyles.miniBoldLabel);
            else
                EditorGUILayout.LabelField($"{KindTag(d.Kind, winner, redesigned)}   {titlePath}", EditorStyles.miniBoldLabel);
            if (onResolveDiff != null && d.Status != "carried")
            {
                if (GUILayout.Button(new GUIContent("Resolve", "Dismiss this difference (saved to sidecar)"),
                        EditorStyles.miniButton, GUILayout.Width(64)))
                    onResolveDiff(d);
            }
            else if (d.Status == "carried")
            {
                EditorGUILayout.LabelField("resolved", EditorStyles.miniLabel, GUILayout.Width(64));
            }
            EditorGUILayout.EndHorizontal();
            DrawUnlockAnnotations(gk, unlockIndex, currentElementName, redesigned);
            DrawValueRows(d, winner, unlockIndex, currentElementName);
            DrawMassChangeRow(d, onApplyPattern, countInPatchForPath);
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// <c>ConstructibleReferences[serializableElementName=LandUnit_X]</c> → append [vanilla]/[mod].
        /// </summary>
        static string TagConstructibleLeaf(string leaf, UnlockCarrierIndex index)
        {
            if (string.IsNullOrEmpty(leaf) || index == null) return leaf;
            // …[serializableElementName=Name] or bare name after =
            const string marker = "serializableElementName=";
            int at = leaf.IndexOf(marker, StringComparison.Ordinal);
            string name = null;
            if (at >= 0)
            {
                int start = at + marker.Length;
                int end = leaf.IndexOf(']', start);
                name = end > start ? leaf.Substring(start, end - start) : leaf.Substring(start);
            }
            else if (leaf == "Descriptor" || leaf.EndsWith(".Descriptor", StringComparison.Ordinal)
                     || leaf == "CostModifierReference" || leaf.EndsWith(".CostModifierReference", StringComparison.Ordinal))
            {
                // Leaf is the field itself; value rows carry the name — don't retag the label.
                return leaf;
            }
            if (string.IsNullOrEmpty(name)) return leaf;
            return leaf + "  [" + index.KindTag(name) + "]";
        }

        static string KindTag(DiffKind kind, string winner, bool redesignedMissing = false) =>
            kind == DiffKind.ExtraInWinner ? $"ONLY in winner ({winner})"
            : kind == DiffKind.MissingInWinner
                ? (redesignedMissing ? "REDESIGNED in winner (merged/moved)" : "MISSING in winner")
            : "CHANGED";

        static void DrawValueRows(
            Diff d,
            string winner,
            UnlockCarrierIndex unlockIndex = null,
            string currentElementName = null)
        {
            bool pathLooksUnlockRef = d.Path != null
                && (d.Path.IndexOf("ConstructibleReferences", StringComparison.Ordinal) >= 0
                    || d.Path.IndexOf("ResourceReferences", StringComparison.Ordinal) >= 0
                    || d.Path.IndexOf("CostModifierReference", StringComparison.Ordinal) >= 0
                    || d.Path.IndexOf("serializableElementName=", StringComparison.Ordinal) >= 0
                    || d.Path.EndsWith(".Descriptor", StringComparison.Ordinal)
                    || d.Path.IndexOf(".Descriptor.", StringComparison.Ordinal) >= 0);
            foreach (var kv in d.Values.OrderBy(k => k.Key == "*winner*" ? "" : k.Key))
            {
                string who = kv.Key == "*winner*" ? winner + " (winner)" : kv.Key;
                string display = Short(kv.Value);
                bool isWinnerRow = kv.Key == "*winner*"
                    || (!string.IsNullOrEmpty(winner)
                        && string.Equals(kv.Key, winner, StringComparison.Ordinal));
                if (unlockIndex != null && isWinnerRow && kv.Value == UnityYaml.Missing)
                {
                    string redirect = unlockIndex.FormatAbsentUnlockRedirect(
                        d.Path, winner, currentElementName);
                    if (!string.IsNullOrEmpty(redirect))
                        display = redirect;
                }
                else if (unlockIndex != null && pathLooksUnlockRef
                    && kv.Value != null && kv.Value != UnityYaml.Missing
                    && LooksLikeUnlockElementName(kv.Value))
                    display = Short(kv.Value) + "  [" + unlockIndex.KindTag(kv.Value) + "]";
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(who, GUILayout.Width(150));
                EditorGUILayout.SelectableLabel(display, EditorStyles.miniLabel,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.EndHorizontal();
            }
        }

        static bool LooksLikeUnlockElementName(string v) =>
            !string.IsNullOrEmpty(v)
            && v.IndexOf('|') < 0
            && v.IndexOf('.') < 0
            && v.IndexOf(' ') < 0
            && !int.TryParse(v, out _);

        static void DrawMassChangeRow(
            Diff d,
            Action<Diff, string> onApplyPattern,
            Func<Diff, int> countInPatchForPath)
        {
            if (onApplyPattern == null || d.Kind == DiffKind.ExtraInWinner) return;
            var src = d.Values.FirstOrDefault(kv => kv.Key != "*winner*" && kv.Value != UnityYaml.Missing);
            string srcVal = src.Value;
            string srcMod = src.Key;
            var parsed = FieldApplier.Parse(d.Path, d.Kind, srcVal);
            if (FieldApplier.IsSupported(parsed))
            {
                int n = countInPatchForPath?.Invoke(d) ?? 0;
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(n <= 0 || string.IsNullOrEmpty(srcMod)))
                {
                    if (GUILayout.Button(
                            n > 0 ? $"Apply to {n} in Patch…" : "Apply to N in Patch… (none in Patch)",
                            GUILayout.Width(220)))
                        onApplyPattern(d, srcMod);
                }
                if (n <= 0)
                    EditorGUILayout.LabelField("Import into Patch first.", EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }
            else if (!string.IsNullOrEmpty(parsed.UnsupportedReason)
                     && !string.IsNullOrEmpty(d.Path)
                     && !d.Path.StartsWith("refs[", StringComparison.Ordinal))
            {
                EditorGUILayout.LabelField("Mass Change: " + parsed.UnsupportedReason, EditorStyles.miniLabel);
            }
        }

        public static string Short(string v) => v == UnityYaml.Missing ? "(absent)" : (v != null && v.Length > 140 ? v.Substring(0, 138) + "…" : v);
    }
}
