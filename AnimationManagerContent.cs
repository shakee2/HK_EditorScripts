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
/// collection assets in your mod folder, and validates the GUID joins the runtime relies on.
///
/// The game does NOT auto-discover collections. AnimationManager loads exactly the GUIDs
/// listed in one AnimationManagerContent's three arrays; anything a pawn references but
/// isn't listed logs "not registered ... please add it to AnimationManagerContent".
///
/// This tool: scan mod folder -> collect Amplitude GUIDs of MeshCollection/Skeleton/
/// ClipCollection/OverrideController -> write into the content's arrays -> validate joins
/// (MeshCollection.prefab GUID must equal some fragment's Prefab/ModelPrefab GUID;
/// ClipCollection.skeleton GUID must match a registered Skeleton).
/// </summary>
public class AnimationContentPopulator : EditorWindow
{
    const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    UnityEngine.Object _content;                 // the AnimationManagerContent asset
    string _scanFolder = "Assets/Resources/New Additions";

    [MenuItem("Tools/Pawn Fragment/Animation Content Populator", false, 6)]
    static void Open()
    {
        var w = GetWindow<AnimationContentPopulator>("Anim Content");
        w.minSize = new Vector2(440, 240);
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("AnimationManagerContent registry", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Fills the content's MeshCollections / AnimationClipCollections / AnimatorOverrideControllers " +
            "arrays from baked assets under the scan folder, then validates the GUID joins.",
            MessageType.Info);

        _content = EditorGUILayout.ObjectField("Content asset", _content, typeof(UnityEngine.Object), false);
        _scanFolder = EditorGUILayout.TextField("Scan folder", _scanFolder);

        EditorGUILayout.Space(6);
        if (GUILayout.Button("Create AnimationManagerContent")) CreateContent();
        using (new EditorGUI.DisabledScope(_content == null))
        {
            if (GUILayout.Button("Scan & Populate")) Populate();
            if (GUILayout.Button("Validate Joins")) ValidateJoins();
            if (GUILayout.Button("Dump Content")) DumpContent();
        }
    }

    // ── Create an empty content asset ─────────────────────────────────────────
    void CreateContent()
    {
        var t = FindType("Amplitude.Mercury.Presentation.AnimationManagerContent")
             ?? FindTypeByName("AnimationManagerContent");
        if (t == null) { Debug.LogError("[AnimContent] AnimationManagerContent type not found."); return; }
        EnsureFolder(_scanFolder);
        var so = ScriptableObject.CreateInstance(t);
        so.name = "New Additions_AnimationManagerContent";
        string path = AssetDatabase.GenerateUniqueAssetPath($"{_scanFolder}/{so.name}.asset");
        AssetDatabase.CreateAsset(so, path);
        AssetDatabase.SaveAssets();
        _content = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
        Debug.Log($"[AnimContent] created at {path}. Now 'Scan & Populate'.\n" +
                  "NOTE: the game's AnimationManager points at ONE content asset — confirm how your mod's " +
                  "content gets loaded/merged (replace vs per-mod), or the runtime won't read this.");
    }

    // ── Scan the folder and write GUID arrays ─────────────────────────────────
    void Populate()
    {
        var meshGuids = CollectGuids("MeshCollection");      // includes Skeleton (subclass of MeshCollection)
        var clipGuids = CollectGuids("ClipCollection");
        var ovrGuids  = CollectGuids("OverrideController");

        Undo.RecordObject(_content, "Populate animation content");
        bool a = WriteGuidArray(_content, new[] { "MeshCollections" }, meshGuids);
        bool b = WriteGuidArray(_content, new[] { "AnimationClipCollections", "ClipCollections" }, clipGuids);
        bool c = WriteGuidArray(_content, new[] { "AnimatorOverrideControllers", "OverrideControllers" }, ovrGuids);

        EditorUtility.SetDirty(_content);
        AssetDatabase.SaveAssets();
        Debug.Log($"[AnimContent] populated: {meshGuids.Count} mesh/skeleton, {clipGuids.Count} clip, {ovrGuids.Count} override." +
                  (a && b && c ? "" : "  (one or more array fields not found — run Dump Content / tell me the field names)"));
        ValidateJoins();
    }

    // collect Amplitude GUIDs of all assets of a type-name under the scan folder
    List<object> CollectGuids(string typeShortName)
    {
        var result = new List<object>();
        foreach (var guid in AssetDatabase.FindAssets($"t:{typeShortName}", new[] { _scanFolder }))
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
    void ValidateJoins()
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
        foreach (var g in AssetDatabase.FindAssets("t:MeshCollection", new[] { _scanFolder }))
        {
            var mc = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(g));
            var prefabRef = mc.GetType().GetField("prefab", ALL)?.GetValue(mc);
            var prefabGuid = prefabRef == null ? null : (prefabRef.GetType().FullName == "Amplitude.Framework.Guid" ? prefabRef : FindGuidField(prefabRef.GetType())?.GetValue(prefabRef));
            bool hit = prefabGuid != null && fragPrefabGuids.Any(fg => GuidEquals(fg, prefabGuid));
            sb.AppendLine($"  MeshCollection '{mc.name}': prefab GUID {(hit ? "matches a fragment ✓" : "NO matching fragment ✗")}");
            if (hit) matched++; else unmatched++;
        }
        sb.AppendLine($"\n{matched} mesh collections joined to a fragment, {unmatched} orphaned.");
        if (unmatched > 0)
            sb.AppendLine("Orphaned = the collection's prefab GUID doesn't equal any fragment's Prefab GUID. " +
                          "The runtime looks up GetMeshCollection(fragment.Prefab.Guid), so these won't resolve.");
        Debug.Log(sb.ToString());
    }

    void DumpContent()
    {
        var sb = new StringBuilder($"=== {_content.name} ({_content.GetType().FullName}) ===\n");
        foreach (var f in _content.GetType().GetFields(ALL))
        {
            var v = f.GetValue(_content);
            int n = v is Array arr ? arr.Length : (v is IList list ? list.Count : -1);
            sb.AppendLine($"  {f.Name} : {f.FieldType.Name}" + (n >= 0 ? $"  [{n}]" : ""));
        }
        Debug.Log(sb.ToString());
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

    object AmpGuidOf(UnityEngine.Object asset)
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