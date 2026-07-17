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
    /// Fetches GitHub release tags, auto-discovers the Options catalog from namespaced tags
    /// (<c>&lt;shortName&gt;/&lt;semver&gt;</c> — tagged packages only), and drives Install / Update /
    /// Remove. Available updates show on the package cards themselves — no "update available" popup.
    /// Background checks (per-package auto-update interval) and the "Check For Updates" menu item
    /// only refresh tag data so the cards can update in place.
    ///
    /// Install / Update call <c>Client.Add</c> with a pinned tag URL; Remove calls <c>Client.Remove</c>.
    /// Local <c>file:</c> references are listed (Remove available) but have nothing meaningful to
    /// compare for updates.
    /// </summary>
    [InitializeOnLoad]
    public static class UpdateChecker
    {
        internal const string PackageNamePrefix = "com.hk.modtools.";
        const string RepoTagsApiUrl = "https://api.github.com/repos/shakee2/HK_EditorScripts/tags";
        internal const string RepoGitUrl = "https://github.com/shakee2/HK_EditorScripts.git";
        // Raw content base for reading a package.json at a specific tag (for not-installed deps).
        const string RepoRawBase = "https://raw.githubusercontent.com/shakee2/HK_EditorScripts";
        const string MenuPath = "Tools/shakee's Tools/Check For Updates";

        const string LastCheckPrefPrefix = "HKModTools.UpdateChecker.LastCheckTicks.";
        const string AutoUpdatePrefPrefix = "HKModTools.UpdateChecker.AutoUpdate.";
        const string IntervalPrefPrefix = "HKModTools.UpdateChecker.IntervalHours.";
        // Survives the domain reload Client.Add usually triggers, so a Shared→tool install chain can finish.
        const string PendingInstallsPref = "HKModTools.UpdateChecker.PendingInstalls";
        // SessionState survives domain reload (clears when the Editor quits) so cards keep knowing
        // latest tags after Remove/Install without waiting for another Check All.
        const string CachedTagsSessionKey = "HKModTools.UpdateChecker.CachedTagNames";
        // Last time the repo tag list was fetched successfully — throttles the on-open auto-refresh.
        const string LastTagsFetchPref = "HKModTools.UpdateChecker.LastTagsFetchTicks";
        // How stale the tag cache may be before opening the Options window auto-refreshes it.
        static readonly TimeSpan OpenRefreshMaxAge = TimeSpan.FromDays(1);
        internal const double DefaultIntervalHours = 24 * 14; // bi-weekly by default; editable per-package in the Options window

        const string SharedPackageName = "com.hk.modtools.shared";
        const string SharedShortName = "shared";
        const string DisplayNamePrefix = "HK Mod Tools - ";

        /// <summary>
        /// Packages shown in Options — discovered from namespaced git tags
        /// (<c>&lt;shortName&gt;/&lt;semver&gt;</c>) on the repo. Untagged packages never appear.
        /// Display name / description / author / deps come from each package's <c>package.json</c>
        /// (installed <see cref="PackageInfo"/> or fetched at the latest tag). Shared is always
        /// listed first when present.
        /// </summary>
        internal static IReadOnlyList<CatalogEntry> Catalog => GetOrBuildCatalog();

        static List<CatalogEntry> discoveredCatalog;

        /// <summary>A resolved <c>com.hk.modtools.*</c> dependency (min version) read from a manifest.</summary>
        internal sealed class Dep
        {
            public string PackageName;
            public Version MinVersion;
        }

        internal sealed class CatalogEntry
        {
            public readonly string Name;
            public string DisplayName;
            public string Description;
            public readonly bool IsFoundation;
            public string ShortName => Name.Substring(PackageNamePrefix.Length);

            public CatalogEntry(string name, string displayName, string description, bool isFoundation = false)
            {
                Name = name;
                DisplayName = displayName;
                Description = description;
                IsFoundation = isFoundation;
            }
        }

        /// <summary>One row in the Options window's package list (catalog entry + install/tag state).</summary>
        internal sealed class PackageRow
        {
            public CatalogEntry Catalog;
            public PackageInfo Installed; // null if not installed
            public string LatestTag;      // e.g. "core/1.0.1"; null if no release tag yet
            public Version LatestVersion;
            public bool TagsKnown;        // false until a successful tags fetch
            public string StatusMessage;  // "Checking…", fetch error, etc.
            public string Author;         // from PackageInfo or fetched package.json; may be null/empty

            // Dependencies read from the package's own manifest: PackageInfo when installed, or the
            // package.json at LatestTag when not (null until that fetch completes).
            public List<Dep> Dependencies;
            public bool DependenciesKnown;

            public bool IsInstalled => Installed != null;
            public bool IsGit => Installed != null && Installed.source == PackageSource.Git;
            public bool IsLocal => Installed != null && Installed.source == PackageSource.Local;
            public bool HasRelease => !string.IsNullOrEmpty(LatestTag) && LatestVersion != null;

            public bool UpdateAvailable
            {
                get
                {
                    if (!IsGit || !HasRelease) return false;
                    Version installed = ParseSemVer(Installed.version);
                    return installed != null && LatestVersion > installed;
                }
            }

            /// <summary>True when an installed package declares a dependency that's missing or below min version.</summary>
            public bool DependencyBumpNeeded
            {
                get
                {
                    if (!IsInstalled || Dependencies == null) return false;
                    foreach (var dep in Dependencies)
                    {
                        var pkg = PackageInfo.GetAllRegisteredPackages()
                            .FirstOrDefault(p => p.name == dep.PackageName);
                        if (pkg == null) return true;
                        Version v = ParseSemVer(pkg.version);
                        if (v == null || v < dep.MinVersion) return true;
                    }
                    return false;
                }
            }

            public bool CanUpdate => UpdateAvailable || DependencyBumpNeeded;
        }

        static bool startupCheckDone;
        static bool tagsFetchInFlight;
        static bool packageOpInFlight;
        static string packageOpLabel;
        static List<string> cachedTagNames;
        static string tagsFetchError;
        static Action pendingAfterTags;

        // Parsed package.json fields at a given tag (key = tag, e.g. "core/1.0.1").
        // An entry present means "fetched"; absent = not fetched yet.
        sealed class ManifestFields
        {
            public List<Dep> Dependencies = new List<Dep>();
            public string Author;
            public string DisplayName;
            public string Description;
        }

        static readonly Dictionary<string, ManifestFields> manifestByTag = new Dictionary<string, ManifestFields>(StringComparer.Ordinal);
        static readonly HashSet<string> manifestFetchInFlight = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>True while a Client.Add / Client.Remove is running — Options disables action buttons.</summary>
        internal static bool IsBusy => packageOpInFlight || tagsFetchInFlight;
        internal static string BusyLabel => packageOpInFlight ? packageOpLabel : (tagsFetchInFlight ? "Fetching releases…" : null);

        /// <summary>Fired when catalog row state or busy flags change — Options window Repaints.</summary>
        internal static event Action StateChanged;

        static UpdateChecker()
        {
            RestoreTagCacheIfNeeded();
            EditorApplication.delayCall += ResumePendingInstallsIfAny;
            EditorApplication.update += RunStartupCheckOnce;
        }

        static void ResumePendingInstallsIfAny()
        {
            var pending = LoadPendingInstalls();
            if (pending.Count == 0) return;

            // Drop entries that already resolved (e.g. Shared installed before the reload).
            pending = pending.Where(p => !IsPackageInstalled(p.name)).ToList();
            if (pending.Count == 0)
            {
                ClearPendingInstalls();
                return;
            }
            SavePendingInstalls(pending);

            Debug.Log($"[UpdateChecker] Resuming pending install queue ({pending.Count} left) after domain reload.");
            RunAddQueue(pending, 0);
        }

        static void RunStartupCheckOnce()
        {
            EditorApplication.update -= RunStartupCheckOnce;
            if (startupCheckDone) return;
            startupCheckDone = true;

            // Don't fight a resume-in-progress install chain with a background tag refresh.
            if (packageOpInFlight || LoadPendingInstalls().Count > 0) return;

            var due = GetInstalledGitPackages()
                .Where(p => GetAutoUpdateEnabled(p.name)
                            && (DateTime.UtcNow - GetLastCheckTime(p.name)).TotalHours >= GetIntervalHours(p.name))
                .ToList();
            if (due.Count == 0) return;

            // Silent refresh — Options cards pick up newer tags via StateChanged; no popup.
            CheckAllNow(manual: false);
        }

        [MenuItem(MenuPath, false, 201)]
        static void CheckForUpdatesMenuItem()
        {
            CheckAllNow(manual: true);
            ToolsOptionsWindow.OpenFromMenu();
        }

        /// <summary>
        /// Fetches the repo tag list and stamps last-check times. Options cards update in place —
        /// no "updates available" dialog. Pass <paramref name="manual"/> true from UI/menu.
        /// </summary>
        public static void CheckAllNow(bool manual)
        {
            foreach (var p in GetInstalledGitPackages())
                SetLastCheckTime(p.name, DateTime.UtcNow);

            RefreshReleaseTags(onDone: null);
            if (manual && GetInstalledGitPackages().Count == 0 && !HasAnyHkPackage())
                Debug.Log("[UpdateChecker] No HK Mod Tools packages installed yet — use Options to install.");
        }

        static bool HasAnyHkPackage() =>
            PackageInfo.GetAllRegisteredPackages().Any(p => p.name.StartsWith(PackageNamePrefix, StringComparison.Ordinal));

        /// <summary>Queues every installed git package that has a newer release tag (plus any deps that need bumping).</summary>
        internal static void UpdateAllAvailable()
        {
            if (packageOpInFlight) return;

            EnsureTagsThen(() =>
            {
                var targets = BuildPackageRows().Where(r => r.IsInstalled && r.CanUpdate).Select(r => r.Catalog.Name).ToList();
                if (targets.Count == 0) return;

                EnsureManifestsForAll(targets, () =>
                {
                    var merged = new List<(string name, string tag)>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var name in targets)
                    {
                        if (!TryBuildResolvedQueue(name, includeTargetInstall: false, out var part, out _))
                            continue;
                        foreach (var item in part)
                        {
                            if (seen.Add(item.name))
                                merged.Add(item);
                        }
                    }

                    if (merged.Count == 0) return;
                    RunAddQueue(merged, 0);
                });
            });
        }

        static void EnsureManifestsForAll(List<string> packageNames, Action onReady)
        {
            int remaining = packageNames.Count;
            if (remaining == 0) { onReady(); return; }
            foreach (var name in packageNames)
                EnsureManifestsThen(name, () => { if (--remaining == 0) onReady(); });
        }

        // ── Options-window catalog ───────────────────────────────────────────────────────────────

        static IReadOnlyList<CatalogEntry> GetOrBuildCatalog()
        {
            RestoreTagCacheIfNeeded();
            if (discoveredCatalog != null) return discoveredCatalog;
            discoveredCatalog = BuildCatalogFromTags(cachedTagNames);
            return discoveredCatalog;
        }

        static void InvalidateCatalog() => discoveredCatalog = null;

        /// <summary>
        /// Builds the Options catalog from namespaced tags only (<c>shortName/semver</c>).
        /// Before tags are known, returns a Shared-only placeholder so the window isn't empty.
        /// </summary>
        static List<CatalogEntry> BuildCatalogFromTags(List<string> tags)
        {
            var shortNames = new HashSet<string>(StringComparer.Ordinal);
            if (tags != null)
            {
                foreach (string tag in tags)
                {
                    int slash = tag.IndexOf('/');
                    if (slash <= 0 || slash >= tag.Length - 1) continue;
                    string shortName = tag.Substring(0, slash);
                    if (ParseSemVer(tag.Substring(slash + 1)) == null) continue;
                    // Ignore legacy un-namespaced junk and path-like noise.
                    if (shortName.IndexOfAny(new[] { '/', '\\', ' ' }) >= 0) continue;
                    shortNames.Add(shortName);
                }
            }

            // Tags not loaded yet — keep Shared visible so Options still has a card.
            if (tags == null)
                shortNames.Add(SharedShortName);

            var entries = new List<CatalogEntry>();
            foreach (string sn in shortNames
                         .OrderBy(s => s.Equals(SharedShortName, StringComparison.Ordinal) ? "" : s,
                             StringComparer.Ordinal))
            {
                string name = sn.Equals(SharedShortName, StringComparison.Ordinal)
                    ? SharedPackageName
                    : PackageNamePrefix + sn;
                bool foundation = sn.Equals(SharedShortName, StringComparison.Ordinal);
                string display = PrettyShortName(sn);
                string description = foundation
                    ? "Foundation mounts + Update Checker + Options window. Required by every other package."
                    : "";

                if (tags != null)
                {
                    var (latestTag, _) = FindLatestTag(tags, sn);
                    if (latestTag != null && manifestByTag.TryGetValue(latestTag, out var fields))
                    {
                        if (!string.IsNullOrEmpty(fields.DisplayName))
                            display = ShortDisplayName(fields.DisplayName, sn);
                        if (!string.IsNullOrEmpty(fields.Description))
                            description = fields.Description;
                    }
                }

                entries.Add(new CatalogEntry(name, display, description, foundation));
            }

            return entries;
        }

        static string PrettyShortName(string shortName)
        {
            if (string.IsNullOrEmpty(shortName)) return shortName;
            // orphanfinder → Orphanfinder is ugly; leave as-is for unknown packages until manifest loads.
            // shared/core are fine capitalized:
            return char.ToUpperInvariant(shortName[0]) + shortName.Substring(1);
        }

        /// <summary>Strips the common "HK Mod Tools - " UPM displayName prefix for card headlines.</summary>
        static string ShortDisplayName(string displayName, string shortNameFallback)
        {
            if (string.IsNullOrEmpty(displayName)) return PrettyShortName(shortNameFallback);
            if (displayName.StartsWith(DisplayNamePrefix, StringComparison.Ordinal))
                return displayName.Substring(DisplayNamePrefix.Length);
            return displayName;
        }

        /// <summary>
        /// Builds one row per catalog entry from currently-registered packages + the last successful
        /// tags fetch (or a "Checking…" placeholder while a fetch is in flight). Call
        /// <see cref="RefreshReleaseTags"/> to (re)fetch.
        /// </summary>
        internal static List<PackageRow> BuildPackageRows()
        {
            RestoreTagCacheIfNeeded();
            var catalog = GetOrBuildCatalog();

            var installed = PackageInfo.GetAllRegisteredPackages()
                .Where(p => p.name.StartsWith(PackageNamePrefix, StringComparison.Ordinal))
                .ToDictionary(p => p.name, StringComparer.Ordinal);

            var rows = new List<PackageRow>(catalog.Count);
            foreach (var entry in catalog)
            {
                installed.TryGetValue(entry.Name, out var pkg);
                var row = new PackageRow { Catalog = entry, Installed = pkg };

                if (cachedTagNames != null)
                {
                    // Prefer last-known tags even while a refresh is in flight (avoids "no release yet"
                    // after Remove → domain reload before the new fetch finishes).
                    row.TagsKnown = true;
                    var (tag, version) = FindLatestTag(cachedTagNames, entry.ShortName);
                    row.LatestTag = tag;
                    row.LatestVersion = version;
                    if (tag == null)
                        row.StatusMessage = "No release tag yet";
                    else if (tagsFetchInFlight)
                        row.StatusMessage = null; // keep showing cached release while refreshing
                }
                else if (tagsFetchInFlight)
                {
                    row.StatusMessage = "Checking…";
                }
                else if (!string.IsNullOrEmpty(tagsFetchError))
                {
                    row.StatusMessage = tagsFetchError;
                }
                else
                {
                    row.StatusMessage = "Release info not loaded";
                }

                PopulateManifestFields(row);
                rows.Add(row);
            }

            return rows;
        }

        /// <summary>
        /// Refreshes the tag list only if it hasn't been fetched within the last day (used by the
        /// Options window on open so re-opening it repeatedly doesn't hit GitHub each time). Manual
        /// Check All still forces a fetch via <see cref="RefreshReleaseTags"/>.
        /// </summary>
        internal static void RefreshReleaseTagsIfStale()
        {
            if (tagsFetchInFlight) return;
            if (cachedTagNames != null && (DateTime.UtcNow - GetLastTagsFetchTime()) < OpenRefreshMaxAge)
            {
                NotifyStateChanged(); // repaint with the cached data; no network hit
                return;
            }
            RefreshReleaseTags();
        }

        /// <summary>Fetches the repo tag list (once; coalesces concurrent callers). Invokes <paramref name="onDone"/> when finished.</summary>
        internal static void RefreshReleaseTags(Action onDone = null)
        {
            if (onDone != null) pendingAfterTags += onDone;

            if (tagsFetchInFlight)
            {
                NotifyStateChanged();
                return;
            }

            tagsFetchInFlight = true;
            tagsFetchError = null;
            NotifyStateChanged();

            var request = UnityWebRequest.Get(RepoTagsApiUrl);
            request.SetRequestHeader("User-Agent", "HK-ModTools-UpdateChecker");
            var op = request.SendWebRequest();

            void Poll()
            {
                if (!op.isDone) return;
                EditorApplication.update -= Poll;

                using (request)
                {
                    tagsFetchInFlight = false;
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        tagsFetchError = "Could not fetch releases: " + request.error;
                        // Keep any previously cached tags so cards don't fall back to "no release yet".
                        if (cachedTagNames == null)
                            Debug.LogWarning("[UpdateChecker] " + tagsFetchError);
                    }
                    else
                    {
                        tagsFetchError = null;
                        SaveTagCache(ExtractTagNames(request.downloadHandler.text));
                        SetLastTagsFetchTime(DateTime.UtcNow);
                    }
                }

                var callbacks = pendingAfterTags;
                pendingAfterTags = null;
                NotifyStateChanged();
                callbacks?.Invoke();
            }
            EditorApplication.update += Poll;
        }

        // ── Dependencies (read from manifests, not hardcoded) ────────────────────────────────────

        /// <summary>
        /// Fills dependencies / author on the row and refreshes catalog display name / description
        /// from <see cref="PackageInfo"/> when installed, otherwise from the package.json at the
        /// row's latest tag (fetched lazily, cached per tag).
        /// </summary>
        static void PopulateManifestFields(PackageRow row)
        {
            if (row.IsInstalled)
            {
                row.Dependencies = ReadInstalledDeps(row.Installed);
                row.DependenciesKnown = true;
                row.Author = row.Installed.author.name;
                if (!string.IsNullOrEmpty(row.Installed.displayName))
                    row.Catalog.DisplayName = ShortDisplayName(row.Installed.displayName, row.Catalog.ShortName);
                if (!string.IsNullOrEmpty(row.Installed.description))
                    row.Catalog.Description = row.Installed.description;
                return;
            }

            if (!row.HasRelease)
            {
                row.Dependencies = new List<Dep>();
                row.DependenciesKnown = true; // nothing to install → nothing to require
                return;
            }

            if (manifestByTag.TryGetValue(row.LatestTag, out var fields))
            {
                row.Dependencies = fields.Dependencies;
                row.DependenciesKnown = true;
                row.Author = fields.Author;
                if (!string.IsNullOrEmpty(fields.DisplayName))
                    row.Catalog.DisplayName = ShortDisplayName(fields.DisplayName, row.Catalog.ShortName);
                if (!string.IsNullOrEmpty(fields.Description))
                    row.Catalog.Description = fields.Description;
            }
            else
            {
                row.Dependencies = null;
                row.DependenciesKnown = false;
                FetchManifest(row.Catalog.Name, row.LatestTag); // async; NotifyStateChanged on completion
            }
        }

        static List<Dep> ReadInstalledDeps(PackageInfo info)
        {
            var list = new List<Dep>();
            if (info.dependencies == null) return list;
            foreach (var d in info.dependencies)
            {
                if (d.name == null || !d.name.StartsWith(PackageNamePrefix, StringComparison.Ordinal)) continue;
                Version v = ParseSemVer(d.version);
                if (v != null) list.Add(new Dep { PackageName = d.name, MinVersion = v });
            }
            return list;
        }

        static string ManifestUrl(string packageName, string tag) =>
            $"{RepoRawBase}/{tag}/Packages/{packageName}/package.json";

        static void FetchManifest(string packageName, string tag, Action onDone = null)
        {
            if (manifestByTag.ContainsKey(tag))
            {
                onDone?.Invoke();
                return;
            }
            if (!manifestFetchInFlight.Add(tag))
            {
                // Already fetching this tag; poll until it lands, then fire the callback.
                if (onDone != null)
                {
                    void Wait()
                    {
                        if (manifestFetchInFlight.Contains(tag)) return;
                        EditorApplication.update -= Wait;
                        onDone();
                    }
                    EditorApplication.update += Wait;
                }
                return;
            }

            var request = UnityWebRequest.Get(ManifestUrl(packageName, tag));
            request.SetRequestHeader("User-Agent", "HK-ModTools-UpdateChecker");
            var op = request.SendWebRequest();

            void Poll()
            {
                if (!op.isDone) return;
                EditorApplication.update -= Poll;
                using (request)
                {
                    manifestFetchInFlight.Remove(tag);
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        manifestByTag[tag] = ParseManifest(request.downloadHandler.text);
                        InvalidateCatalog(); // pick up displayName/description from the new manifest
                    }
                    else
                        // Cache an empty entry so we don't hammer a missing/renamed manifest each Repaint.
                        manifestByTag[tag] = new ManifestFields();
                }
                NotifyStateChanged();
                onDone?.Invoke();
            }
            EditorApplication.update += Poll;
        }

        /// <summary>
        /// Parses displayName / description / author + <c>com.hk.modtools.*</c> dependency entries
        /// from a package.json (small regexes; we don't need a full JSON parser for these fields).
        /// </summary>
        static ManifestFields ParseManifest(string json)
        {
            var fields = new ManifestFields();
            if (string.IsNullOrEmpty(json)) return fields;

            var display = Regex.Match(json, "\"displayName\"\\s*:\\s*\"([^\"]+)\"");
            if (display.Success) fields.DisplayName = display.Groups[1].Value;

            var description = Regex.Match(json, "\"description\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (description.Success)
                fields.Description = UnescapeJsonString(description.Groups[1].Value);

            // "author": { "name": "…" } or "author": "…"
            var authorObj = Regex.Match(json, "\"author\"\\s*:\\s*\\{[^}]*\"name\"\\s*:\\s*\"([^\"]+)\"");
            if (authorObj.Success)
                fields.Author = authorObj.Groups[1].Value;
            else
            {
                var authorStr = Regex.Match(json, "\"author\"\\s*:\\s*\"([^\"]+)\"");
                if (authorStr.Success) fields.Author = authorStr.Groups[1].Value;
            }

            var block = Regex.Match(json, "\"dependencies\"\\s*:\\s*\\{([^}]*)\\}", RegexOptions.Singleline);
            string body = block.Success ? block.Groups[1].Value : "";
            foreach (Match m in Regex.Matches(body, "\"(com\\.hk\\.modtools\\.[^\"]+)\"\\s*:\\s*\"([^\"]+)\""))
            {
                Version v = ParseSemVer(m.Groups[2].Value);
                if (v != null) fields.Dependencies.Add(new Dep { PackageName = m.Groups[1].Value, MinVersion = v });
            }
            return fields;
        }

        static string UnescapeJsonString(string s) =>
            s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");

        /// <summary>
        /// Ensures the package.json manifests needed to resolve an install/update of
        /// <paramref name="packageName"/> are cached (target's latest tag + transitively any
        /// not-installed dependency's latest tag), then invokes <paramref name="onReady"/>.
        /// </summary>
        static void EnsureManifestsThen(string packageName, Action onReady)
        {
            var rows = BuildPackageRows().ToDictionary(r => r.Catalog.Name, StringComparer.Ordinal);
            var missing = new List<(string pkg, string tag)>();
            var visited = new HashSet<string>(StringComparer.Ordinal);

            void Walk(string name)
            {
                if (!rows.TryGetValue(name, out var r) || !r.HasRelease) return;
                if (!visited.Add(r.LatestTag)) return;
                if (!manifestByTag.TryGetValue(r.LatestTag, out var fields))
                {
                    missing.Add((name, r.LatestTag)); // can't walk its deps until fetched
                    return;
                }
                foreach (var d in fields.Dependencies) Walk(d.PackageName);
            }

            Walk(packageName);

            if (missing.Count == 0)
            {
                onReady();
                return;
            }

            int remaining = missing.Count;
            foreach (var (pkg, tag) in missing)
                FetchManifest(pkg, tag, () =>
                {
                    if (--remaining == 0)
                        EnsureManifestsThen(packageName, onReady); // re-walk; newly-fetched deps may reveal more
                });
        }

        /// <summary>
        /// Installs <paramref name="packageName"/> at its latest release tag. Missing dependencies
        /// (read from the manifest) are installed first; installed deps below the required min
        /// version are updated first. Shared itself can't be bootstrapped from Options (Options lives
        /// in Shared) — if Shared is missing, the user is told to add it via Package Manager.
        /// </summary>
        internal static void InstallLatest(string packageName)
        {
            if (packageOpInFlight) return;

            EnsureTagsThen(() =>
            {
                var row = BuildPackageRows().FirstOrDefault(r => r.Catalog.Name == packageName);
                if (row == null) return;

                if (row.IsInstalled)
                {
                    EditorUtility.DisplayDialog("Install", $"{row.Catalog.DisplayName} is already installed.", "OK");
                    return;
                }

                if (!row.HasRelease)
                {
                    EditorUtility.DisplayDialog("Install",
                        $"{row.Catalog.DisplayName} has no release tag yet (experimental packages stay untagged until ready).\n\n" +
                        "Install via Package Manager with a branch pin only if you're developing that tool.", "OK");
                    return;
                }

                EnsureManifestsThen(packageName, () =>
                {
                    if (!TryBuildResolvedQueue(packageName, includeTargetInstall: true, out var queue, out string error))
                    {
                        EditorUtility.DisplayDialog("Install", error, "OK");
                        return;
                    }

                    string summary = string.Join("\n", queue.Select(q => $"  • {q.name} @ {q.tag}"));
                    if (!EditorUtility.DisplayDialog("Install package",
                            "Install / update:\n\n" + summary + "\n\nUnity will download and compile; expect a domain reload.", "Install", "Cancel"))
                        return;

                    RunAddQueue(queue, 0);
                });
            });
        }

        /// <summary>
        /// Updates an installed git package to its latest release tag, also bumping any catalog
        /// dependencies that are below their required min version (no confirm — the card already
        /// shows the target).
        /// </summary>
        internal static void UpdateToLatest(string packageName)
        {
            if (packageOpInFlight) return;

            EnsureTagsThen(() =>
            {
                var row = BuildPackageRows().FirstOrDefault(r => r.Catalog.Name == packageName);
                if (row == null || !row.IsInstalled || !row.CanUpdate) return;

                EnsureManifestsThen(packageName, () =>
                {
                    if (!TryBuildResolvedQueue(packageName, includeTargetInstall: false, out var queue, out string error))
                    {
                        EditorUtility.DisplayDialog("Update", error, "OK");
                        return;
                    }

                    if (queue.Count == 0) return;
                    RunAddQueue(queue, 0);
                });
            });
        }

        /// <summary>
        /// Builds an ordered Client.Add queue: dependency installs/updates first, then the target.
        /// When <paramref name="includeTargetInstall"/> is true the target is always queued (install).
        /// When false, the target is queued only if it needs it (newer tag or a dep bump).
        /// Dependency requirements come from the manifests (installed <see cref="PackageInfo"/> or the
        /// fetched package.json at each package's latest tag) — call <see cref="EnsureManifestsThen"/>
        /// first so they're cached.
        /// </summary>
        static bool TryBuildResolvedQueue(
            string packageName,
            bool includeTargetInstall,
            out List<(string name, string tag)> queue,
            out string error)
        {
            // Locals (not out-params) so nested local functions can assign them — CS1628.
            var built = new List<(string name, string tag)>();
            string errorMsg = null;
            queue = built;
            error = null;

            var rows = BuildPackageRows().ToDictionary(r => r.Catalog.Name, StringComparer.Ordinal);
            if (!rows.TryGetValue(packageName, out var target))
            {
                error = "Unknown package: " + packageName;
                return false;
            }

            var queued = new HashSet<string>(StringComparer.Ordinal);

            bool Enqueue(PackageRow row, string reason)
            {
                if (row == null || queued.Contains(row.Catalog.Name)) return true;
                if (row.IsLocal)
                {
                    errorMsg = $"{row.Catalog.DisplayName} is a local file: reference — can't install/update it from Options ({reason}).";
                    return false;
                }
                if (!row.HasRelease)
                {
                    errorMsg = $"{row.Catalog.DisplayName} has no release tag to install/update from ({reason}).";
                    return false;
                }
                queued.Add(row.Catalog.Name);
                built.Add((row.Catalog.Name, row.LatestTag));
                return true;
            }

            // Requirements the package being installed/updated declares at its latest tag.
            List<Dep> DepsAtLatest(PackageRow row) =>
                row.HasRelease && manifestByTag.TryGetValue(row.LatestTag, out var fields)
                    ? fields.Dependencies
                    : new List<Dep>();

            bool ResolveDeps(PackageRow parent)
            {
                foreach (var dep in DepsAtLatest(parent))
                {
                    if (!rows.TryGetValue(dep.PackageName, out var depRow))
                    {
                        errorMsg = $"{parent.Catalog.DisplayName} depends on {dep.PackageName}, which isn't in the Options catalog.";
                        return false;
                    }

                    // Resolve the dependency's own deps first (tool→…→shared chains).
                    if (!ResolveDeps(depRow)) return false;

                    if (!depRow.IsInstalled)
                    {
                        // Options lives in Shared — can't bootstrap Shared from here.
                        if (depRow.Catalog.IsFoundation)
                        {
                            errorMsg =
                                "Shared isn't installed. Options runs inside Shared, so add it first via Package Manager:\n\n" +
                                $"{RepoGitUrl}?path=Packages/com.hk.modtools.shared#shared/<version>";
                            return false;
                        }
                        if (!Enqueue(depRow, "required dependency, not installed")) return false;
                        continue;
                    }

                    Version installed = ParseSemVer(depRow.Installed.version);
                    if (installed != null && installed >= dep.MinVersion) continue; // satisfied

                    // Installed but too old (or unparseable) — bump to latest release.
                    if (depRow.HasRelease && depRow.LatestVersion != null && depRow.LatestVersion < dep.MinVersion)
                    {
                        errorMsg =
                            $"{depRow.Catalog.DisplayName} must be ≥ v{dep.MinVersion} (required by {parent.Catalog.DisplayName}), " +
                            $"but the latest release is only v{depRow.LatestVersion}. Publish a newer tag.";
                        return false;
                    }
                    if (!Enqueue(depRow, $"requires ≥ v{dep.MinVersion}, installed v{depRow.Installed.version}"))
                        return false;
                }
                return true;
            }

            if (!ResolveDeps(target))
            {
                error = errorMsg;
                return false;
            }

            if (includeTargetInstall)
            {
                if (!Enqueue(target, "requested install"))
                {
                    error = errorMsg;
                    return false;
                }
            }
            else if (target.UpdateAvailable)
            {
                if (!Enqueue(target, "requested update"))
                {
                    error = errorMsg;
                    return false;
                }
            }

            error = null;
            return true;
        }

        /// <summary>Removes an installed package via <c>Client.Remove</c>, with foundation/dependent warnings.</summary>
        internal static void RemovePackage(string packageName)
        {
            if (packageOpInFlight) return;

            var row = BuildPackageRows().FirstOrDefault(r => r.Catalog.Name == packageName);
            if (row == null || !row.IsInstalled) return;

            if (row.Catalog.IsFoundation)
            {
                var dependents = BuildPackageRows()
                    .Where(r => r.IsInstalled && !r.Catalog.IsFoundation)
                    .Select(r => r.Catalog.DisplayName)
                    .ToList();
                if (dependents.Count > 0)
                {
                    string list = string.Join(", ", dependents);
                    if (!EditorUtility.DisplayDialog("Remove Shared",
                            "Shared is required by other installed HK Mod Tools packages:\n\n  " + list +
                            "\n\nRemoving it will break those packages until Shared is reinstalled. Continue?",
                            "Remove anyway", "Cancel"))
                        return;
                }
                else if (!EditorUtility.DisplayDialog("Remove Shared",
                             "Remove Shared? Other tool packages need it — reinstall before adding them again.",
                             "Remove", "Cancel"))
                    return;
            }
            else if (!EditorUtility.DisplayDialog("Remove package",
                         $"Remove {row.Catalog.DisplayName} ({row.Catalog.Name})?\n\nYou can reinstall it later from this window.",
                         "Remove", "Cancel"))
                return;

            packageOpInFlight = true;
            packageOpLabel = $"Removing {row.Catalog.DisplayName}…";
            NotifyStateChanged();

            var removeRequest = Client.Remove(packageName);
            void Poll()
            {
                if (!removeRequest.IsCompleted) return;
                EditorApplication.update -= Poll;
                packageOpInFlight = false;
                packageOpLabel = null;

                if (removeRequest.Status == StatusCode.Success)
                    Debug.Log($"[UpdateChecker] Removed {packageName}.");
                else
                    Debug.LogError($"[UpdateChecker] Remove of {packageName} failed: {removeRequest.Error?.message}");

                NotifyStateChanged();
            }
            EditorApplication.update += Poll;
        }

        static void EnsureTagsThen(Action action)
        {
            RestoreTagCacheIfNeeded();
            if (cachedTagNames != null)
            {
                action();
                return;
            }
            RefreshReleaseTags(action);
        }

        static void SaveTagCache(List<string> tags)
        {
            cachedTagNames = tags;
            SessionState.SetString(CachedTagsSessionKey,
                tags == null || tags.Count == 0 ? "" : string.Join("\n", tags));
            InvalidateCatalog();
        }

        static void RestoreTagCacheIfNeeded()
        {
            if (cachedTagNames != null) return;
            string raw = SessionState.GetString(CachedTagsSessionKey, "");
            if (string.IsNullOrEmpty(raw)) return;
            cachedTagNames = raw.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        static void RunAddQueue(List<(string name, string tag)> queue, int index)
        {
            if (index >= queue.Count)
            {
                ClearPendingInstalls();
                packageOpInFlight = false;
                packageOpLabel = null;
                NotifyStateChanged();
                return;
            }

            // Persist the remainder so a domain reload mid-chain can resume (Shared→tool installs).
            SavePendingInstalls(queue.Skip(index).ToList());

            var (name, tag) = queue[index];
            string url = $"{RepoGitUrl}?path=Packages/{name}#{tag}";
            packageOpInFlight = true;
            packageOpLabel = $"Installing {name} @ {tag}…";
            NotifyStateChanged();

            var addRequest = Client.Add(url);
            void Poll()
            {
                if (!addRequest.IsCompleted) return;
                EditorApplication.update -= Poll;

                if (addRequest.Status == StatusCode.Success)
                    Debug.Log($"[UpdateChecker] Installed/updated {name} to {tag}.");
                else
                {
                    Debug.LogError($"[UpdateChecker] Install of {name} @ {tag} failed: {addRequest.Error?.message}");
                    ClearPendingInstalls();
                    packageOpInFlight = false;
                    packageOpLabel = null;
                    NotifyStateChanged();
                    return; // stop the queue on failure
                }

                // Domain reload usually kills this process mid-queue; ResumePendingInstallsIfAny
                // picks up the saved remainder. If no reload happens, continue in-process.
                RunAddQueue(queue, index + 1);
            }
            EditorApplication.update += Poll;
        }

        static List<(string name, string tag)> LoadPendingInstalls()
        {
            string raw = EditorPrefs.GetString(PendingInstallsPref, "");
            if (string.IsNullOrEmpty(raw)) return new List<(string, string)>();
            var list = new List<(string, string)>();
            foreach (string part in raw.Split('|'))
            {
                int hash = part.IndexOf('#');
                if (hash <= 0 || hash >= part.Length - 1) continue;
                list.Add((part.Substring(0, hash), part.Substring(hash + 1)));
            }
            return list;
        }

        static void SavePendingInstalls(List<(string name, string tag)> queue) =>
            EditorPrefs.SetString(PendingInstallsPref,
                string.Join("|", queue.Select(q => q.name + "#" + q.tag)));

        static void ClearPendingInstalls() => EditorPrefs.DeleteKey(PendingInstallsPref);

        static bool IsPackageInstalled(string packageName) =>
            PackageInfo.GetAllRegisteredPackages().Any(p => p.name == packageName);

        static void NotifyStateChanged() => StateChanged?.Invoke();

        static List<PackageInfo> GetInstalledGitPackages() =>
            PackageInfo.GetAllRegisteredPackages()
                .Where(p => p.name.StartsWith(PackageNamePrefix, StringComparison.Ordinal) && p.source == PackageSource.Git)
                .OrderBy(p => p.name, StringComparer.Ordinal)
                .ToList();

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

        static DateTime GetLastTagsFetchTime()
        {
            string raw = EditorPrefs.GetString(LastTagsFetchPref, "");
            return long.TryParse(raw, out long ticks) ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
        }

        static void SetLastTagsFetchTime(DateTime utc) =>
            EditorPrefs.SetString(LastTagsFetchPref, utc.Ticks.ToString());
    }
}
