using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amplitude.Framework.Asset;
using Amplitude.Framework.Runtime;
using UnityEngine;
using AssetDatabase = Amplitude.Framework.Asset.AssetDatabase;
using AmpAssetBundle = Amplitude.Framework.Asset.AssetBundle;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Session-scoped mounts for Compat Patcher mod <c>.assetbundle</c> sources.
    /// Never mounts/unmounts vanilla (<c>mercurydatabases.assetbundle</c>).
    ///
    /// Amplitude's <see cref="AmpAssetBundle"/> always sets <c>Name = Path.GetFileName(path)</c>;
    /// <c>TryMountAssetBundle(providerName, …)</c> only uses <paramref name="providerName"/> for
    /// lookup — Mount registers under the filename. So we must adopt/reuse by filename, remount
    /// only when missing/stale, and never double-Mount the same leaf name.
    /// </summary>
    public static class CompatBundleMounts
    {
        const string VanillaProvider = "mercurydatabases.assetbundle";

        public class Entry
        {
            public string Path;
            public string ProviderName; // Amplitude key = filename of the bundle
            public string Label;
            public bool Stale;
        }

        static readonly Dictionary<string, Entry> s_byPath =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Amplitude provider name for a bundle path (= filename).</summary>
        public static string ProviderNameFor(string absolutePath)
        {
            string full = NormalizePath(absolutePath);
            return Path.GetFileName(full);
        }

        /// <summary>Mount if needed; reuse when already mounted and healthy for this path.</summary>
        public static IAssetProvider EnsureMounted(string absolutePath, string label = null)
        {
            string full = NormalizePath(absolutePath);
            if (string.IsNullOrEmpty(full) || !File.Exists(full))
                throw new FileNotFoundException("Mod assetbundle not found: " + absolutePath);

            string providerName = ProviderNameFor(full);
            if (string.Equals(providerName, VanillaProvider, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Refusing to mount vanilla databases as a mod source.");

            // Fast path: we already tracked this absolute path and the provider is healthy.
            if (s_byPath.TryGetValue(full, out var tracked))
            {
                if (!string.IsNullOrEmpty(label)) tracked.Label = label;
                if (IsHealthy(tracked))
                    return FindProvider(tracked.ProviderName);
                // Stale tracking — drop registry entry; may still need to unmount below.
                s_byPath.Remove(full);
            }

            // Amplitude already has this filename mounted (us, Mod Tools, leftover) — adopt if same path.
            var existing = FindProvider(providerName);
            if (existing != null)
            {
                if (IsPhantom(existing))
                {
                    try { AssetDatabase.UnmountAssetBundle(providerName); } catch { /* phantom */ }
                    existing = null;
                }
                else if (existing is AmpAssetBundle ab)
                {
                    string mountedPath = NormalizePath(ab.Path);
                    if (!string.Equals(mountedPath, full, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Assetbundle filename '{providerName}' is already mounted from a different path:\n"
                            + $"  mounted: {ab.Path}\n"
                            + $"  wanted:  {full}\n"
                            + "Amplitude keys providers by filename only — unload the other mount first "
                            + "(Loaded mod bundles → Force unload), or rename one of the files.");
                    }
                    Track(full, providerName, label);
                    return existing;
                }
                else
                {
                    Track(full, providerName, label);
                    return existing;
                }
            }

            // Not mounted — mount. Lookup key MUST be the filename (what Mount will register).
            if (!AssetDatabase.TryMountAssetBundle(providerName, full, uint.MaxValue, out var provider,
                    Amplitude.Framework.Asset.AssetBundle.Options.None) || provider == null)
                throw new InvalidDataException("Failed to mount assetbundle: " + full);

            Track(full, provider.Name ?? providerName, label);
            return provider;
        }

        public static bool TryGetProvider(string absolutePath, out IAssetProvider provider)
        {
            provider = null;
            string full = NormalizePath(absolutePath);
            if (!s_byPath.TryGetValue(full, out var e) || !IsHealthy(e)) return false;
            provider = FindProvider(e.ProviderName);
            return provider != null;
        }

        public static IReadOnlyList<Entry> ListOurs()
        {
            RefreshStaleFlags();
            return s_byPath.Values
                .OrderBy(e => e.Label ?? e.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Only mounts the Compat Patcher tracked (mod sources added + Compare). Never lists the
        /// game's own FX/UI/terrain/<c>Mercury.Data.*</c> bundles from <c>Humankind/AssetBundles/</c>.
        /// </summary>
        public static IReadOnlyList<Entry> ListMountedCompatProviders() => ListOurs();

        public static void ForceUnload(string providerNameOrPath)
        {
            if (string.IsNullOrEmpty(providerNameOrPath)) return;

            string providerName = providerNameOrPath;
            string full = NormalizePath(providerNameOrPath);
            if (s_byPath.TryGetValue(full, out var byPath))
                providerName = byPath.ProviderName;
            else if (File.Exists(full))
                providerName = ProviderNameFor(full);
            // else treat argument as provider name (filename)

            if (string.Equals(providerName, VanillaProvider, StringComparison.OrdinalIgnoreCase))
                return;

            // Never unload the game's own AssetBundles (FX, UI, terrain, Mercury.Data.*, …).
            bool weTracked = s_byPath.Values.Any(e =>
                string.Equals(e.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
            if (!weTracked)
            {
                var p = FindProvider(providerName);
                string mountedPath = p is AmpAssetBundle ab ? ab.Path : full;
                if (IsHumankindGameBundlePath(mountedPath))
                {
                    Debug.LogWarning("[CompatPatcher] Refusing to unload game AssetBundle: " + providerName);
                    return;
                }
            }

            try { if (AssetDatabase.IsMounted(providerName)) AssetDatabase.UnmountAssetBundle(providerName); }
            catch { /* already gone */ }

            var drop = s_byPath.Where(kv =>
                    string.Equals(kv.Value.ProviderName, providerName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kv.Key, full, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).ToList();
            foreach (var k in drop) s_byPath.Remove(k);
        }

        public static void UnloadAllOurs()
        {
            foreach (var name in s_byPath.Values.Select(e => e.ProviderName).Distinct().ToList())
                ForceUnload(name);
            s_byPath.Clear();
        }

        static void Track(string fullPath, string providerName, string label)
        {
            s_byPath[fullPath] = new Entry
            {
                Path = fullPath,
                ProviderName = providerName,
                Label = label ?? Path.GetFileNameWithoutExtension(fullPath),
                Stale = false,
            };
        }

        static void RefreshStaleFlags()
        {
            foreach (var e in s_byPath.Values)
                e.Stale = !IsHealthy(e);
        }

        static bool IsHealthy(Entry e)
        {
            if (e == null || string.IsNullOrEmpty(e.ProviderName)) return false;
            if (!AssetDatabase.IsMounted(e.ProviderName)) return false;
            var p = FindProvider(e.ProviderName);
            if (p == null || IsPhantom(p)) return false;
            if (p is AmpAssetBundle ab)
            {
                string mounted = NormalizePath(ab.Path);
                if (!string.Equals(mounted, e.Path, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        static bool IsPhantom(IAssetProvider provider)
        {
            if (provider == null) return true;
            try
            {
                var descriptors = new List<AssetDescriptor>();
                provider.AddAllAssetDescriptors(descriptors, AssetProviderOption.AskForType);
                return false;
            }
            catch
            {
                return true;
            }
        }

        static IAssetProvider FindProvider(string name) =>
            AssetDatabase.AllProviders.FirstOrDefault(p =>
                p != null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try { return Path.GetFullPath(path).Replace('\\', '/'); }
            catch { return path.Replace('\\', '/'); }
        }

        /// <summary>
        /// True for bundles under the retail game's <c>…/Humankind/AssetBundles/…</c> tree
        /// (FX, UI, terrain, Mercury.Data.*, etc.) — not workshop/mod paths.
        /// </summary>
        static bool IsHumankindGameBundlePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string n = path.Replace('\\', '/');
            if (n.IndexOf("/workshop/", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return n.IndexOf("/Humankind/AssetBundles/", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("/Humankind\\AssetBundles/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Best-effort display name for a mod source. For <c>.assetbundle</c>, loads the sibling
        /// RuntimeModule UnityFS (same stem, no extension — what the game uses) and returns
        /// <c>RuntimeModule.name</c>. Falls back to stripping the workshop
        /// <c>.{guid}.{ver}</c> suffix from the filename.
        /// </summary>
        public static string SuggestModName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "mod";
            path = path.TrimEnd('/', '\\');
            if (Directory.Exists(path))
                return Path.GetFileName(path);

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".assetbundle")
            {
                string fromModule = TryReadRuntimeModuleName(path);
                if (!string.IsNullOrEmpty(fromModule)) return fromModule;
                return StripWorkshopFilename(Path.GetFileNameWithoutExtension(path));
            }
            return Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>
        /// Workshop layout: <c>Mod Name.{guid}.{ver}</c> (no extension) next to
        /// <c>mod name.{guid}.{ver}.assetbundle</c>. That companion file is a Unity AssetBundle
        /// holding the <see cref="RuntimeModule"/> the game registers by <c>.name</c>.
        /// </summary>
        public static string TryReadRuntimeModuleName(string assetBundlePath)
        {
            try
            {
                string full = NormalizePath(assetBundlePath);
                string dir = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

                string stem = Path.GetFileNameWithoutExtension(full); // strips .assetbundle
                string companion = Directory.EnumerateFiles(dir)
                    .FirstOrDefault(f =>
                    {
                        string name = Path.GetFileName(f);
                        return string.Equals(name, stem, StringComparison.OrdinalIgnoreCase)
                               && !name.EndsWith(".assetbundle", StringComparison.OrdinalIgnoreCase)
                               && !name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase);
                    });
                if (companion == null)
                {
                    // Exact stem without extension may be stored with different casing as a
                    // separate file that still matches after normalizing.
                    companion = Path.Combine(dir, stem);
                    if (!File.Exists(companion)) return null;
                }

                var unityBundle = UnityEngine.AssetBundle.LoadFromFile(companion);
                if (unityBundle == null) return null;
                try
                {
                    foreach (var obj in unityBundle.LoadAllAssets())
                    {
                        if (obj is RuntimeModule rm && !string.IsNullOrEmpty(rm.name))
                            return rm.name;
                    }
                    // Type may not resolve in some editor setups — match by class name.
                    foreach (var obj in unityBundle.LoadAllAssets())
                    {
                        if (obj != null && obj.GetType().Name == "RuntimeModule"
                            && !string.IsNullOrEmpty(obj.name))
                            return obj.name;
                    }
                }
                finally
                {
                    unityBundle.Unload(true);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CompatPatcher] Could not read RuntimeModule name from " + assetBundlePath + ": " + e.Message);
            }
            return null;
        }

        /// <summary>Strip trailing <c>.{32-hex-guid}(.{digits})*</c> workshop versioning from a file stem.</summary>
        public static string StripWorkshopFilename(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return stem;
            // e.g. "vip modpack.3c459bbeb2e744ba9e50a11f59680687.5.53" → "vip modpack"
            var m = System.Text.RegularExpressions.Regex.Match(
                stem, @"^(.*)\.[0-9a-fA-F]{32}(?:\.\d+)*$");
            return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value.Trim() : stem;
        }
    }
}
