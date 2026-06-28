using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amplitude.Framework.Asset;
using Amplitude.Mercury.Production.Modification;
using UnityEngine;

/// <summary>
/// Mounts the vanilla "MercuryDatabases" asset bundle the same way the Mod Editor
/// window does (Amplitude.Mercury.Production.Modification.ModuleEditor), so the
/// vanilla database ScriptableObjects can be browsed without copying them into the
/// project (no more Assets/VanillaReference). Source path is derived from the
/// "Humankind Folder" already configured in the Mod Editor (ModuleEditor.MercuryFolderPath).
/// </summary>
public static class VanillaDatabaseMount
{
    const string BundleFolderName = "MercuryDatabases";
    const string ProviderName = "mercurydatabases.assetbundle";

    static IAssetProvider s_provider;
    static string s_lastError;

    // Every vanilla object handed out by LoadAllOfType is remembered here against the
    // AssetDescriptor of the collection that owns it (main asset or sub-asset alike), since
    // that descriptor's FilePath is the same vanilla-mirrored path the Mod Editor's own
    // "Override from Archives" importer uses as the override destination.
    static readonly Dictionary<UnityEngine.Object, AssetDescriptor> s_ownerDescriptor = new();

    public static bool IsMounted => s_provider != null;
    public static string LastError => s_lastError;

    public static string BundlePath
    {
        get
        {
            string mercuryFolder = ModuleEditor.MercuryFolderPath;
            if (string.IsNullOrEmpty(mercuryFolder)) return null;
            return Path.Combine(mercuryFolder, "AssetBundles", BundleFolderName, ProviderName);
        }
    }

    public static bool TryMount(out string error)
    {
        error = null;
        if (s_provider != null) return true;

        if (AssetDatabase.IsMounted(ProviderName))
        {
            foreach (var provider in AssetDatabase.AllProviders)
            {
                if (provider.Name == ProviderName) { s_provider = provider; break; }
            }
            if (s_provider != null) return true;
        }

        string mercuryFolder = ModuleEditor.MercuryFolderPath;
        if (string.IsNullOrEmpty(mercuryFolder))
        {
            error = s_lastError = "Humankind folder is not configured (set it in Mercury/Mod Editor).";
            return false;
        }

        string bundlePath = BundlePath;
        if (!File.Exists(bundlePath))
        {
            error = s_lastError = $"Vanilla databases bundle not found at: {bundlePath}";
            return false;
        }

        try
        {
            bool ok = AssetDatabase.TryMountAssetBundle(ProviderName, bundlePath, uint.MaxValue, out s_provider, Amplitude.Framework.Asset.AssetBundle.Options.None);
            if (!ok)
            {
                error = s_lastError = $"Failed to mount vanilla databases bundle: {bundlePath}";
                return false;
            }
            s_lastError = null;
            return true;
        }
        catch (Exception ex)
        {
            error = s_lastError = $"Exception mounting vanilla databases bundle: {ex.Message}";
            return false;
        }
    }

    /// <summary>Re-mounts on next call (use after the Humankind folder setting changes).</summary>
    public static void Invalidate()
    {
        s_provider = null;
        s_ownerDescriptor.Clear();
    }

    /// <summary>
    /// Every vanilla asset assignable to <paramref name="t"/>, loaded straight from the mounted
    /// bundle. The bundle's top-level assets are all *Collection containers (e.g.
    /// TechnologyDefinitionCollection); the actual rows live as sub-assets inside them — same
    /// shape as Unity's own LoadAllAssetsAtPath(mainAsset + subAssets), so both are checked.
    /// </summary>
    public static IEnumerable<UnityEngine.Object> LoadAllOfType(Type t)
    {
        if (!TryMount(out _)) yield break;

        var descriptors = new List<AssetDescriptor>();
        s_provider.AddAllAssetDescriptors(descriptors, AssetProviderOption.AskForType);
        foreach (var descriptor in descriptors)
        {
            var assetType = descriptor.GetAssetType();
            if (assetType != null && t.IsAssignableFrom(assetType))
            {
                var obj = s_provider.LoadAsset<UnityEngine.Object>(descriptor);
                if (obj != null) { s_ownerDescriptor[obj] = descriptor; yield return obj; }
            }

            foreach (var sub in s_provider.FetchAllSubAssetsOfType(descriptor.Guid, t))
            {
                if (sub == null) continue;
                s_ownerDescriptor[sub] = descriptor;
                yield return sub;
            }
        }
    }

    /// <summary>
    /// The AssetDescriptor of the collection (main asset or owning sub-asset container) that a
    /// vanilla object loaded via <see cref="LoadAllOfType"/> came from. Its FilePath mirrors the
    /// path the vanilla data lives at — the same path the Mod Editor's "Override from Archives"
    /// importer uses as the destination when overriding that element.
    /// </summary>
    public static bool TryGetOwnerDescriptor(UnityEngine.Object vanillaObject, out AssetDescriptor descriptor)
        => s_ownerDescriptor.TryGetValue(vanillaObject, out descriptor);

    /// <summary>True if <paramref name="o"/> was loaded from the mounted vanilla provider (as opposed to a project asset).</summary>
    public static bool IsVanillaAsset(UnityEngine.Object o)
        => o != null && s_provider != null && s_provider.TryGetAssetGuid(o, out _);

    [UnityEditor.MenuItem("Tools/Tech Tree/Debug Vanilla Mount")]
    static void DebugMount()
    {
        if (!TryMount(out var error)) { Debug.LogError($"[VanillaMount] {error}"); return; }
        var descriptors = new List<AssetDescriptor>();
        s_provider.AddAllAssetDescriptors(descriptors, AssetProviderOption.AskForType);
        int withType = 0, withoutType = 0;
        var typeCounts = new Dictionary<string, int>();
        foreach (var d in descriptors)
        {
            var t = d.GetAssetType();
            if (t == null) { withoutType++; continue; }
            withType++;
            typeCounts[t.FullName] = typeCounts.TryGetValue(t.FullName, out var c) ? c + 1 : 1;
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[VanillaMount] provider='{s_provider.Name}' descriptors={descriptors.Count} withType={withType} withoutType={withoutType}");
        if (descriptors.Count > 0)
            sb.AppendLine("sample FilePath: " + descriptors[0].FilePath + "  TypeAsString: " + descriptors[0].TypeAsString);
        foreach (var kv in typeCounts.OrderByDescending(kv => kv.Value).Take(20))
            sb.AppendLine($"  [{kv.Value,5}] {kv.Key}");
        Debug.Log(sb.ToString());
    }
}
