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
    /// auto-update toggle + check interval + an on-demand "Check Now", plus a shortcut into Unity's
    /// built-in Shortcuts manager for rebinding this suite's menu-item keybinds (no custom rebind UI —
    /// every tool here is a regular <c>[MenuItem]</c>, which Unity's own shortcut editor already
    /// supports rebinding).
    /// </summary>
    public class ToolsOptionsWindow : EditorWindow
    {
        [MenuItem("Tools/shakee's Tools/Options", false, 300)]
        static void Open() => GetWindow<ToolsOptionsWindow>("HK Mod Tools Options");

        Vector2 _scroll;

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawUpdatesSection();
            EditorGUILayout.Space(12);
            DrawKeybindsSection();

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
                "Every HK Mod Tools window/action is a regular Unity menu item, so keybinds are " +
                "rebound through Unity's own Shortcuts manager rather than a separate editor here — " +
                "search for \"shakee's Tools\" there to find this suite's commands.", MessageType.None);

            if (GUILayout.Button("Open Unity Shortcuts Manager...", GUILayout.Width(220)))
                EditorApplication.ExecuteMenuItem("Edit/Shortcuts...");
        }
    }
}
