using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace HK.ModTools.Shared
{
    /// <summary>
    /// Aggregate settings window for every installed <c>com.hk.modtools.*</c> package: per-package
    /// auto-update toggle + check interval + an on-demand "Check Now"; a shortcut into Unity's
    /// built-in Shortcuts manager for rebinding this suite's menu-item keybinds (most tools here are a
    /// regular <c>[MenuItem]</c>, which Unity's own shortcut editor already supports rebinding); and a
    /// Formula Autocomplete section for the handful of shortcuts that AREN'T menu items — raw KeyDown
    /// checks inside <c>PropertyEffectOdinDrawer</c> (core package), invisible to Unity's Shortcuts
    /// manager, so they get real toggle controls here instead. Reads/writes those settings' EditorPrefs
    /// keys by string literal (see <c>PropertyEffectDrawer.cs</c>'s <c>PrefRightArrowConfirms</c> /
    /// <c>PrefCtrlEDConfirms</c>) rather than referencing the core assembly, since shared must not
    /// depend on the packages that depend on it.
    /// </summary>
    public class ToolsOptionsWindow : EditorWindow
    {
        [MenuItem("Tools/shakee's Tools/Options", false, 300)]
        static void Open() => GetWindow<ToolsOptionsWindow>("HK Mod Tools Options");

        // Mirrors PropertyEffectDrawer.cs's PrefRightArrowConfirms / PrefCtrlEDConfirms in the core
        // package (Upgrades/PropertyEffectDrawer.cs) — keep the two in sync if either changes.
        const string PrefRightArrowConfirms = "HKModTools.Autocomplete.RightArrowConfirms";
        const string PrefCtrlEDConfirms = "HKModTools.Autocomplete.CtrlEDConfirms";
        const string CorePackageName = "com.hk.modtools.core";

        Vector2 _scroll;

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawUpdatesSection();
            EditorGUILayout.Space(12);
            DrawKeybindsSection();
            DrawAutocompleteShortcutsSection();

            EditorGUILayout.EndScrollView();
        }

        // ── Updates ──────────────────────────────────────────────────────────────────
        void DrawUpdatesSection()
        {
            EditorGUILayout.LabelField("Updates", EditorStyles.boldLabel);

            var gitPackages = UpdateChecker.GetGitPackagesForOptionsWindow();
            var localPackages = PackageInfo.GetAllRegisteredPackages()
                .Where(p => p.name.StartsWith(UpdateChecker.PackageNamePrefix, StringComparison.Ordinal)
                            && p.source != PackageSource.Git)
                .OrderBy(p => p.name, StringComparer.Ordinal)
                .ToList();

            if (gitPackages.Count == 0 && localPackages.Count == 0)
            {
                EditorGUILayout.HelpBox("No HK Mod Tools packages are currently installed.", MessageType.Info);
                return;
            }

            if (GUILayout.Button("Check All Now", GUILayout.Width(140)))
                UpdateChecker.CheckAllNow(manual: true);

            EditorGUILayout.Space(4);

            foreach (var pkg in gitPackages) DrawPackageRow(pkg);

            if (localPackages.Count > 0)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Installed via a local reference (nothing to check):", EditorStyles.miniLabel);
                foreach (var pkg in localPackages)
                    EditorGUILayout.LabelField("  " + (pkg.displayName ?? pkg.name), EditorStyles.miniLabel);
            }
        }

        void DrawPackageRow(PackageInfo pkg)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(pkg.displayName ?? pkg.name, EditorStyles.boldLabel, GUILayout.Width(220));
            EditorGUILayout.LabelField("v" + pkg.version, GUILayout.Width(70));

            bool autoUpdate = UpdateChecker.GetAutoUpdateEnabled(pkg.name);
            bool newAutoUpdate = EditorGUILayout.ToggleLeft("Auto-update", autoUpdate, GUILayout.Width(100));
            if (newAutoUpdate != autoUpdate) UpdateChecker.SetAutoUpdateEnabled(pkg.name, newAutoUpdate);

            if (GUILayout.Button("Check Now", GUILayout.Width(90)))
                UpdateChecker.CheckSingleNow(pkg.name, manual: true);

            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(!newAutoUpdate))
            {
                double intervalHours = UpdateChecker.GetIntervalHours(pkg.name);
                double days = intervalHours / 24.0;
                double newDays = EditorGUILayout.DoubleField("Check every (days)", days);
                if (!Mathf.Approximately((float)newDays, (float)days) && newDays > 0)
                    UpdateChecker.SetIntervalHours(pkg.name, newDays * 24.0);
            }

            EditorGUILayout.EndVertical();
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
