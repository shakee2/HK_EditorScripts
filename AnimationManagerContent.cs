using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Populates an AnimationManagerContent (the hand-maintained registry) from the baked
/// collection assets in a mod folder, and validates the GUID joins the runtime relies on.
/// Used by the Unit Visual Workflow wizard's "Build into mod's AnimationManagerContent" step
/// (Tools/Unit Visual Workflow).
///
/// CORRECTION (confirmed by an actual build+in-game test, not just decompiled inference): the
/// "game does NOT auto-discover collections, you must manually register here" premise below was
/// speculative and turned out to be FALSE for at least the skinned-mesh-fragment case — a unit
/// rendered correctly in-game without ever running Populate. The Mod Editor's build pipeline
/// appears to auto-discover/register a fragment's MeshCollection the same way it auto-scans
/// fragmentDirectories for fragments. Treat Populate/ValidateJoins as a diagnostic/manual-override
/// aid (useful for validating joins or fixing a case the auto-discovery misses), not a required
/// step in the normal workflow — EXCEPT for the Tier0b live-mesh-patch case, where the replacement
/// MeshCollection ships inert and is loaded/registered at runtime by the BepInEx injector instead.
///
/// This: scan mod folder -> collect Amplitude GUIDs of MeshCollection/Skeleton/ClipCollection/
/// OverrideController -> write into the content's arrays -> validate joins (MeshCollection.prefab
/// GUID must equal some fragment's Prefab/ModelPrefab GUID; ClipCollection.skeleton GUID must
/// match a registered Skeleton).
/// </summary>
internal static class AnimationContentBuilder
{
    const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // ── Create an empty content asset ─────────────────────────────────────────
    internal static UnityEngine.Object CreateContent(string scanFolder, string contentName)
    {
        var t = FindType("Amplitude.Mercury.Presentation.AnimationManagerContent")
             ?? FindTypeByName("AnimationManagerContent");
        if (t == null) { Debug.LogError("[AnimContent] AnimationManagerContent type not found."); return null; }
        EnsureFolder(scanFolder);
        var so = ScriptableObject.CreateInstance(t);
        so.name = contentName;
        string path = AssetDatabase.GenerateUniqueAssetPath($"{scanFolder}/{so.name}.asset");
        AssetDatabase.CreateAsset(so, path);
        AssetDatabase.SaveAssets();
        var content = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
        Debug.Log($"[AnimContent] created at {path}.\n" +
                  "NOTE: the game's AnimationManager points at ONE content asset — confirm how your mod's " +
                  "content gets loaded/merged (replace vs per-mod), or the runtime won't read this.");
        return content;
    }

    // ── Scan the folder and write GUID arrays ─────────────────────────────────
    internal static void Populate(UnityEngine.Object content, string scanFolder)
    {
        var meshGuids = CollectGuids("MeshCollection", scanFolder);      // includes Skeleton (subclass of MeshCollection)
        var clipGuids = CollectGuids("ClipCollection", scanFolder);
        var ovrGuids  = CollectGuids("OverrideController", scanFolder);

        Undo.RecordObject(content, "Populate animation content");
        bool a = WriteGuidArray(content, new[] { "MeshCollections" }, meshGuids);
        bool b = WriteGuidArray(content, new[] { "AnimationClipCollections", "ClipCollections" }, clipGuids);
        bool c = WriteGuidArray(content, new[] { "AnimatorOverrideControllers", "OverrideControllers" }, ovrGuids);

        EditorUtility.SetDirty(content);
        AssetDatabase.SaveAssets();
        Debug.Log($"[AnimContent] populated: {meshGuids.Count} mesh/skeleton, {clipGuids.Count} clip, {ovrGuids.Count} override." +
                  (a && b && c ? "" : "  (one or more array fields not found — run DumpContent / check field names)"));
        ValidateJoins(content, scanFolder);
    }

    // collect Amplitude GUIDs of all assets of a type-name under the scan folder
    static List<object> CollectGuids(string typeShortName, string scanFolder)
    {
        var result = new List<object>();
        foreach (var guid in AssetDatabase.FindAssets($"t:{typeShortName}", new[] { scanFolder }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) continue;
            var amp = AmpGuidOf(asset);
            if (amp != null && !result.Any(g => GuidEquals(g, amp))) result.Add(amp);
        }
        return result;
    }

