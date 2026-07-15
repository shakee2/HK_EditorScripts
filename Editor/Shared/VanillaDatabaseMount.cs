using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amplitude.Framework.Asset;
using Amplitude.Mercury.Production.Modification;
using UnityEngine;

sealed class NarrativeNreFilter : ILogHandler
{
    readonly ILogHandler _inner;
    public NarrativeNreFilter(ILogHandler inner) { _inner = inner; }

    public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        => _inner.LogFormat(logType, context, format, args);

    public void LogException(Exception exception, UnityEngine.Object context)
    {
        if (exception is NullReferenceException
            && exception.StackTrace != null
            && exception.StackTrace.Contains("NarrativeEventDefinition.EnumerateSimulationEventVariables"))
            return;
        _inner.LogException(exception, context);
    }
}

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

    static bool MountBundle(string bundlePath, out IAssetProvider provider)
    {
        var original = Debug.unityLogger.logHandler;
        Debug.unityLogger.logHandler = new NarrativeNreFilter(original);
        try
        {
            return AssetDatabase.TryMountAssetBundle(ProviderName, bundlePath, uint.MaxValue,
                out provider, Amplitude.Framework.Asset.AssetBundle.Options.None);
        }
        finally
        {
            Debug.unityLogger.logHandler = original;
        }
    }

    public static bool TryMount(out string error)
    {
        error = null;
        // Note: we intentionally do NOT force-remount here based on a "liveness" probe. Amplitude's
        // own AssetBundle.FixUnityAssetBundleLoadingState() already reloads a native handle that a mod
        // build unloaded, on the next access. Re-mounting the same file ourselves collides with the
        // Mod Editor's mount of that same bundle (double LoadFromFile) and yields a broken provider
        // (live UnityObject, null contentDescriptor) that then NREs on every refresh.
        if (s_provider != null) return true;

        if (AssetDatabase.IsMounted(ProviderName))
        {
            foreach (var provider in AssetDatabase.AllProviders)
            {
                if (provider.Name == ProviderName) { s_provider = provider; break; }
            }
            if (s_provider != null) return true;
            // Already mounted per AssetDatabase.IsMounted (e.g. a domain reload cleared our
            // static s_provider without the native bundle handle going with it) but not found
            // in AllProviders — this is the "vanilla references show as missing after a build"
            // state. Self-heal: force-unmount the phantom registration and mount fresh, rather
            // than bailing and requiring a full mod rebuild to clear it.
            return ForceRemount(out error);
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
            bool ok = MountBundle(bundlePath, out s_provider);
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
    /// Drops whatever AssetDatabase thinks is mounted under <see cref="ProviderName"/> (even if
    /// our own bookkeeping already lost track of it) and mounts the bundle fresh. This is the fix
    /// for "vanilla references show as missing" after a mod build — the build's domain reload can
    /// clear <see cref="s_provider"/> while AssetDatabase still reports the provider as mounted,
    /// so plain <see cref="TryMount"/> would otherwise bail forever until a full rebuild happened
    /// to clear that phantom state incidentally.
    /// </summary>
    public static bool ForceRemount(out string error)
    {
        try { AssetDatabase.UnmountAssetBundle(ProviderName); } catch { /* phantom handle — nothing to unmount */ }
        Invalidate();

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
            bool ok = MountBundle(bundlePath, out s_provider);
            if (!ok)
            {
                error = s_lastError = $"Force re-mount failed: {bundlePath}";
                return false;
            }
            error = s_lastError = null;
            return true;
        }
        catch (Exception ex)
        {
            error = s_lastError = $"Exception during force re-mount: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Fetches descriptors from <see cref="s_provider"/>, self-healing once if the fetch throws.
    /// The fetch can fail when our cached provider is a stale duplicate of the bundle the Mod Editor
    /// also has mounted (its native handle can end up with a null contentDescriptor). Rather than
    /// force-remounting — which unmounts the shared bundle and can break the Mod Editor's own mount,
    /// and collides on the next LoadFromFile — we just drop our cached reference and re-adopt via
    /// <see cref="TryMount"/>, which re-resolves the healthy provider already in AssetDatabase's
    /// provider list. Not an iterator method (unlike <see cref="LoadAllOfType"/>) so try/catch is legal.
    /// </summary>
    static bool TryFetchDescriptors(List<AssetDescriptor> descriptors)
    {
        try
        {
            s_provider.AddAllAssetDescriptors(descriptors, AssetProviderOption.AskForType);
            return true;
        }
        catch (Exception ex)
        {
            s_lastError = $"Vanilla databases fetch failed, re-adopting provider: {ex.Message}";
            Debug.LogWarning($"[VanillaMount] {s_lastError}");
            Invalidate();

            if (!TryMount(out var mountError))
            {
                s_lastError = $"Self-heal failed: {mountError}";
                Debug.LogError($"[VanillaMount] {s_lastError}");
                return false;
            }

            try
            {
                descriptors.Clear();
                s_provider.AddAllAssetDescriptors(descriptors, AssetProviderOption.AskForType);
                return true;
            }
            catch (Exception ex2)
            {
                s_lastError = $"Vanilla databases still unavailable after re-adopting provider: {ex2.Message}";
                Debug.LogError($"[VanillaMount] {s_lastError}");
                return false;
            }
        }
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
        if (!TryFetchDescriptors(descriptors)) yield break;

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
    {
        if (o == null || s_provider == null) return false;
        try { return s_provider.TryGetAssetGuid(o, out _); }
        catch (Exception ex)
        {
            // Same stale-provider condition as TryFetchDescriptors; don't crash the caller's GUI.
            s_lastError = $"Vanilla databases provider was stale during IsVanillaAsset: {ex.Message}";
            Debug.LogError($"[VanillaMount] {s_lastError}");
            Invalidate();
            return false;
        }
    }

    /// <summary>
    /// Lifts a vanilla element (a sub-asset of a *Collection in the mounted bundle) into an
    /// editable override, using the same Amplitude.Framework.Utility.DatatableElementCollectionUtility
    /// calls the Mod Editor's own "Override from Archives" importer uses: get-or-create the
    /// destination collection at vanilla's own FilePath, then duplicate the element by name
    /// (ensureUniqueName: false, so it overrides by name at load instead of becoming a copy).
    /// </summary>
    public static UnityEngine.Object OverrideVanillaElement(UnityEngine.Object refAsset)
    {
        if (!TryGetOwnerDescriptor(refAsset, out var ownerDescriptor))
        {
            Debug.LogError($"[VanillaMount] {refAsset.name}: not a vanilla element with a known owner (mount may have been invalidated).");
            return null;
        }
        if (refAsset is not Amplitude.Framework.IDatatableElement genuineElement)
        {
            Debug.LogError($"[VanillaMount] {refAsset.name} does not implement IDatatableElement; can't be overridden this way.");
            return null;
        }

        var collectionType = ownerDescriptor.GetAssetType();
        if (collectionType == null)
        {
            Debug.LogError($"[VanillaMount] {refAsset.name}: couldn't resolve the owning collection's type.");
            return null;
        }
        string directory = Path.GetDirectoryName(ownerDescriptor.FilePath)?.Replace('\\', '/');
        string collectionName = Path.GetFileNameWithoutExtension(ownerDescriptor.FileName);

        var collection = Amplitude.Framework.Utility.DatatableElementCollectionUtility
            .GetOrCreateDatatableElementCollection(collectionType, directory, collectionName, startNameEditing: false);
        if (collection == null)
        {
            Debug.LogError($"[VanillaMount] Failed to get-or-create override collection '{directory}/{collectionName}'.");
            return null;
        }

        Amplitude.Framework.IDatatableElement[] duplicates = null;
        bool ok = Amplitude.Framework.Utility.DatatableElementCollectionUtility.TryDuplicateDatatableElements(
            new[] { genuineElement }, ref collection, ref duplicates,
            showWarningDialogThresholdCount: false, ensureUniqueName: false, reimport: false);
        if (!ok || duplicates == null || duplicates.Length == 0)
        {
            Debug.LogError($"[VanillaMount] TryDuplicateDatatableElements failed for {refAsset.name}.");
            return null;
        }

        duplicates[0].SetEditable(true);
        return duplicates[0] as UnityEngine.Object;
    }

    [UnityEditor.MenuItem("Tools/shakee's Tools/Debug/Tech Tree/Force Re-mount Vanilla Database", false, 101)]
    static void DebugForceRemount()
    {
        if (ForceRemount(out var error)) Debug.Log("[VanillaMount] Force re-mount succeeded.");
        else Debug.LogError($"[VanillaMount] {error}");
    }

    [UnityEditor.MenuItem("Tools/shakee's Tools/Debug/Tech Tree/Vanilla Mount", false, 102)]
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
