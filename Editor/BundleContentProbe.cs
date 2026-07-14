using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Amplitude.Framework.Asset;
using Amplitude.Mercury.Animation;
using UnityEditor;
using UnityEngine;
using AssetDatabase = Amplitude.Framework.Asset.AssetDatabase;

/// <summary>
/// Diagnostic: mounts the MOD'S OWN built .assetbundle (same one deployed to the game's Community
/// folder) and reports what actually shipped for a name filter — answers "did my baked asset even
/// make it into the bundle, and intact?" without needing to launch the game.
/// </summary>
public class BundleContentProbe : EditorWindow
{
    const string ProviderName = "hkreimagined.bundleprobe";

    string _bundlePath = @"Assets\AssetBundles\StandaloneWindows64\HK Re-Imagined\hk re-imagined.assetbundle";
    string _nameFilter = "Erika";
    IAssetProvider _provider;

    [MenuItem("Tools/shakee's Tools/Debug/Inspect Built Mod Bundle", false, 8)]
    static void Open()
    {
        var w = GetWindow<BundleContentProbe>("Bundle Probe");
        w.minSize = new Vector2(480, 160);
    }

    void OnDisable()
    {
        if (_provider != null) { try { AssetDatabase.UnmountAssetBundle(ProviderName); } catch { } _provider = null; }
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Mounts the project's own built bundle (the same file deployed to the game's Community " +
            "folder) and dumps what it finds for the name filter — confirms whether a baked asset " +
            "actually shipped, intact, rather than guessing from in-game symptoms.",
            MessageType.Info);
        _bundlePath = EditorGUILayout.TextField("Bundle path (relative)", _bundlePath);
        _nameFilter = EditorGUILayout.TextField("Name filter", _nameFilter);
        if (GUILayout.Button("Mount && Dump")) Dump();
    }

    void Dump()
    {
        if (_provider != null) { try { AssetDatabase.UnmountAssetBundle(ProviderName); } catch { } _provider = null; }

        string fullPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), _bundlePath);
        if (!System.IO.File.Exists(fullPath)) { Debug.LogError($"[BundleProbe] not found: {fullPath}"); return; }

        bool ok = AssetDatabase.TryMountAssetBundle(ProviderName, fullPath, uint.MaxValue, out _provider, Amplitude.Framework.Asset.AssetBundle.Options.None);
        if (!ok || _provider == null) { Debug.LogError($"[BundleProbe] mount failed: {fullPath}"); return; }

        var descriptors = new List<AssetDescriptor>();
        _provider.AddAllAssetDescriptors(descriptors, AssetProviderOption.AskForType);

        var sb = new StringBuilder();
        sb.AppendLine($"=== BundleProbe: {_bundlePath} ===");
        sb.AppendLine($"Total descriptors: {descriptors.Count}");

        string filter = (_nameFilter ?? "").ToLowerInvariant();
        var matches = descriptors.Where(d => (d.FileName ?? "").ToLowerInvariant().Contains(filter)).ToList();
        sb.AppendLine($"Matching '{_nameFilter}': {matches.Count}");

        foreach (var d in matches)
        {
            var t = d.GetAssetType();
            sb.AppendLine($"\n[{t?.Name ?? d.TypeAsString}] {d.FileName}  path={d.FilePath}  guid={d.Guid}");

            UnityEngine.Object obj = null;
            try { obj = _provider.LoadAsset<UnityEngine.Object>(d); } catch (Exception e) { sb.AppendLine($"  LoadAsset threw: {e.Message}"); }

            if (obj is MeshCollection mc)
            {
                sb.AppendLine($"  SourcePrefab={mc.SourcePrefab}  SkeletonInstance={(mc.SkeletonInstance != null ? mc.SkeletonInstance.name : "null")}");
                var infos = mc.SkinnedMeshInfos;
                sb.AppendLine($"  SkinnedMeshInfos: {(infos?.Length ?? 0)}");
                if (infos != null)
                    foreach (var info in infos)
                    {
                        sb.Append($"    MeshName='{info.MeshName}'  FxMeshContent.Guid={info.FxMeshContent.Guid}");
                        // Reconstruct the actual Unity Mesh from the baked bytes so we can compare
                        // real geometry (bounds/vertex count) against the label — names can lie
                        // (e.g. a mislabeled GameObject in the source rig), bounds don't.
                        try
                        {
                            var mesh = mc.GetUnityMesh(info.MeshName);
                            if (mesh != null)
                                sb.Append($"  verts={mesh.vertexCount}  bounds.size={mesh.bounds.size}  bounds.center={mesh.bounds.center}");
                            else
                                sb.Append("  (GetUnityMesh returned null)");
                        }
                        catch (Exception e) { sb.Append($"  (GetUnityMesh threw: {e.Message})"); }
                        sb.AppendLine();
                    }
            }
            else if (obj != null)
            {
                DumpFieldsShallow(sb, obj);
            }
            else
            {
                sb.AppendLine("  (LoadAsset returned null — present as a descriptor but not loadable, or type mismatch)");
            }
        }

        // Also: does an AnimationManagerContent-typed asset ship in THIS bundle, and if so, does its
        // MeshCollections[] list include any of the matched GUIDs? (AnimationManagerContent isn't a
        // compile-time type in this project — reflect on whatever type name contains it.)
        var contentDescs = descriptors.Where(d => (d.GetAssetType()?.Name ?? d.TypeAsString ?? "").Contains("AnimationManagerContent")).ToList();
        sb.AppendLine($"\nAnimationManagerContent-typed assets in this bundle: {contentDescs.Count}");
        foreach (var cd in contentDescs)
        {
            UnityEngine.Object content = null;
            try { content = _provider.LoadAsset<UnityEngine.Object>(cd); } catch { }
            if (content == null) { sb.AppendLine($"  {cd.FileName}: failed to load"); continue; }
            sb.AppendLine($"  {cd.FileName}:");
            var f = content.GetType().GetField("MeshCollections", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var arr = f?.GetValue(content) as Array;
            sb.AppendLine($"    MeshCollections[]: {(arr?.Length ?? 0)} entries");
            if (arr != null)
                foreach (var g in arr)
                    if (matches.Any(m => string.Equals(g.ToString(), m.Guid.ToString(), StringComparison.OrdinalIgnoreCase)))
                        sb.AppendLine($"      contains a matched GUID: {g}");
        }

        Debug.Log(sb.ToString());
    }

    static void DumpFieldsShallow(StringBuilder sb, UnityEngine.Object obj)
    {
        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var f in obj.GetType().GetFields(BF))
        {
            object v; try { v = f.GetValue(obj); } catch { v = "<err>"; }
            string disp = v is Array a ? $"[{a.Length}]" : (v as UnityEngine.Object)?.name ?? v?.ToString() ?? "null";
            sb.AppendLine($"  {f.Name} ({f.FieldType.Name}) = {disp}");
        }
    }
}
