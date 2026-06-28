using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amplitude.Framework.Asset;
using Amplitude.Framework.Localization;
using UnityEngine;

/// <summary>
/// Mounts the Mod Editor's own archive translations bundle the same way its Localization
/// Window does (Amplitude.Mercury.Production.Modification.LocalizationEditor) and exposes a
/// flat key -> text lookup, e.g. "%Technology_Era4_07Title" -> "Gunpowder". This bundle ships
/// with the ModTools install itself (Assets/Editor/Resources/Translations), unlike
/// MercuryDatabases which is mounted from the Humankind game install — so it has nothing to
/// do with VanillaDatabaseMount even though both are vanilla-style asset bundle mounts.
/// </summary>
public static class ArchiveTranslations
{
    const string BundleFolderPath = "Assets/Editor/Resources/Translations";
    const string BundleFileName = "Mercury.Modding.Translations.assetbundle";
    const string PreferredLanguageCode = "en-US";

    static string ProviderName => BundleFileName.ToLowerInvariant();

    static IAssetProvider s_provider;
    static string s_lastError;
    static Dictionary<string, string> s_vanillaCache;  // vanilla bundle only — built once, never changes mid-session

    public static bool IsMounted => s_provider != null;
    public static string LastError => s_lastError;

    public static string BundlePath => Path.GetFullPath(Path.Combine(BundleFolderPath, BundleFileName));

    public static bool TryMount(out string error)
    {
        error = null;
        if (s_provider != null) return true;

        // The translations bundle is owned by the Mod Editor's own Localization Window
        // (Amplitude.Mercury.Production.Modification.LocalizationEditor). On a cold start
        // it may not be mounted yet, so we mount it ourselves; after a domain reload our
        // static s_provider is wiped, so we recover Amplitude's already-registered provider
        // by name first.
        //
        // IMPORTANT: never call AssetDatabase.Refresh() here. After a reload the native Unity
        // AssetBundle may still be loaded while Amplitude's provider registry is transient.
        // Refresh() forces Amplitude's DatatableElementCache.BuildLocalizationCache to rebuild
        // (it walks every provider via LoadAllMainAssetsOfType), and against a half-registered
        // translations provider that throws a NullReferenceException that breaks every
        // DatatableElementReference drawer in the inspector (CTRL+R / Refresh hit the same
        // Amplitude path). Re-mounting when the native bundle is already loaded throws
        // "another AssetBundle with the same files is already loaded" and leaves the provider
        // half-registered — the exact state that makes the cache rebuild NRE. So: recover by
        // name if available; otherwise attempt a fresh mount (cold start only, when nothing is
        // loaded yet) wrapped so the "already loaded" throw can't propagate or trigger Refresh.
        if (AssetDatabase.IsMounted(ProviderName))
        {
            foreach (var provider in AssetDatabase.AllProviders)
                if (provider.Name == ProviderName) { s_provider = provider; break; }
            if (s_provider != null) { s_lastError = null; return true; }
            // Mounted per IsMounted but the provider isn't in AllProviders yet — transient
            // right after a reload. Don't poison s_lastError and don't re-mount (the native
            // bundle is loaded; re-mounting would throw). Return false so the caller retries.
            error = null;
            return false;
        }

        // IsMounted is false: nothing is registered for this name, so a fresh mount is safe
        // (no "already loaded" conflict). This is the cold-start path.
        string bundlePath = BundlePath;
        if (!File.Exists(bundlePath))
        {
            error = s_lastError = $"Translations bundle not found at: {bundlePath}";
            return false;
        }
        try
        {
            bool ok = AssetDatabase.TryMountAssetBundle(ProviderName, bundlePath, 0u, out s_provider, Amplitude.Framework.Asset.AssetBundle.Options.None);
            if (ok) { s_lastError = null; return true; }
            error = s_lastError = $"Failed to mount translations bundle: {bundlePath}";
            return false;
        }
        catch (System.Exception e)
        {
            // The only way to get here on the cold-start path is an unexpected error; the
            // "already loaded" case is ruled out by the IsMounted check above. Do NOT call
            // Refresh() — that would trigger Amplitude's localization-cache rebuild against
            // the half-registered provider and NRE the whole inspector. Just report and fail
            // softly; the caller retries next frame via the delayed retry / Reload button.
            error = s_lastError = $"Translations bundle mount threw: {e.Message}";
            return false;
        }
    }