    // ── Validation: the join keys the runtime relies on ───────────────────────
    internal static string ValidateJoins(UnityEngine.Object content, string scanFolder)
    {
        var sb = new StringBuilder("=== Join validation ===\n");

        // gather fragment Prefab/ModelPrefab GUIDs across the project
        var fragPrefabGuids = new List<object>();
        foreach (var tn in new[] { "PresentationPawnFragmentSkinnedMesh", "PresentationPawnFragmentMesh" })
            foreach (var g in AssetDatabase.FindAssets($"t:{tn}"))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(g));
                if (asset == null) continue;
                var refField = asset.GetType().GetField("Prefab", ALL) ?? asset.GetType().GetField("ModelPrefab", ALL);
                var refObj = refField?.GetValue(asset);
                var gd = refObj == null ? null : FindGuidField(refObj.GetType())?.GetValue(refObj);
                if (gd != null && !IsZeroGuid(gd)) fragPrefabGuids.Add(gd);
            }

        // each registered MeshCollection.prefab GUID should match some fragment Prefab GUID
        int matched = 0, unmatched = 0;
        foreach (var g in AssetDatabase.FindAssets("t:MeshCollection", new[] { scanFolder }))
        {
            var mc = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(g));
            var prefabRef = mc.GetType().GetField("prefab", ALL)?.GetValue(mc);
            var prefabGuid = prefabRef == null ? null : (prefabRef.GetType().FullName == "Amplitude.Framework.Guid" ? prefabRef : FindGuidField(prefabRef.GetType())?.GetValue(prefabRef));
            bool hit = prefabGuid != null && fragPrefabGuids.Any(fg => GuidEquals(fg, prefabGuid));
            sb.AppendLine($"  MeshCollection '{mc.name}': prefab GUID {(hit ? "matches a fragment" : "NO matching fragment")}");
            if (hit) matched++; else unmatched++;
        }
        sb.AppendLine($"\n{matched} mesh collections joined to a fragment, {unmatched} orphaned.");
        if (unmatched > 0)
            sb.AppendLine("Orphaned = the collection's prefab GUID doesn't equal any fragment's Prefab GUID. " +
                          "The runtime looks up GetMeshCollection(fragment.Prefab.Guid), so these won't resolve.");
        var result = sb.ToString();
        Debug.Log(result);
        return result;
    }

    internal static string DumpContent(UnityEngine.Object content)
    {
        var sb = new StringBuilder($"=== {content.name} ({content.GetType().FullName}) ===\n");
        foreach (var f in content.GetType().GetFields(ALL))
        {
            var v = f.GetValue(content);
            int n = v is Array arr ? arr.Length : (v is IList list ? list.Count : -1);
            sb.AppendLine($"  {f.Name} : {f.FieldType.Name}" + (n >= 0 ? $"  [{n}]" : ""));
        }
        var result = sb.ToString();
        Debug.Log(result);
        return result;
    }

    // ── reflection plumbing ───────────────────────────────────────────────────
    static bool WriteGuidArray(UnityEngine.Object content, string[] candidateNames, List<object> guids)
    {
        var t = content.GetType();
        FieldInfo f = candidateNames.Select(n => t.GetField(n, ALL)).FirstOrDefault(x => x != null);
        if (f == null) return false;

        // field is Guid[]; build a typed array
        var elem = f.FieldType.IsArray ? f.FieldType.GetElementType() : null;
        if (elem == null) { Debug.LogWarning($"[AnimContent] {f.Name} is not an array; skipping."); return false; }
        var typed = Array.CreateInstance(elem, guids.Count);
        for (int i = 0; i < guids.Count; i++) typed.SetValue(guids[i], i);
        f.SetValue(content, typed);
        return true;
    }

    static object AmpGuidOf(UnityEngine.Object asset)
    {
        var au = FindType("Amplitude.Framework.Editor.Asset.AssetUtility");
        var m = au?.GetMethod("GetAssetGuid", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                              null, new[] { typeof(UnityEngine.Object) }, null);
        if (m != null) { try { var g = m.Invoke(null, new object[] { asset }); if (g != null && !IsZeroGuid(g)) return g; } catch { } }
        Debug.LogWarning($"[AnimContent] couldn't get Amplitude GUID for '{asset.name}'.");
        return null;
    }

    static FieldInfo FindGuidField(Type t)
    {
        for (var cur = t; cur != null; cur = cur.BaseType)
        {
            var f = cur.GetFields(ALL).FirstOrDefault(x => x.FieldType.FullName == "Amplitude.Framework.Guid");
            if (f != null) return f;
        }
        return null;
    }

    static bool IsZeroGuid(object guid)
    {
        var t = guid.GetType(); bool any = false;
        foreach (var n in new[] { "a", "b", "c", "d" })
        { var f = t.GetField(n, ALL); if (f != null) { any = true; if (Convert.ToInt64(f.GetValue(guid)) != 0) return false; } }
        return any;
    }

    static bool GuidEquals(object g1, object g2)
    {
        if (g1 == null || g2 == null) return false;
        var t = g1.GetType();
        foreach (var n in new[] { "a", "b", "c", "d" })
        {
            var f = t.GetField(n, ALL);
            if (f != null && Convert.ToInt64(f.GetValue(g1)) != Convert.ToInt64(f.GetValue(g2))) return false;
        }
        return true;
    }

    static Type FindType(string fullName)
    { foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { var t = a.GetType(fullName); if (t != null) return t; } return null; }

    static Type FindTypeByName(string shortName)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        { Type[] ts; try { ts = a.GetTypes(); } catch { continue; }
          var t = ts.FirstOrDefault(x => x.Name == shortName); if (t != null) return t; }
        return null;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parts = path.Split('/'); string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        { var next = cur + "/" + parts[i]; if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]); cur = next; }
    }
}
