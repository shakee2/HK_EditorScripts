using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Mass Change UI: groups recurring field diffs from the scoped row set, lets the user pick a
    /// source mod per group, and applies via <see cref="FieldApplier"/> to Patch SOs only.
    /// </summary>
    public class MassFieldChangeWindow : EditorWindow
    {
        List<MassChange.Candidate> _candidates;
        Dictionary<string, string> _patchPathByName;
        HashSet<string> _patchNames;
        HashSet<string> _selectedKeys; // when non-null, only these element keys are applied
        Func<ElementRow, string> _elemKey;
        Action _onDone;
        Vector2 _scroll;
        bool _useCustomSelection;
        bool _closing;

        public static void Show(
            List<MassChange.Candidate> candidates,
            Dictionary<string, string> patchPathByName,
            HashSet<string> patchNames,
            HashSet<string> customSelectionKeys,
            Func<ElementRow, string> elemKey,
            Action onDone)
        {
            var w = GetWindow<MassFieldChangeWindow>(utility: false, title: "Mass Change", focus: true);
            w._candidates = candidates ?? new List<MassChange.Candidate>();
            w._patchPathByName = patchPathByName ?? new Dictionary<string, string>();
            w._patchNames = patchNames ?? new HashSet<string>();
            w._selectedKeys = customSelectionKeys;
            w._useCustomSelection = customSelectionKeys != null && customSelectionKeys.Count > 0;
            w._elemKey = elemKey ?? (r => r.Type + "|" + r.Name);
            w._onDone = onDone;
            w._closing = false;
            w.minSize = new Vector2(740, 440);
            w.Show();
        }

        void OnGUI()
        {
            if (_closing) { Close(); return; }
            if (_candidates == null)
            {
                EditorGUILayout.HelpBox("No candidates.", MessageType.Info);
                return;
            }

            int inPatchPool = CountInPatchTargets();
            EditorGUILayout.LabelField(
                $"Mass Change — {_candidates.Count} pattern(s), {inPatchPool} Patch target(s) in scope",
                EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Applies field edits only to elements already in Assets/Databases/Patch/. "
                + "Import winners first. Never auto-imports.\n"
                + (_useCustomSelection
                    ? "Scope: custom selection from the main window."
                    : "Scope: current filtered list (including Type filter)."),
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var c in _candidates)
                DrawCandidate(c);
            EditorGUILayout.EndScrollView();

            int toApply = _candidates.Count(c => c.Action != "skip" && IsActionable(c));
            int els = CountApplyElements();
            EditorGUILayout.LabelField(
                $"Will apply {toApply} pattern(s) → up to {els} Patch element(s) (unsupported / not-in-Patch skipped).",
                EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(toApply == 0 || els == 0))
            {
                if (GUILayout.Button($"Apply ({toApply})", GUILayout.Width(120), GUILayout.Height(24)))
                    RunApply();
            }
            if (GUILayout.Button("Cancel", GUILayout.Width(100), GUILayout.Height(24)))
            {
                _closing = true;
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }

        void DrawCandidate(MassChange.Candidate c)
        {
            int inPatch = c.Elements.Count(e => InScope(e) && _patchNames.Contains(e.Name));
            int notInPatch = c.Elements.Count(e => InScope(e) && !_patchNames.Contains(e.Name));

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(c.Path, EditorStyles.boldLabel);
            string kindTag = c.Kind == DiffKind.MissingInWinner ? "ADD"
                           : c.Kind == DiffKind.Changed ? "PICK" : "?";
            EditorGUILayout.LabelField(kindTag, GUILayout.Width(50));
            EditorGUILayout.LabelField($"Patch:{inPatch}", GUILayout.Width(70));
            if (notInPatch > 0)
                EditorGUILayout.LabelField($"skip:{notInPatch}", GUILayout.Width(60));
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(c.Preview))
            {
                EditorGUILayout.SelectableLabel("  preview: " + DiffGui.Short(c.Preview),
                    EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }

            if (c.ApplyKind == FieldApplier.ApplyKind.Unsupported)
            {
                EditorGUILayout.HelpBox(
                    "Unsupported pattern: " + (c.UnsupportedReason ?? "complex / Odin"),
                    MessageType.Warning);
                c.Action = "skip";
            }
            else if (inPatch == 0)
            {
                EditorGUILayout.LabelField("No matching elements in Patch — import first.", EditorStyles.miniLabel);
                c.Action = "skip";
            }
            else
            {
                var options = new List<string> { "skip" };
                options.AddRange(c.Sources);
                int idx = Math.Max(0, options.IndexOf(c.Action));
                if (idx < 0) idx = 0;
                int newIdx = EditorGUILayout.Popup("Source mod", idx, options.ToArray());
                c.Action = options[Mathf.Clamp(newIdx, 0, options.Count - 1)];
            }

            EditorGUILayout.EndVertical();
        }

        bool InScope(ElementRow row)
        {
            if (!_useCustomSelection || _selectedKeys == null) return true;
            return _selectedKeys.Contains(_elemKey(row));
        }

        bool IsActionable(MassChange.Candidate c) =>
            c.ApplyKind != FieldApplier.ApplyKind.Unsupported
            && c.Elements.Any(e => InScope(e) && _patchNames.Contains(e.Name));

        int CountInPatchTargets()
        {
            var names = new HashSet<string>();
            foreach (var c in _candidates)
                foreach (var e in c.Elements)
                    if (InScope(e) && _patchNames.Contains(e.Name))
                        names.Add(e.Name);
            return names.Count;
        }

        int CountApplyElements()
        {
            var keys = new HashSet<string>();
            foreach (var c in _candidates.Where(c => c.Action != "skip" && IsActionable(c)))
                foreach (var e in c.Elements)
                    if (InScope(e) && _patchNames.Contains(e.Name))
                        keys.Add(_elemKey(e));
            return keys.Count;
        }

        void RunApply()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Mass Change", "Applying…", 0.2f);
                var restrict = _useCustomSelection ? _selectedKeys : null;
                var stats = MassChange.Apply(_candidates, _patchPathByName, restrict, _elemKey);
                string msg =
                    $"Applied: {stats.Applied}\n"
                    + $"Already had value: {stats.SkippedAlready}\n"
                    + $"Not in Patch (skipped): {stats.SkippedNotInPatch}\n"
                    + $"Failed: {stats.Failed}\n"
                    + $"Unsupported: {stats.Unsupported}";
                Debug.Log("[CompatPatcher] Mass Change · " + msg.Replace("\n", " · "));
                EditorUtility.DisplayDialog("Mass Change", msg, "OK");
            }
            catch (Exception e)
            {
                Debug.LogError("[CompatPatcher] Mass Change failed: " + e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _closing = true;
                _onDone?.Invoke();
                Close();
            }
        }
    }
}
