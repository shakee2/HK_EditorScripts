using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <param name="onApplyPattern">
        /// Optional: when set, supported ADD/PICK diffs show an "Apply to N in Patch…" button.
        /// Args: (diff, preferredSourceMod). Caller owns Patch-only scoping and apply.
        /// </param>
        /// <param name="countInPatchForPath">
        /// Optional: returns how many scoped Patch elements share this path/kind (for the button label).
        /// </param>
        /// <param name="onResolveDiff">
        /// Optional: per-row Resolve — dismisses this difference (sidecar / session).
        /// </param>
        /// <param name="hideResolved">
        /// When true, hide diffs with Status=<c>carried</c> (already resolved).
        /// </param>
        public static void DrawTable(
            IEnumerable<Diff> diffs,
            string winner,
            bool odin,
            Action<Diff, string> onApplyPattern = null,
            Func<Diff, int> countInPatchForPath = null,
            bool hideExtraInWinner = false,
            Action<Diff> onResolveDiff = null,
            bool hideResolved = true)
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

            foreach (var d in visible)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                DrawDiffHeader(d, winner, onResolveDiff);
                foreach (var kv in d.Values.OrderBy(k => k.Key == "*winner*" ? "" : k.Key))
                {
                    string who = kv.Key == "*winner*" ? winner + " (winner)" : kv.Key;
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(who, GUILayout.Width(150));
                    EditorGUILayout.SelectableLabel(Short(kv.Value), EditorStyles.miniLabel,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    EditorGUILayout.EndHorizontal();
                }

                if (onApplyPattern != null && d.Kind != DiffKind.ExtraInWinner)
                {
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

                EditorGUILayout.EndVertical();
            }
        }

        static void DrawDiffHeader(Diff d, string winner, Action<Diff> onResolveDiff)
        {
            EditorGUILayout.BeginHorizontal();
            string kindTag = d.Kind == DiffKind.ExtraInWinner ? $"ONLY in winner ({winner})"
                           : d.Kind == DiffKind.MissingInWinner ? "MISSING in winner"
                           : "CHANGED";
            EditorGUILayout.LabelField($"{kindTag}   {d.Path}", EditorStyles.miniBoldLabel);
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
        }

        public static string Short(string v) => v == UnityYaml.Missing ? "(absent)" : (v != null && v.Length > 140 ? v.Substring(0, 138) + "…" : v);
    }
}
