using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Networking;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace HK.ModTools.Shared
{
    /// <summary>
    /// Checks every installed <c>com.hk.modtools.*</c> package's GitHub repo tags for a newer
    /// release than what's currently resolved via UPM, and offers to update in place. Each package
    /// versions independently via namespaced tags (<c>&lt;shortName&gt;/&lt;semver&gt;</c>, e.g.
    /// <c>core/1.1.0</c>) on the one shared repo, since <c>?path=</c> git dependencies share a single
    /// tag list. One background check per <see cref="ToolsOptionsWindow"/>-configured interval (or
    /// the "Check For Updates" menu item any time in between) fetches the tag list once and evaluates
    /// every package due for a check, then shows a single aggregated dialog.
    ///
    /// Only meaningful for packages resolved via a git URL (<see cref="PackageSource.Git"/>); a local
    /// "file:" reference has nothing meaningful to compare against and is skipped.
    /// </summary>
    [InitializeOnLoad]
    public static class UpdateChecker
    {
        internal const string PackageNamePrefix = "com.hk.modtools.";
        const string RepoTagsApiUrl = "https://api.github.com/repos/shakee2/HK_EditorScripts/tags";
        const string RepoGitUrl = "https://github.com/shakee2/HK_EditorScripts.git";
        const string MenuPath = "Tools/shakee's Tools/Check For Updates";

        const string LastCheckPrefPrefix = "HKModTools.UpdateChecker.LastCheckTicks.";
        const string AutoUpdatePrefPrefix = "HKModTools.UpdateChecker.AutoUpdate.";
        const string IntervalPrefPrefix = "HKModTools.UpdateChecker.IntervalHours.";
        const string SkippedTagPrefPrefix = "HKModTools.UpdateChecker.SkippedTag.";
        internal const double DefaultIntervalHours = 24 * 14; // bi-weekly by default; editable per-package in the Options window

        class PackageStatus
        {
            public PackageInfo info;
            public string shortName;   // e.g. "core"
            public Version installed;
            public Version latest;
            public string latestTag;   // e.g. "core/1.1.0"
        }

        static bool startupCheckDone;

        static UpdateChecker()
        {
            EditorApplication.update += RunStartupCheckOnce;
        }

        static void RunStartupCheckOnce()
        {
            EditorApplication.update -= RunStartupCheckOnce;
            if (startupCheckDone) return;
            startupCheckDone = true;

            var due = GetInstalledGitPackages()
                .Where(p => GetAutoUpdateEnabled(p.name)
                            && (DateTime.UtcNow - GetLastCheckTime(p.name)).TotalHours >= GetIntervalHours(p.name))
                .ToList();
            if (due.Count == 0) return;

            RunCheck(due, manual: false);
        }

        [MenuItem(MenuPath, false, 201)]
        static void CheckForUpdatesMenuItem() => CheckAllNow(manual: true);

        /// <summary>Checks every installed com.hk.modtools.* git package right now, ignoring the auto-update toggle/interval/skip state.</summary>
        public static void CheckAllNow(bool manual)
        {
            var packages = GetInstalledGitPackages();
            if (packages.Count == 0)
            {
                if (manual) EditorUtility.DisplayDialog("Check For Updates",
                    "No HK Mod Tools packages are installed via a git URL (local \"file:\" references have nothing to check).", "OK");
                return;
            }
            RunCheck(packages, manual);
        }

        /// <summary>Checks a single installed package right now (used by the Options window's per-package "Check Now" button).</summary>
        public static void CheckSingleNow(string packageName, bool manual)
        {
            var pkg = GetInstalledGitPackages().FirstOrDefault(p => p.name == packageName);
            if (pkg == null)
            {
                if (manual) EditorUtility.DisplayDialog("Check For Updates",
                    $"{packageName} is not installed via a git URL.", "OK");
                return;
            }
            RunCheck(new List<PackageInfo> { pkg }, manual);
        }

        static void RunCheck(List<PackageInfo> packages, bool manual)
        {
            foreach (var p in packages) SetLastCheckTime(p.name, DateTime.UtcNow);

            var request = UnityWebRequest.Get(RepoTagsApiUrl);
            request.SetRequestHeader("User-Agent", "HK-ModTools-UpdateChecker");
            var op = request.SendWebRequest();

            void Poll()
            {
                if (!op.isDone) return;
                EditorApplication.update -= Poll;
                HandleTagsResponse(request, packages, manual);
            }
            EditorApplication.update += Poll;
        }

        static List<PackageInfo> GetInstalledGitPackages() =>
            PackageInfo.GetAllRegisteredPackages()
                .Where(p => p.name.StartsWith(PackageNamePrefix, StringComparison.Ordinal) && p.source == PackageSource.Git)
                .OrderBy(p => p.name, StringComparer.Ordinal)
                .ToList();

        static void HandleTagsResponse(UnityWebRequest request, List<PackageInfo> packages, bool manual)
        {
            using (request)
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    string message = $"[UpdateChecker] Could not check for updates: {request.error}";
                    if (manual) EditorUtility.DisplayDialog("Check For Updates", message, "OK");
                    else Debug.LogWarning(message);
                    return;
                }

                var allTags = ExtractTagNames(request.downloadHandler.text);
                var statuses = new List<PackageStatus>();

                foreach (var pkg in packages)
                {
                    string shortName = pkg.name.Substring(PackageNamePrefix.Length);
                    var (latestTag, latestVersion) = FindLatestTag(allTags, shortName);
                    if (latestTag == null) continue; // no tags yet for this package (e.g. an untagged experimental package)

                    Version installed = ParseSemVer(pkg.version);
                    if (installed != null && latestVersion <= installed) continue; // up to date

                    if (!manual && GetSkippedTag(pkg.name) == latestTag) continue; // user deferred this exact tag already

                    statuses.Add(new PackageStatus { info = pkg, shortName = shortName, installed = installed, latest = latestVersion, latestTag = latestTag });
                }

                if (statuses.Count == 0)
                {
                    if (manual) EditorUtility.DisplayDialog("Check For Updates", "All checked HK Mod Tools packages are up to date.", "OK");
                    return;
                }

                ShowAggregatedDialog(statuses);
            }
        }

        static void ShowAggregatedDialog(List<PackageStatus> statuses)
        {
            string lines = string.Join("\n", statuses.Select(s =>
                $"  {(s.info.displayName ?? s.shortName)}: {(s.installed != null ? "v" + s.installed : "unknown")} -> {s.latestTag}"));
            string message = (statuses.Count == 1 ? "An update is available:\n\n" : "Updates are available:\n\n") +
                lines + "\n\nUpdate now?";

            int choice = EditorUtility.DisplayDialogComplex(
                "HK Mod Tools Update" + (statuses.Count == 1 ? "" : "s") + " Available", message,
                "Update", "Later", "Skip This Version" + (statuses.Count == 1 ? "" : "s"));

            if (choice == 0) ApplyUpdatesSequentially(statuses, 0);
            else if (choice == 2) foreach (var s in statuses) SetSkippedTag(s.info.name, s.latestTag);
        }

        static void ApplyUpdatesSequentially(List<PackageStatus> statuses, int index)
        {
            if (index >= statuses.Count) return;
            var s = statuses[index];
            string url = $"{RepoGitUrl}?path=Packages/{s.info.name}#{s.latestTag}";
            var addRequest = Client.Add(url);

            void Poll()
            {
                if (!addRequest.IsCompleted) return;
                EditorApplication.update -= Poll;

                if (addRequest.Status == StatusCode.Success)
                    Debug.Log($"[UpdateChecker] Updated {s.info.name} to {s.latestTag}.");
                else
                    Debug.LogError($"[UpdateChecker] Update of {s.info.name} to {s.latestTag} failed: {addRequest.Error?.message}");

                ApplyUpdatesSequentially(statuses, index + 1);
            }
            EditorApplication.update += Poll;
        }

        static (string tag, Version version) FindLatestTag(List<string> allTags, string shortName)
        {
            string prefix = shortName + "/";
            return allTags
                .Where(t => t.StartsWith(prefix, StringComparison.Ordinal))
                .Select(t => (tag: t, version: ParseSemVer(t.Substring(prefix.Length))))
                .Where(t => t.version != null)
                .OrderByDescending(t => t.version)
                .Select(t => (t.tag, t.version))
                .FirstOrDefault();
        }

        /// <summary>
        /// Minimal extraction of GitHub's tags API response — we only need each tag's "name" field,
        /// so a small regex avoids pulling in a JSON library for one string per array element.
        /// </summary>
        static List<string> ExtractTagNames(string tagsJson) =>
            Regex.Matches(tagsJson, "\"name\"\\s*:\\s*\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value)
                .ToList();

        static Version ParseSemVer(string version)
        {
            if (string.IsNullOrEmpty(version)) return null;
            string trimmed = version.TrimStart('v', 'V');
            return Version.TryParse(trimmed, out Version v) ? v : null;
        }

        // ── Per-package prefs (read by ToolsOptionsWindow too) ──────────────────────────────────

        internal static bool GetAutoUpdateEnabled(string packageName) =>
            EditorPrefs.GetBool(AutoUpdatePrefPrefix + packageName, true);

        internal static void SetAutoUpdateEnabled(string packageName, bool value) =>
            EditorPrefs.SetBool(AutoUpdatePrefPrefix + packageName, value);

        internal static double GetIntervalHours(string packageName) =>
            EditorPrefs.GetFloat(IntervalPrefPrefix + packageName, (float)DefaultIntervalHours);

        internal static void SetIntervalHours(string packageName, double hours) =>
            EditorPrefs.SetFloat(IntervalPrefPrefix + packageName, (float)hours);

        static DateTime GetLastCheckTime(string packageName)
        {
            string raw = EditorPrefs.GetString(LastCheckPrefPrefix + packageName, "");
            return long.TryParse(raw, out long ticks) ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
        }

        static void SetLastCheckTime(string packageName, DateTime utc) =>
            EditorPrefs.SetString(LastCheckPrefPrefix + packageName, utc.Ticks.ToString());

        static string GetSkippedTag(string packageName) =>
            EditorPrefs.GetString(SkippedTagPrefPrefix + packageName, "");

        static void SetSkippedTag(string packageName, string tag) =>
            EditorPrefs.SetString(SkippedTagPrefPrefix + packageName, tag);

        /// <summary>Every currently-installed com.hk.modtools.* package resolved via a git URL — exposed for the Options window.</summary>
        internal static List<PackageInfo> GetGitPackagesForOptionsWindow() => GetInstalledGitPackages();
    }
}
