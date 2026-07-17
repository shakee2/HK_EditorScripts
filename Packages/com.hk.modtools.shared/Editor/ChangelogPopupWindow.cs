using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace HK.ModTools.Shared
{
    /// <summary>
    /// Utility popup opened from Options when a package has an update: one collapsible card per
    /// version between the installed release and the latest tag (newest first), filled from that
    /// package's <c>CHANGELOG.md</c>.
    /// </summary>
    public class ChangelogPopupWindow : EditorWindow
    {
        UpdateChecker.PackageRow _row;
        Vector2 _scroll;
        readonly Dictionary<string, bool> _foldouts = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool _subscribed;
        bool _requested;
        static GUIStyle _bodyStyle;

        /// <summary>Opens (or focuses) the What's new popup for <paramref name="row"/>.</summary>
        internal static void Open(UpdateChecker.PackageRow row)
        {
            if (row == null || !row.UpdateAvailable) return;

            string title = "What's new — " + row.Catalog.DisplayName;
            var window = GetWindow<ChangelogPopupWindow>(utility: true, title: title, focus: true);
            window._row = row;
            window._foldouts.Clear();
            window._requested = false;
            window.minSize = new Vector2(420, 320);
            if (window.position.width < 420 || window.position.height < 280)
                window.position = new Rect(window.position.x, window.position.y, 480, 420);
            window.EnsureFetch();
            window.Show();
        }

        void OnEnable()
        {
            UpdateChecker.StateChanged += OnCheckerStateChanged;
            _subscribed = true;
        }

        void OnDisable()
        {
            if (_subscribed)
            {
                UpdateChecker.StateChanged -= OnCheckerStateChanged;
                _subscribed = false;
            }
        }

        void OnCheckerStateChanged() => Repaint();

        void EnsureFetch()
        {
            if (_row == null || _requested) return;
            _requested = true;
            UpdateChecker.EnsureChangelogForUpdate(_row);
        }

        void OnGUI()
        {
            if (_row == null)
            {
                EditorGUILayout.HelpBox("No package selected.", MessageType.Info);
                return;
            }

            EnsureFetch();

            string from = _row.Installed != null ? "v" + _row.Installed.version : "?";
            string to = _row.LatestVersion != null ? "v" + _row.LatestVersion : "?";
            EditorGUILayout.LabelField($"{_row.Catalog.DisplayName}: {from} → {to}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(_row.Catalog.Name, EditorStyles.miniLabel);
            EditorGUILayout.Space(4);

            var range = UpdateChecker.GetChangelogRange(_row);

            if (range.Loading)
            {
                EditorGUILayout.HelpBox("Loading changelog…", MessageType.Info);
                return;
            }

            if (!string.IsNullOrEmpty(range.Error) && range.Blocks.Count == 0)
            {
                EditorGUILayout.HelpBox(range.Error, MessageType.Warning);
                return;
            }

            if (range.Blocks == null || range.Blocks.Count == 0)
            {
                EditorGUILayout.HelpBox("No changelog entries in this range.", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EnsureBodyStyle();

            for (int i = 0; i < range.Blocks.Count; i++)
            {
                DrawVersionCard(range.Blocks[i], expandByDefault: i == 0);
                EditorGUILayout.Space(4);
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawVersionCard(UpdateChecker.ChangelogVersionBlock block, bool expandByDefault)
        {
            string key = block.Version.ToString();
            if (!_foldouts.ContainsKey(key))
                _foldouts[key] = expandByDefault;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            string header = "v" + block.Version;
            if (!string.IsNullOrEmpty(block.Date))
                header += "  —  " + block.Date;

            _foldouts[key] = EditorGUILayout.Foldout(_foldouts[key], header, toggleOnLabelClick: true, style: EditorStyles.foldoutHeader);
            if (_foldouts[key])
            {
                EditorGUILayout.Space(2);
                GUILayout.Label(block.Body ?? "", _bodyStyle);
            }

            EditorGUILayout.EndVertical();
        }

        static void EnsureBodyStyle()
        {
            if (_bodyStyle != null) return;
            _bodyStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                richText = false,
                padding = new RectOffset(4, 4, 2, 2)
            };
        }
    }
}
