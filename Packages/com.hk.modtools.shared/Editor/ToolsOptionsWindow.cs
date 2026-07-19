using System.Linq;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace HK.ModTools.Shared
{
    /// <summary>
    /// Aggregate settings window for every <c>com.hk.modtools.*</c> package: a catalog list with
    /// Install / Update / Remove (so you don't have to paste git URLs into Package Manager by hand),
    /// per-package auto-update toggle + check interval for installed git packages, a shortcut into
    /// Unity's built-in Shortcuts manager for rebinding this suite's menu-item keybinds (most tools
    /// here are a regular <c>[MenuItem]</c>, which Unity's own shortcut editor already supports
    /// rebinding), and a Formula Autocomplete section for the handful of shortcuts that AREN'T menu
    /// items — raw KeyDown checks inside <c>PropertyEffectOdinDrawer</c> (core package), invisible
    /// to Unity's Shortcuts manager, so they get real toggle controls here instead. Reads/writes
    /// those settings' EditorPrefs keys by string literal (see <c>PropertyEffectDrawer.cs</c>'s
    /// <c>PrefRightArrowConfirms</c> / <c>PrefCtrlEDConfirms</c>) rather than referencing the core
    /// assembly, since shared must not depend on the packages that depend on it.
    /// </summary>
    public class ToolsOptionsWindow : EditorWindow
    {
        [MenuItem("Tools/shakee's Tools/Options", false, 300)]
        static void Open() => OpenFromMenu();

        /// <summary>Opens (or focuses) this window — also used by the Check For Updates menu item.</summary>
        internal static void OpenFromMenu()
        {
            var window = GetWindow<ToolsOptionsWindow>("HK Mod Tools Options");
            window.minSize = new Vector2(560, 480);
            // Only set a comfortable default the first time; don't fight a user-resized window.
            if (window.position.width < 560 || window.position.height < 400)
                window.position = new Rect(window.position.x, window.position.y, 620, 560);
        }

        // Mirrors PropertyEffectDrawer.cs's PrefRightArrowConfirms / PrefCtrlEDConfirms in the core
        // package (Upgrades/PropertyEffectDrawer.cs) — keep the two in sync if either changes.
        const string PrefRightArrowConfirms = "HKModTools.Autocomplete.RightArrowConfirms";
        const string PrefCtrlEDConfirms = "HKModTools.Autocomplete.CtrlEDConfirms";
        const string CorePackageName = "com.hk.modtools.core";

        Vector2 _scroll;
        bool _subscribed;
        bool _requestedTags;
        static GUIStyle _authorStyle;
        static GUIStyle _headlineStyle;

        void OnEnable()
        {
            minSize = new Vector2(560, 480);
            UpdateChecker.StateChanged += OnCheckerStateChanged;
            _subscribed = true;
            if (!_requestedTags)
            {
                _requestedTags = true;
                UpdateChecker.RefreshReleaseTagsIfStale();
            }
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

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawPackagesSection();
            EditorGUILayout.Space(12);
            DrawKeybindsSection();
            DrawAutocompleteShortcutsSection();

            EditorGUILayout.EndScrollView();
        }

        // ── Packages (install / update / remove) ─────────────────────────────────────
        void DrawPackagesSection()
        {
            EditorGUILayout.LabelField("Packages", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Install, update, or remove HK Mod Tools packages here — no need to paste git URLs " +
                "into Package Manager. Shared must be installed first (this window lives in Shared). " +
                "Installing or updating a tool will also bump any required dependencies that are " +
                "missing or below the minimum version. Opening this window (and Check All) refreshes " +
                "release tags from GitHub so the cards show newer versions.",
                MessageType.None);

            var rows = UpdateChecker.BuildPackageRows();
            bool anyUpdate = rows.Any(r => r.CanUpdate);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(UpdateChecker.IsBusy))
            {
                if (GUILayout.Button("Check All", GUILayout.Width(100)))
                    UpdateChecker.CheckAllNow(manual: true);

                using (new EditorGUI.DisabledScope(!anyUpdate))
                {
                    if (GUILayout.Button("Update All", GUILayout.Width(100)))
                        UpdateChecker.UpdateAllAvailable();
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (UpdateChecker.IsBusy && !string.IsNullOrEmpty(UpdateChecker.BusyLabel))
                EditorGUILayout.HelpBox(UpdateChecker.BusyLabel, MessageType.Info);

            EditorGUILayout.Space(4);

            foreach (var row in rows)
                DrawPackageRow(row);
        }

        void DrawPackageRow(UpdateChecker.PackageRow row)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EnsureHeadlineStyle();
            // ExpandWidth(true) + wordWrap so the title wraps instead of ellipsis-clipping next to Author.
            GUILayout.Label(BuildHeadline(row), _headlineStyle, GUILayout.ExpandWidth(true));
            if (!string.IsNullOrEmpty(row.Author))
            {
                EnsureAuthorStyle();
                GUILayout.Label("Author: " + row.Author, _authorStyle, GUILayout.ExpandWidth(false));
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(row.Catalog.Description, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(row.Catalog.Name, EditorStyles.miniLabel);

            if (!row.IsInstalled && row.HasRelease)
                EditorGUILayout.LabelField(FormatDependencies(row), EditorStyles.wordWrappedMiniLabel);

            if (row.IsGit)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                bool autoUpdate = UpdateChecker.GetAutoUpdateEnabled(row.Catalog.Name);
                bool newAutoUpdate = EditorGUILayout.ToggleLeft("Auto-check", autoUpdate, GUILayout.Width(90));
                if (newAutoUpdate != autoUpdate)
                    UpdateChecker.SetAutoUpdateEnabled(row.Catalog.Name, newAutoUpdate);

                using (new EditorGUI.DisabledScope(!newAutoUpdate))
                {
                    double intervalHours = UpdateChecker.GetIntervalHours(row.Catalog.Name);
                    double days = intervalHours / 24.0;
                    EditorGUILayout.LabelField("every", GUILayout.Width(40));
                    double newDays = EditorGUILayout.DoubleField(days, GUILayout.Width(50));
                    EditorGUILayout.LabelField("days", GUILayout.Width(35));
                    if (!Mathf.Approximately((float)newDays, (float)days) && newDays > 0)
                        UpdateChecker.SetIntervalHours(row.Catalog.Name, newDays * 24.0);
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(UpdateChecker.IsBusy))
            {
                if (!row.IsInstalled)
                {
                    bool canInstall = row.HasRelease;
                    using (new EditorGUI.DisabledScope(!canInstall))
                    {
                        string label;
                        if (canInstall)
                            label = $"Install {UpdateChecker.FormatVersionLabel(row.LatestVersion)}";
                        else if (!row.TagsKnown)
                            label = "Checking…";
                        else
                            label = "Install (no release yet)";

                        if (GUILayout.Button(label, GUILayout.Width(200)))
                            UpdateChecker.InstallLatest(row.Catalog.Name);
                    }
                }
                else
                {
                    if (row.IsLocal)
                    {
                        EditorGUILayout.LabelField(
                            "Local file: reference — manage via manifest / git pull",
                            EditorStyles.wordWrappedMiniLabel);
                    }
                    else if (!row.IsGit)
                    {
                        EditorGUILayout.LabelField($"Source: {row.Installed.source}", EditorStyles.miniLabel);
                    }
                    else
                    {
                        if (GUILayout.Button("Check for Update", GUILayout.Width(130)))
                            UpdateChecker.CheckAllNow(manual: true);

                        if (row.HasRelease)
                        {
                            string notesLabel = row.UpdateAvailable ? "What's new" : "Changelog";
                            if (GUILayout.Button(notesLabel, GUILayout.Width(90)))
                                ChangelogPopupWindow.Open(row);
                        }

                        if (row.UpdateAvailable)
                        {
                            string updateLabel = $"Update to {UpdateChecker.FormatVersionLabel(row.LatestVersion)}";
                            if (GUILayout.Button(updateLabel, GUILayout.Width(180)))
                                UpdateChecker.UpdateToLatest(row.Catalog.Name);
                        }
                        else if (row.CanUpdate)
                        {
                            if (GUILayout.Button("Update dependencies", GUILayout.Width(150)))
                                UpdateChecker.UpdateToLatest(row.Catalog.Name);
                        }
                    }

                    GUILayout.FlexibleSpace();
                    var prev = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(1f, 0.7f, 0.7f);
                    if (GUILayout.Button("Remove", GUILayout.Width(70)))
                        UpdateChecker.RemovePackage(row.Catalog.Name);
                    GUI.backgroundColor = prev;
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        static string BuildHeadline(UpdateChecker.PackageRow row)
        {
            string name = row.Catalog.DisplayName;
            if (!row.IsInstalled)
                return name + " - Not installed";

            string current = UpdateChecker.FormatVersionLabel(row.Installed.version);
            if (row.IsLocal) current += " (local)";

            if (row.UpdateAvailable)
                return $"{name} - {current} --> {UpdateChecker.FormatVersionLabel(row.LatestVersion)} available";

            return $"{name} - {current}";
        }

        static string FormatDependencies(UpdateChecker.PackageRow row)
        {
            if (!row.DependenciesKnown || row.Dependencies == null)
                return "Requires: (loading…)";
            if (row.Dependencies.Count == 0)
                return "Requires: nothing (standalone)";

            var parts = row.Dependencies.Select(d =>
            {
                var depEntry = UpdateChecker.Catalog.FirstOrDefault(c => c.Name == d.PackageName);
                string label = depEntry != null ? depEntry.DisplayName : d.PackageName;
                return $"{label} ≥ v{d.MinVersion}";
            });
            return "Requires: " + string.Join(", ", parts);
        }

        static void EnsureAuthorStyle()
        {
            if (_authorStyle != null) return;
            _authorStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperRight,
                wordWrap = false,
            };
        }

        static void EnsureHeadlineStyle()
        {
            if (_headlineStyle != null) return;
            _headlineStyle = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true };
        }

        // ── Keybinds ─────────────────────────────────────────────────────────────────
        void DrawKeybindsSection()
        {
            EditorGUILayout.LabelField("Keybinds", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Most HK Mod Tools windows/actions are regular Unity menu items, so those keybinds are " +
                "rebound through Unity's own Shortcuts manager rather than a separate editor here — " +
                "search for \"shakee's Tools\" there to find this suite's commands. The formula " +
                "autocomplete shortcuts below are the one exception (see next section).", MessageType.None);

            if (GUILayout.Button("Open Unity Shortcuts Manager...", GUILayout.Width(220)))
                EditorApplication.ExecuteMenuItem("Edit/Shortcuts...");
        }

        // ── Formula Autocomplete ─────────────────────────────────────────────────────
        void DrawAutocompleteShortcutsSection()
        {
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Formula Autocomplete", EditorStyles.boldLabel);

            bool coreInstalled = PackageInfo.GetAllRegisteredPackages().Any(p => p.name == CorePackageName);
            if (!coreInstalled)
            {
                EditorGUILayout.HelpBox(
                    $"{CorePackageName} isn't installed, so these settings have nothing to affect.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                "The Descriptor Effect formula field's suggestion popup (core package, requires Odin) " +
                "accepts the highlighted suggestion via raw key checks rather than a Unity menu item, so " +
                "it isn't in the Shortcuts manager above. Turn a binding off if it fights with normal " +
                "text-field cursor movement; ↑ / ↓ (navigate) are always on.", MessageType.None);

            bool rightArrow = EditorPrefs.GetBool(PrefRightArrowConfirms, true);
            bool newRightArrow = EditorGUILayout.ToggleLeft("→ (Right Arrow) accepts the highlighted suggestion", rightArrow);
            if (newRightArrow != rightArrow) EditorPrefs.SetBool(PrefRightArrowConfirms, newRightArrow);

            bool ctrlEd = EditorPrefs.GetBool(PrefCtrlEDConfirms, true);
            bool newCtrlEd = EditorGUILayout.ToggleLeft("Ctrl+E / Ctrl+D accepts the highlighted suggestion", ctrlEd);
            if (newCtrlEd != ctrlEd) EditorPrefs.SetBool(PrefCtrlEDConfirms, newCtrlEd);

            if (!newRightArrow && !newCtrlEd)
                EditorGUILayout.HelpBox("Both bindings are off — the suggestion popup can still be picked with the mouse.", MessageType.Warning);
        }
    }
}
