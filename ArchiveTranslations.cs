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

        if (AssetDatabase.IsMounted(ProviderName))
        {
            foreach (var provider in AssetDatabase.AllProviders)
                if (provider.Name == ProviderName) { s_provider = provider; break; }
            if (s_provider != null) return true;
        }

        string bundlePath = BundlePath;
        if (!File.Exists(bundlePath))
        {
            error = s_lastError = $"Translations bundle not found at: {bundlePath}";
            return false;
        }

        bool ok = AssetDatabase.TryMountAssetBundle(ProviderName, bundlePath, 0u, out s_provider, Amplitude.Framework.Asset.AssetBundle.Options.None);
        if (!ok) error = s_lastError = $"Failed to mount translations bundle: {bundlePath}";
        else s_lastError = null;
        return ok;
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
        foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:LocalizedStringTranslationCollection"))
        {
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            var coll = UnityEditor.AssetDatabase.LoadAssetAtPath<LocalizedStringTranslationCollection>(path);
            if (coll == null) continue;
            coll.Initialize();
            foreach (var translation in coll.Translations)
            {
                string key = translation.LocalizationLine.Id;
                if (!string.IsNullOrEmpty(key)) result[key] = translation.LocalizationLine.Body;
            }
        }
        return result;
    }

    static Dictionary<string, string> BuildVanillaCache()
    {
        if (s_vanillaCache != null) return s_vanillaCache;
        s_vanillaCache = new Dictionary<string, string>(48000);
        if (!TryMount(out _)) return s_vanillaCache;

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
        if (chosenLang == null) return s_vanillaCache;

        foreach (var d in byLanguage[chosenLang])
        {
            var coll = s_provider.LoadAsset<LocalizedStringTranslationCollection>(d);
            if (coll == null) continue;
            coll.Initialize();
            foreach (var translation in coll.Translations)
            {
                string key = translation.LocalizationLine.Id;
                if (!string.IsNullOrEmpty(key)) s_vanillaCache[key] = translation.LocalizationLine.Body;
            }
        }
        return s_vanillaCache;
    }
}
