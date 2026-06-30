using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amplitude.Framework.Asset;
using UnityEditor;
using UnityEngine;
using AssetDatabase = Amplitude.Framework.Asset.AssetDatabase;

/// <summary>
/// Resolves an arbitrary Amplitude GUID against the vanilla .assetbundle set under
/// &lt;Humankind&gt;/AssetBundles/, generalizing GuidLookup's proven mount/scan loop so the Unit
/// Visual Workflow wizard can load a target unit's rig PREFAB in-editor (for bone-structure
/// auto-detection) without requiring the runtime-only AnimationManager registry, which doesn't
/// exist in the editor (AnimationManagerContent.Instance is null there — see
/// Docs/ENCAccessProof-master/docs/Custom3DModels-Findings-Shareable.md).
///
/// IMPORTANT distinction: this is for true bundle ASSETS (Skeleton, MeshCollection, rig prefabs,
/// textures) — content that is NOT a database entry and so isn't reachable through
/// VanillaDatabaseMount/Database Browser at all. Database entries (PresentationPawnDefinition,
/// PresentationPawnDescription, UnitDefinition, etc. — anything import-able via "Override from
/// Archives") live in the MercuryDatabases bundle and should go through
/// VanillaDatabaseMount.LoadAllOfType instead, which already handles sub-asset rows inside
/// *Collection containers; this class's raw per-bundle descriptor scan does not.
///
/// Provider naming matches AssetExplorer's convention (plain lowercase filename, no prefix) so a
/// bundle the Mod Editor already auto-mounted is reused rather than double-mounted (which
/// Amplitude's native bundle layer rejects) — see GuidLookup.cs for the same fix.
/// </summary>
internal static class VanillaAssetResolver
{
    internal static GameObject LoadGameObjectByGuid(Amplitude.Framework.Guid guid)
    {
        var obj = LoadAssetByGuid(guid, out var type);
        if (obj is GameObject go) return go;
        if (obj != null) Debug.LogWarning($"[VanillaAssetResolver] GUID {guid} resolved to a {type?.Name ?? obj.GetType().Name}, not a GameObject.");
        return null;
    }

    internal static UnityEngine.Object LoadAssetByGuid(Amplitude.Framework.Guid guid, out Type resolvedType)
    {
        resolvedType = null;
        string mercuryFolder = Amplitude.Mercury.Production.Modification.ModuleEditor.MercuryFolderPath;
        if (string.IsNullOrEmpty(mercuryFolder)) { Debug.LogError("[VanillaAssetResolver] Humankind folder is not configured (set it in Mercury/Mod Editor)."); return null; }

        string root = Path.Combine(mercuryFolder, "AssetBundles");
        if (!Directory.Exists(root)) { Debug.LogError($"[VanillaAssetResolver] AssetBundles folder not found: {root}"); return null; }

        var bundleFiles = Directory.GetDirectories(root)
            .SelectMany(dir => Directory.GetFiles(dir, "*.assetbundle"))
            .OrderBy(f => f)
            .ToList();

        foreach (var path in bundleFiles)
        {
            // Same provider-name convention as AssetExplorer (plain lowercase filename) — the Mod
            // Editor auto-mounts the vanilla bundles under this name on its own, so checking/reusing
            // it (instead of a custom alias) avoids double-mounting the same physical file.
            string providerName = Path.GetFileName(path).ToLowerInvariant();
            IAssetProvider provider = null;
            bool weMounted = false;
            try
            {
                if (AssetDatabase.IsMounted(providerName))
                {
                    provider = AssetDatabase.AllProviders.FirstOrDefault(p => p.Name == providerName);
                }
                else
                {
                    weMounted = AssetDatabase.TryMountAssetBundle(providerName, path, uint.MaxValue, out provider, Amplitude.Framework.Asset.AssetBundle.Options.None);
                    if (!weMounted) continue;
                }
                if (provider == null) continue;

                var descriptors = new List<AssetDescriptor>();
                provider.AddAllAssetDescriptors(descriptors, AssetProviderOption.AskForType);
                var hit = descriptors.FirstOrDefault(d => d.Guid == guid);
                if (hit == null) continue;

                resolvedType = hit.GetAssetType();
                return provider.LoadAsset<UnityEngine.Object>(hit);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VanillaAssetResolver] error scanning {Path.GetFileName(path)}: {e.Message}");
            }
            finally
            {
                if (weMounted && resolvedType == null)
                {
                    try { AssetDatabase.UnmountAssetBundle(providerName); } catch { }
                }
            }
        }

        Debug.LogWarning($"[VanillaAssetResolver] GUID {guid} not found in any vanilla bundle.");
        return null;
    }
}