    public static void Invalidate()
    {
        s_provider = null;
        s_vanillaCache = null;
    }

    /// <summary>
    /// Flat localization key (e.g. "%Technology_Era4_07Title") -> translated text, for one
    /// language. Vanilla rows come from the mounted archive bundle (cached — vanilla never
    /// changes mid-session); project-level overrides (anything the Localization Window has
    /// written into the project, e.g. Assets/Localization/Translations/Translations.asset)
    /// are re-scanned on every call so edits show up immediately, and win on key collisions.
    /// </summary>
    public static Dictionary<string, string> BuildKeyToTextDict()
    {
        var result = new Dictionary<string, string>(BuildVanillaCache());

        // Don't walk the project translation collections unless the archive bundle is
        // actually mounted. Calling coll.Initialize() on a LocalizedStringTranslationCollection
        // pokes Amplitude's DatatableElementCache (BuildLocalizationCacheIFN), and when the
        // translations provider is in the stale/broken state left by a CTRL+R refresh while
        // the bundle's native AssetBundle is half-loaded, that cache rebuild NREs inside
        // LoadAllMainAssetsOfType — spamming the console and breaking every
        // DatatableElementReference inspector drawer. If we can't mount, return just the
        // (empty) vanilla cache and let labels fall back to keys instead of triggering that
        // cascade. The next successful mount (cold start / Localization Window re-open) will
        // repopulate everything.
        if (!IsMounted) return result;

        foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:LocalizedStringTranslationCollection"))
        {
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            var coll = UnityEditor.AssetDatabase.LoadAssetAtPath<LocalizedStringTranslationCollection>(path);
            if (coll == null) continue;
            try { coll.Initialize(); }
            catch (System.Exception e) { Debug.LogWarning($"[ArchiveTranslations] Initialize() threw for '{path}' (provider likely stale): {e.Message}"); continue; }
            foreach (var translation in coll.Translations)
            {
                string key = translation.LocalizationLine.Id;
                if (!string.IsNullOrEmpty(key)) result[key] = translation.LocalizationLine.Body;
            }
        }
        return result;
    }

    // ── Project-level override writes ──────────────────────────────────────────
    // Same destination the Mod Editor's own Localization Window writes to: one
    // writable LocalizedStringTranslationCollection in the project, keyed by Id.
    // Overrides aren't tied to the vanilla shard a key came from (unlike Descriptor
    // overrides) — any project collection with a matching Id wins, so get-or-create
    // a single canonical one here instead of mirroring per-shard paths.
    const string OverrideDir  = "Assets/Localization/Translations";
    const string OverrideName = "Translations";

    // The Mod Editor's Localization Window uses DatatableElementCollectionUtility to
    // get-or-create this collection, and that utility resolves an existing collection via
    // Amplitude's in-memory DatatableElement registry (NOT by file path). After a domain
    // reload that registry can be in a half-cleared state where the .asset exists on disk
    // but the registry has no entry — the utility then falls through to its create branch,
    // whose internal path computation yields "" when the registry is stale, producing
    // "Creating asset at path  failed" (empty path). The direct AssetDatabase lookup
    // below sidesteps the registry entirely on the (common) "asset already exists" path,
    // so we only call the utility for the genuine first-time creation, where we also make
    // sure the destination folder exists first (the utility produces an empty path when the
    // folder doesn't exist either, instead of a clean "folder not found" error).
    static string OverrideAssetPath => OverrideDir + "/" + OverrideName + ".asset";

