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
        public static void DrawTable(IEnumerable<Diff> diffs, string winner, bool odin)
        {
            var list = diffs?.ToList() ?? new List<Diff>();
            int diffCount = list.Count;
            EditorGUILayout.LabelField(odin
                ? $"Differences (read-only) — {diffCount} — Odin element (no Flatten body): references only, use Compare side-by-side for full fields:"
                : $"Differences (read-only) — {diffCount}:", EditorStyles.miniBoldLabel);
            if (diffCount == 0)
            {
                EditorGUILayout.LabelField("No differences.", EditorStyles.miniLabel);
                return;
            }
            foreach (var d in list)
            {
                if (d.Kind == DiffKind.ExtraInWinner)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.LabelField($"ONLY in winner ({winner})   {d.Path}", EditorStyles.miniBoldLabel);
                    if (d.Values.TryGetValue("*winner*", out var wv) && wv != UnityYaml.Missing)
                        EditorGUILayout.SelectableLabel("    " + Short(wv), EditorStyles.miniLabel,
                            GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    EditorGUILayout.EndVertical();
                    continue;
                }
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                string kindTag = d.Kind == DiffKind.MissingInWinner ? "MISSING in winner" : "CHANGED";
                EditorGUILayout.LabelField($"{kindTag}   {d.Path}", EditorStyles.miniBoldLabel);
                foreach (var kv in d.Values.OrderBy(k => k.Key == "*winner*" ? "" : k.Key))
                {
                    string who = kv.Key == "*winner*" ? winner + " (winner)" : kv.Key;
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(who, GUILayout.Width(150));
                    EditorGUILayout.SelectableLabel(Short(kv.Value), EditorStyles.miniLabel,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndVertical();
            }
        }

        public static string Short(string v) => v == UnityYaml.Missing ? "(absent)" : (v != null && v.Length > 140 ? v.Substring(0, 138) + "…" : v);
    }
}
