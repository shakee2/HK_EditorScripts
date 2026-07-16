using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Networking;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

/// <summary>
/// Checks the repo's GitHub tags for newer versions of every installed HK ModTools package and
/// offers to update each in place. Runs a throttled background check on editor load (once per
/// <see cref="CheckIntervalHours"/>) plus an on-demand menu item.
///
/// MULTI-PACKAGE: the repo hosts several UPM packages under Packages/ (installed via the
/// "?path=Packages/&lt;name&gt;#&lt;tag&gt;" git-URL form), so release tags are PER PACKAGE and
/// prefixed with the package name: "com.hk.modtools.core/1.2.0". The checker enumerates every
/// registered package whose name starts with <see cref="PackagePrefix"/> and is resolved from a
/// git URL, compares each against the highest tag carrying its prefix, and updates via
/// Client.Add with the matching ?path= URL — the same call the Package Manager UI makes, which
/// rewrites the manifest dependency and performs the git fetch/checkout itself.
///
/// Packages resolved via a local "file:" reference are skipped — there's nothing meaningful to
/// compare a live checkout against.
/// </summary>
[InitializeOnLoad]
public static class UpdateChecker
{
    const string PackagePrefix = "com.hk.modtools.";
    const string RepoTagsApiUrl = "https://api.github.com/repos/shakee2/HK_EditorScripts/tags";
    const string RepoGitUrl = "https://github.com/shakee2/HK_EditorScripts.git";
    const string MenuPath = "Tools/shakee's Tools/Check For Updates";

    const string LastCheckPrefKey = "HKEditorScripts.UpdateChecker.LastCheckTicks";
    const string SkippedVersionPrefKey = "HKEditorScripts.UpdateChecker.SkippedVersion"; // per-package: key + "." + package name
    const double CheckIntervalHours = 24 * 14; // bi-weekly; manual "Check For Updates" menu item covers the rest

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

        if (FindGitPackages().Count == 0)
            return; // nothing consuming via a git URL — local "file:" development checkouts have nothing to compare

        double hoursSinceLastCheck = (DateTime.UtcNow - GetLastCheckTime()).TotalHours;
        if (hoursSinceLastCheck < CheckIntervalHours)
            return;

        CheckForUpdate(manual: false);
    }

    [MenuItem(MenuPath, false, 201)]
    static void CheckForUpdateMenuItem() => CheckForUpdate(manual: true);

    static void CheckForUpdate(bool manual)
    {
        SetLastCheckTime(DateTime.UtcNow);

        var request = UnityWebRequest.Get(RepoTagsApiUrl);
        request.SetRequestHeader("User-Agent", "HK-EditorScripts-UpdateChecker");
        var op = request.SendWebRequest();

        void Poll()
        {
            if (!op.isDone) return;
            EditorApplication.update -= Poll;
            HandleTagsResponse(request, manual);
        }
        EditorApplication.update += Poll;
    }

    static void HandleTagsResponse(UnityWebRequest request, bool manual)
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

            List<string> tags = ExtractTagNames(request.downloadHandler.text);
            var updates = new List<(PackageInfo pkg, string tag, Version version)>();
            foreach (PackageInfo pkg in FindGitPackages())
            {
                // this package's releases are tags prefixed "<name>/" — e.g. "com.hk.modtools.core/1.2.0"
                var latest = tags
                    .Where(t => t.StartsWith(pkg.name + "/", StringComparison.OrdinalIgnoreCase))
                    .Select(t => (tag: t, version: ParseSemVer(t.Substring(pkg.name.Length + 1))))
                    .Where(t => t.version != null)
                    .OrderByDescending(t => t.version)
                    .FirstOrDefault();
                if (latest.tag == null) continue;

                Version current = ParseSemVer(pkg.version);
                if (current != null && latest.version <= current) continue;
                if (!manual && EditorPrefs.GetString(SkippedVersionPrefKey + "." + pkg.name, "") == latest.tag)
                    continue; // user chose "Skip This Version" for this exact tag already

                updates.Add((pkg, latest.tag, latest.version));
            }

            if (updates.Count == 0)
            {
                if (manual) EditorUtility.DisplayDialog("Check For Updates", "All HK ModTools packages are up to date.", "OK");
                return;
            }

            string list = string.Join("\n", updates.Select(u => $"  {u.pkg.name}: v{u.pkg.version} -> v{u.version}"));
            int choice = EditorUtility.DisplayDialogComplex(
                "HK ModTools Update Available",
                $"Newer versions are available:\n\n{list}\n\nUpdate now?",
                updates.Count > 1 ? "Update All" : "Update", "Later", "Skip These Versions");

            if (choice == 0) ApplyUpdates(updates.Select(u => (u.pkg.name, u.tag)).ToList(), 0);
            else if (choice == 2)
                foreach (var u in updates)
                    EditorPrefs.SetString(SkippedVersionPrefKey + "." + u.pkg.name, u.tag);
        }
    }

    /// <summary>Client.Add handles one request at a time — chain them sequentially.</summary>
    static void ApplyUpdates(List<(string name, string tag)> updates, int index)
    {
        if (index >= updates.Count) return;
        (string name, string tag) = updates[index];
        var addRequest = Client.Add($"{RepoGitUrl}?path=Packages/{name}#{tag}");

        void Poll()
        {
            if (!addRequest.IsCompleted) return;
            EditorApplication.update -= Poll;

            if (addRequest.Status == StatusCode.Success)
                Debug.Log($"[UpdateChecker] Updated {name} to {tag}.");
            else
                Debug.LogError($"[UpdateChecker] Update of {name} to {tag} failed: {addRequest.Error?.message}");
            ApplyUpdates(updates, index + 1);
        }
        EditorApplication.update += Poll;
    }

    static List<PackageInfo> FindGitPackages() =>
        PackageInfo.GetAllRegisteredPackages()
            .Where(p => p.name.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase) && p.source == PackageSource.Git)
            .ToList();

    /// <summary>
    /// Minimal extraction of GitHub's tags API response — we only need each tag's "name" field,
    /// so a small regex avoids pulling in a JSON library for one string per array element.
    /// </summary>
    static List<string> ExtractTagNames(string tagsJson) =>
        Regex.Matches(tagsJson, "\"name\"\\s*:\\s*\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();

    static Version ParseSemVer(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        string trimmed = tag.TrimStart('v', 'V');
        return Version.TryParse(trimmed, out Version version) ? version : null;
    }

    static DateTime GetLastCheckTime()
    {
        string raw = EditorPrefs.GetString(LastCheckPrefKey, "");
        return long.TryParse(raw, out long ticks) ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
    }

    static void SetLastCheckTime(DateTime utc) =>
        EditorPrefs.SetString(LastCheckPrefKey, utc.Ticks.ToString());
}
