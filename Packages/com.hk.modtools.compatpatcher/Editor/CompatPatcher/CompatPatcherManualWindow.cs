using System.IO;
using UnityEditor;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Utility window that shows <c>Docs/CompatPatcher-Manual.md</c> from this package.
    /// </summary>
    public class CompatPatcherManualWindow : EditorWindow
    {
        const string ManualRelative = "Docs/CompatPatcher-Manual.md";

        string _text;
        string _path;
        string _error;
        Vector2 _scroll;
        static GUIStyle _body;

        public static void Open()
        {
            var w = GetWindow<CompatPatcherManualWindow>(utility: true, title: "Compat Patcher Manual", focus: true);
            w.minSize = new Vector2(420, 360);
            if (w.position.width < 420 || w.position.height < 320)
                w.position = new Rect(w.position.x, w.position.y, 520, 560);
            w.LoadManual();
            w.Show();
        }

        void OnEnable() => LoadManual();

        void LoadManual()
        {
            _text = null;
            _error = null;
            _path = null;
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(CompatPatcherManualWindow).Assembly);
                if (info == null || string.IsNullOrEmpty(info.resolvedPath))
                {
                    _error = "Could not resolve the compatpatcher package path.";
                    return;
                }
                _path = Path.Combine(info.resolvedPath, ManualRelative);
                if (!File.Exists(_path))
                {
                    _error = "Manual not found:\n" + _path;
                    return;
                }
                _text = File.ReadAllText(_path);
            }
            catch (System.Exception e)
            {
                _error = e.Message;
            }
        }

        void OnGUI()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(60)))
                LoadManual();
            if (!string.IsNullOrEmpty(_path)
                && GUILayout.Button("Reveal", EditorStyles.toolbarButton, GUILayout.Width(60)))
                EditorUtility.RevealInFinder(_path);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.HelpBox(_error, MessageType.Warning);
                return;
            }
            if (string.IsNullOrEmpty(_text))
            {
                EditorGUILayout.HelpBox("Manual is empty.", MessageType.Info);
                return;
            }

            if (_body == null)
            {
                _body = new GUIStyle(EditorStyles.wordWrappedLabel)
                {
                    richText = false,
                    alignment = TextAnchor.UpperLeft,
                };
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            // Short markdown-ish manual: headings as bold, body as wrapped labels.
            foreach (var raw in _text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw;
                if (line.StartsWith("### "))
                    EditorGUILayout.LabelField(line.Substring(4), EditorStyles.boldLabel);
                else if (line.StartsWith("## "))
                {
                    EditorGUILayout.Space(6);
                    EditorGUILayout.LabelField(line.Substring(3), EditorStyles.boldLabel);
                }
                else if (line.StartsWith("# "))
                    EditorGUILayout.LabelField(line.Substring(2), EditorStyles.largeLabel);
                else if (line.Trim() == "---")
                    EditorGUILayout.Space(8);
                else if (line.Length == 0)
                    EditorGUILayout.Space(2);
                else if (IsMdTableRule(line))
                    continue; // skip |---|---|
                else if (line.TrimStart().StartsWith("|"))
                    EditorGUILayout.LabelField(FormatMdTableRow(line), _body);
                else
                    EditorGUILayout.LabelField(StripMd(line), _body);
            }
            EditorGUILayout.EndScrollView();
        }

        static bool IsMdTableRule(string line)
        {
            string t = line.Trim();
            if (t.Length < 3 || t[0] != '|') return false;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c != '|' && c != '-' && c != ':' && c != ' ') return false;
            }
            return true;
        }

        static string FormatMdTableRow(string line)
        {
            var parts = line.Split('|');
            var cells = new System.Collections.Generic.List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string c = StripMd(parts[i]).Trim();
                if (c.Length > 0) cells.Add(c);
            }
            if (cells.Count == 0) return "";
            if (cells.Count == 1) return "• " + cells[0];
            return "• " + cells[0] + " — " + string.Join(" · ", cells.GetRange(1, cells.Count - 1));
        }

        static string StripMd(string s)
        {
            // Light cleanup so **bold** / `code` read cleanly in IMGUI labels.
            return s.Replace("**", "").Replace("`", "").Replace("*", "");
        }
    }
}
