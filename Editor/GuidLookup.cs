using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Amplitude.Framework.Asset;
using Amplitude.Mercury.Animation;
using UnityEditor;
using UnityEngine;
using AssetDatabase = Amplitude.Framework.Asset.AssetDatabase;

/// <summary>
/// Resolves a single Amplitude GUID against every vanilla .assetbundle under
/// &lt;Humankind&gt;/AssetBundles/ — answers "what asset is this GUID?" without grepping
/// the binary bundles (the GUID is a serialized struct, not ASCII text, so a raw text
/// search over the bundle files never finds it).
///
/// Checks each descriptor's own Guid, and — for MeshCollection assets specifically —
/// also SourcePrefab, since that's the join key AnimationManager.GetMeshCollection
/// actually looks up by (see AssetExplorer's "Copy SourcePrefab GUID").
/// </summary>
public class GuidLookup : EditorWindow
{
    string _target = "d242eb387b17c544699ee58f6d15df44";
    string _result = "";
    Vector2 _scroll;

    [MenuItem("Tools/shakee's Tools/Debug/Find GUID In Vanilla Bundles", false, 9)]
    static void Open()
    {
        var w = GetWindow<GuidLookup>("GUID Lookup");
        w.minSize = new Vector2(520, 300);
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Mounts every vanilla .assetbundle under <Humankind>/AssetBundles/ one at a time and " +
            "checks each asset descriptor's GUID (and, for MeshCollections, SourcePrefab) against " +
            "the target. Slower than a text search but works against binary-serialized GUIDs.",
            MessageType.Info);

        _target = EditorGUILayout.TextField("Target GUID", _target);
        if (GUILayout.Button("Search all vanilla bundles")) Search();

        EditorGUILayout.Space(6);
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        EditorGUILayout.TextArea(_result, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    void Search()
    {
        var sb = new StringBuilder();
        string needle = (_target ?? "").Trim();
        if (needle.Length == 0) { _result = "Enter a GUID first."; return; }

        string mercuryFolder = Amplitude.Mercury.Production.Modification.ModuleEditor.MercuryFolderPath;
        if (string.IsNullOrEmpty(mercuryFolder))
        {
            _result = "Humankind folder is not configured (set it in Mercury/Mod Editor).";
            return;
        }

        string root = Path.Combine(mercuryFolder, "AssetBundles");
        if (!Directory.Exists(root))
        {
            _result = $"AssetBundles folder not found: {root}";
            return;
        }

        var bundleFiles = Directory.GetDirectories(root)
            .SelectMany(dir => Directory.GetFiles(dir, "*.assetbundle"))
            .OrderBy(f => f)
            .ToList();

        sb.AppendLine($"Searching {bundleFiles.Count} vanilla bundles for GUID {needle} ...\n");
        bool found = false;

        foreach (var path in bundleFiles)
        {
            // Same provider-name convention as AssetExplorer (plain lowercase filename) — the Mod
            // Editor auto-mounts the vanilla bundles under this name on its own, so checking/reusing
            // it (instead of a custom alias) avoids double-mounting the same physical file, which
            // Amplitude's native bundle layer rejects.
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
                    bool ok = AssetDatabase.TryMountAssetBundle(providerName, path, uint.MaxValue, out provider, Amplitude.Framework.Asset.AssetBundle.Options.None);
                    weMounted = ok;
                    if (!ok) { sb.AppendLine($"[skip] failed to mount: {path}"); continue; }
                }
                if (provider == null) { sb.AppendLine($"[skip] no provider for: {path}"); continue; }

                var descriptors = new List<AssetDescriptor>();
                provider.AddAllAssetDescriptors(descriptors, AssetProviderOption.AskForType);

                foreach (var d in descriptors)
                {
                    bool descMatch = string.Equals(d.Guid.ToString(), needle, StringComparison.OrdinalIgnoreCase);
                    bool sourcePrefabMatch = false;
                    UnityEngine.Object obj = null;
                    var t = d.GetAssetType();

                    if (t == typeof(MeshCollection) || t != null && t.IsSubclassOf(typeof(MeshCollection)))
                    {
                        try
                        {
                            obj = provider.LoadAsset<UnityEngine.Object>(d);
                            if (obj is MeshCollection mc)
                                sourcePrefabMatch = string.Equals(mc.SourcePrefab.ToString(), needle, StringComparison.OrdinalIgnoreCase);
                        }
                        catch { }
                    }

                    if (descMatch || sourcePrefabMatch)
                    {
                        found = true;
                        sb.AppendLine($"=== MATCH in {Path.GetFileName(path)} (folder: {Path.GetFileName(Path.GetDirectoryName(path))}) ===");
                        sb.AppendLine($"  Name      = {d.FileName}");
                        sb.AppendLine($"  Type      = {t?.Name ?? d.TypeAsString}");
                        sb.AppendLine($"  Path      = {d.FilePath}");
                        sb.AppendLine($"  Guid      = {d.Guid}{(descMatch ? "  <-- matched here" : "")}");
                        if (obj is MeshCollection mc2)
                        {
                            sb.AppendLine($"  SourcePrefab = {mc2.SourcePrefab}{(sourcePrefabMatch ? "  <-- matched here" : "")}");
                            sb.AppendLine($"  SkeletonInstance = {(mc2.SkeletonInstance != null ? mc2.SkeletonInstance.name : "null")}");
                        }
                        sb.AppendLine();
                    }
                }
            }
            catch (Exception e)
            {
                sb.AppendLine($"[error] {path}: {e.Message}");
            }
            finally
            {
                if (weMounted)
                {
                    try { AssetDatabase.UnmountAssetBundle(providerName); } catch { }
                }
            }
        }

        if (!found) sb.AppendLine("No match found in any vanilla bundle.");
        _result = sb.ToString();
        Debug.Log(_result);
    }
}