    static LocalizedStringTranslationCollection GetOrCreateOverrideCollection()
    {
        // Fast/common path: the .asset already exists — load it directly, bypassing the
        // Amplitude registry. This survives domain reloads and stale-registry states.
        var existing = UnityEditor.AssetDatabase.LoadAssetAtPath<LocalizedStringTranslationCollection>(OverrideAssetPath);
        if (existing != null)
        {
            existing.Initialize();
            return existing;
        }

        // Slow/first-time path: make sure the folder exists, then let the utility create.
        EnsureFolderExists(OverrideDir);
        try
        {
            var collection = Amplitude.Framework.Utility.DatatableElementCollectionUtility
                .GetOrCreateDatatableElementCollection(typeof(LocalizedStringTranslationCollection), OverrideDir, OverrideName, startNameEditing: false);
            var typed = collection as LocalizedStringTranslationCollection;
            if (typed == null) Debug.LogError($"[ArchiveTranslations] Failed to get-or-create override collection '{OverrideDir}/{OverrideName}'.");
            return typed;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ArchiveTranslations] GetOrCreateDatatableElementCollection threw for '{OverrideDir}/{OverrideName}' (folder exists={UnityEditor.AssetDatabase.IsValidFolder(OverrideDir)}): {e}");
            return null;
        }
    }

    // Creates the folder chain up to `folderPath` (e.g. "Assets/Localization/Translations")
    // using AssetDatabase.CreateFolder, which also creates the .meta Unity needs. Mirrors
    // what the Mod Editor's Localization Window does implicitly before creating its asset.
    static void EnsureFolderExists(string folderPath)
    {
        if (UnityEditor.AssetDatabase.IsValidFolder(folderPath)) return;

        // Walk from "Assets" outward, creating each missing segment.
        var parts = folderPath.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!UnityEditor.AssetDatabase.IsValidFolder(next))
                UnityEditor.AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    /// <summary>Finds the project-level override row for <paramref name="key"/>, if any.</summary>
    /// <remarks>Checks the canonical override collection (by path) first — fast and
    /// registry-independent — then falls back to a project-wide scan for any other
    /// LocalizedStringTranslationCollection a key may have been written to (e.g. by the
    /// Mod Editor's own Localization Window into a different shard).</remarks>
    public static bool TryGetOverride(string key, out LocalizedStringTranslation row, out LocalizedStringTranslationCollection owner)
    {
        row = null; owner = null;
        // Bail when the archive bundle isn't mounted: coll.Initialize() (called inside
        // TryGetOverrideInCollection) pokes Amplitude's DatatableElementCache, which NREs
        // when the translations provider is in the stale state left by a CTRL+R refresh.
        // No provider => no usable override lookup; report "not imported" and let the UI
        // show the Import button instead of triggering the cache-rebuild cascade.
        if (!IsMounted) return false;

        if (TryGetOverrideInCollection(UnityEditor.AssetDatabase.LoadAssetAtPath<LocalizedStringTranslationCollection>(OverrideAssetPath), key, out row, out owner))
            return true;

        foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:LocalizedStringTranslationCollection"))
        {
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            if (path == OverrideAssetPath) continue;   // already checked
            var coll = UnityEditor.AssetDatabase.LoadAssetAtPath<LocalizedStringTranslationCollection>(path);
            if (TryGetOverrideInCollection(coll, key, out row, out owner)) return true;
        }
        return false;
    }

    static bool TryGetOverrideInCollection(LocalizedStringTranslationCollection coll, string key,
        out LocalizedStringTranslation row, out LocalizedStringTranslationCollection owner)
    {
        row = null; owner = null;
        if (coll == null) return false;
        try { coll.Initialize(); }
        catch (System.Exception e) { Debug.LogWarning($"[ArchiveTranslations] Initialize() threw while looking up '{key}' (provider likely stale): {e.Message}"); return false; }
        foreach (var t in coll.Translations)
            if (t.LocalizationLine.Id == key) { row = t; owner = coll; return true; }
        return false;
    }

    public static bool HasOverride(string key) => !string.IsNullOrEmpty(key) && TryGetOverride(key, out _, out _);

    /// <summary>Creates a project override row for <paramref name="key"/> (seeded with <paramref name="seedText"/>) if one doesn't already exist, and returns it either way.</summary>
    public static LocalizedStringTranslation EnsureOverride(string key, string seedText)
    {
        if (TryGetOverride(key, out var existing, out _)) return existing;

        var coll = GetOrCreateOverrideCollection();
        if (coll == null) return null;

        // Collection-level factory (handles the element's identity/registration within the
        // collection) rather than a bare `new LocalizedStringTranslation()` + list Add.
        // TryGetOverride above already ruled out an existing row, so plain Create is right.
        if (coll.CreateDatatableElement(typeof(LocalizedStringTranslation), key) is not LocalizedStringTranslation row)
        {
            Debug.LogError($"[ArchiveTranslations] Failed to create translation row for '{key}'.");
            return null;
        }
        row.LocalizationLine.Id = key;
        row.LocalizationLine.Body = seedText ?? "";
        row.SetEditable(true);
        // Mark the collection dirty and let Unity persist it through its normal save flow
        // (focus loss / editor save). We deliberately do NOT call AssetDatabase.SaveAssets()
        // here: doing so synchronously inside the Import button's GUI event triggers
        // Amplitude's datatable-collection save handler, which pops a confusing
        // "Couldn't create asset file!" dialog. The in-memory row is already created and
        // registered, so the change takes effect immediately regardless of when the disk
        // write happens.
        UnityEditor.EditorUtility.SetDirty(coll);
        return row;
    }

    /// <summary>Writes new text into an existing override row. No-op if the key hasn't been imported yet (see <see cref="EnsureOverride"/>).</summary>
    public static void SetOverrideText(string key, string text)
    {
        if (!TryGetOverride(key, out var row, out var owner)) return;
        row.LocalizationLine.Body = text ?? "";
        UnityEditor.EditorUtility.SetDirty(owner);
    }

    static Dictionary<string, string> BuildVanillaCache()
    {
        // Only return a cached result when it's actually populated. A previous failed
        // TryMount left s_vanillaCache as a non-null but EMPTY dict — caching that would
        // make every later call return empty forever (labels stuck on keys) even after the
        // provider recovers. So treat an empty cache as "not built yet" and retry the mount.
        if (s_vanillaCache != null && s_vanillaCache.Count > 0) return s_vanillaCache;
        s_vanillaCache = new Dictionary<string, string>(48000);
        if (!TryMount(out _)) return s_vanillaCache;

        // The provider may be "mounted" per IsMounted/AllProviders but still in a stale,
        // broken state after a CTRL+R refresh (native bundle half-loaded). Touching it can
        // NRE inside LoadAllMainAssetsOfType / AddAllAssetDescriptors and corrupt Amplitude's
        // DatatableElementCache, which then NREs every DatatableElementReference inspector
        // drawer on each repaint. Wrap the provider access so a broken provider degrades to
        // an empty cache (keys fallback) instead of poisoning the editor.
        try
        {
            BuildVanillaCacheFromProvider();
        }
        catch (System.Exception e)
        {
            // Drop the (likely half-populated) cache and the provider ref so the next call
            // re-attempts the mount from scratch instead of re-using the broken provider.
            s_provider = null;
            s_vanillaCache = null;
            Debug.LogWarning($"[ArchiveTranslations] Vanilla cache build failed (provider likely stale after refresh): {e.Message}");
        }
        return s_vanillaCache ?? (s_vanillaCache = new Dictionary<string, string>(48000));
    }

    static void BuildVanillaCacheFromProvider()
    {
        var descriptors = new List<AssetDescriptor>();
        s_provider.AddAllAssetDescriptors(descriptors, AssetProviderOption.AskForType);

        // Translations are sharded across many collections per language; filename is shaped
        // "<languageCode>,<hash>" — group by language, then load every shard of the chosen one.
        var byLanguage = new Dictionary<string, List<AssetDescriptor>>();
        foreach (var d in descriptors)
        {
            var t = d.GetAssetType();
            if (t == null || !typeof(LocalizedStringTranslationCollection).IsAssignableFrom(t)) continue;
            string fileName = d.FileName;
            int comma = fileName.IndexOf(',');
            string lang = comma > 0 ? fileName.Substring(0, comma) : fileName;
            if (!byLanguage.TryGetValue(lang, out var list)) byLanguage[lang] = list = new List<AssetDescriptor>();
            list.Add(d);
        }

        string chosenLang = byLanguage.ContainsKey(PreferredLanguageCode) ? PreferredLanguageCode : byLanguage.Keys.FirstOrDefault();
        if (chosenLang == null) return;

        foreach (var d in byLanguage[chosenLang])
        {
            var coll = s_provider.LoadAsset<LocalizedStringTranslationCollection>(d);
            if (coll == null) continue;
            try { coll.Initialize(); }
            catch (System.Exception e) { Debug.LogWarning($"[ArchiveTranslations] Initialize() threw for shard '{d.FileName}' (provider likely stale): {e.Message}"); continue; }
            foreach (var translation in coll.Translations)
            {
                string key = translation.LocalizationLine.Id;
                if (!string.IsNullOrEmpty(key)) s_vanillaCache[key] = translation.LocalizationLine.Body;
            }
        }
    }
}
