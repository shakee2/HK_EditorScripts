using System;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Networking;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

/// <summary>
/// Checks the package's GitHub repo for a newer version tag than what's currently resolved via
/// UPM, and offers to update in place. Runs a throttled background check on editor load
/// (once per <see cref="CheckIntervalHours"/>) plus an on-demand menu item.
///
/// Only meaningful when the consuming project's manifest.json points at a git URL (e.g.
/// "https://github.com/shakee2/HK_EditorScripts.git#1.0.0"), not a local "file:" reference —
/// updating rewrites that dependency's revision via the Package Manager Client API, which then
/// does the actual git fetch/checkout itself (no separate "git pull" needed).
/// </summary>
[InitializeOnLoad]
public static class UpdateChecker
{
    const string PackageName = "com.shakee.hk-editorscripts";
    const string RepoTagsApiUrl = "https://api.github.com/repos/shakee2/HK_EditorScripts/tags";
    const string RepoGitUrl = "https://github.com/shakee2/HK_EditorScripts.git";
    const string MenuPath = "Tools/shakee's Tools/Check For Updates";

    const string LastCheckPrefKey = "HKEditorScripts.UpdateChecker.LastCheckTicks";
    const string SkippedVersionPrefKey = "HKEditorScripts.UpdateChecker.SkippedVersion";
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

        if (!IsGitPackage())
            return; // nothing to check against when consuming via a local "file:" reference

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

            string latestTag = FindLatestSemVerTag(request.downloadHandler.text);
            if (latestTag == null)
            {
                if (manual) EditorUtility.DisplayDialog("Check For Updates", "No version tags found on the repository yet.", "OK");
                return;
            }

            Version latestVersion = ParseSemVer(latestTag);
            Version currentVersion = GetInstalledVersion();

            if (currentVersion != null && latestVersion <= currentVersion)
            {
                if (manual) EditorUtility.DisplayDialog("Check For Updates", $"You're up to date (v{currentVersion}).", "OK");
                return;
            }

            if (!manual && EditorPrefs.GetString(SkippedVersionPrefKey, "") == latestTag)
                return; // user chose "Skip This Version" for this exact tag already

            string currentLabel = currentVersion != null ? $"v{currentVersion}" : "unknown";
            int choice = EditorUtility.DisplayDialogComplex(
                "HK Editor Scripts Update Available",
                $"A newer version is available: {latestTag} (current: {currentLabel}).\n\nUpdate now?",
                "Update", "Later", "Skip This Version");

            if (choice == 0) ApplyUpdate(latestTag);
            else if (choice == 2) EditorPrefs.SetString(SkippedVersionPrefKey, latestTag);
        }
    }

    static void ApplyUpdate(string tag)
    {
        var addRequest = Client.Add($"{RepoGitUrl}#{tag}");

        void Poll()
        {
            if (!addRequest.IsCompleted) return;
            EditorApplication.update -= Poll;

            if (addRequest.Status == StatusCode.Success)
                Debug.Log($"[UpdateChecker] Updated {PackageName} to {tag}.");
            else
                Debug.LogError($"[UpdateChecker] Update to {tag} failed: {addRequest.Error?.message}");
        }
        EditorApplication.update += Poll;
    }

    static bool IsGitPackage()
    {
        PackageInfo pkg = FindPackage();
        return pkg != null && pkg.source == PackageSource.Git;
    }

    static Version GetInstalledVersion()
    {
        PackageInfo pkg = FindPackage();
        return pkg != null ? ParseSemVer(pkg.version) : null;
    }

    static PackageInfo FindPackage() =>
        PackageInfo.GetAllRegisteredPackages().FirstOrDefault(p => p.name == PackageName);

    /// <summary>
    /// Minimal extraction of GitHub's tags API response — we only need each tag's "name" field,
    /// so a small regex avoids pulling in a JSON library for one string per array element.
    /// </summary>
    static string FindLatestSemVerTag(string tagsJson)
    {
        return Regex.Matches(tagsJson, "\"name\"\\s*:\\s*\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Select(name => (name, version: ParseSemVer(name)))
            .Where(t => t.version != null)
            .OrderByDescending(t => t.version)
            .Select(t => t.name)
            .FirstOrDefault();
    }

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
