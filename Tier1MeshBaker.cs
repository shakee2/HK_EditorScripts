using System.Collections.Generic;
using System.Linq;
using Amplitude.Mercury.Animation;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Mesh-bake helpers used by the Unit Visual Workflow wizard (Tools/Unit Visual Workflow) —
/// bakes a prefab's renderers into a MeshCollection asset.
///
/// Confirmed against the decompiled Amplitude.Mercury.Animation.dll shipped with this project:
/// MeshCollection.Reimport() (public, virtual) already does the whole bake — instantiate the
/// prefab assigned via SetPrefab, walk its SkinnedMeshRenderer[]/MeshRenderer[], and pack each
/// shared mesh into a SkinnedMeshInfo.FxMeshContent. No separate FxMesh asset or "Assets ->
/// Create -> Amplitude/Fx/Mesh" step is needed for this path — that menu authors a *standalone*
/// FxMesh, which Reimport() does not consume.
///
/// IMPORTANT: any Skeleton resolved from the mounted VANILLA bundle (not a project asset) has no
/// Unity GUID/fileID, so a UnityEngine.Object reference to it does not survive a domain reload
/// (Unity's window-state serialization can't re-resolve it -> "fake null"). Every entry point here
/// therefore takes the skeleton as a GUID string and resolves it fresh, synchronously, inside the
/// method — never as a stored object reference passed in from a field held across frames.
/// </summary>
internal static class MeshViability
{
    // GPU mesh-content encoding limits, confirmed in FxComponentMeshContentManager.ContentLayer.
    static readonly Vector3 PosMin = new Vector3(-8f, -16f, -8f);
    static readonly Vector3 PosMax = new Vector3(8f, 16f, 8f);
    static readonly Vector2 UvMin = new Vector2(-2f, 0f);
    static readonly Vector2 UvMax = new Vector2(6f, 1f);

    internal enum Severity { Info, Warning, Error }
    internal class Finding { public Severity severity; public string message; }

    internal static List<Finding> Check(GameObject prefab, MeshCollection existing)
    {
        var findings = new List<Finding>();

        var skinned = prefab.GetComponentsInChildren<SkinnedMeshRenderer>();
        var statics = prefab.GetComponentsInChildren<MeshRenderer>()
            .Where(mr => mr.GetComponent<MeshFilter>() != null).ToArray();

        if (skinned.Length == 0 && statics.Length == 0)
        {
            findings.Add(new Finding { severity = Severity.Error, message = "No SkinnedMeshRenderer or MeshRenderer found in the prefab." });
            return findings;
        }

        foreach (var smr in skinned) CheckMesh(findings, smr.sharedMesh, smr.name);
        foreach (var mr in statics) CheckMesh(findings, mr.GetComponent<MeshFilter>().sharedMesh, mr.name);

        if (skinned.Length > 0 && existing != null && existing.SkeletonInstance == null)
            findings.Add(new Finding { severity = Severity.Warning, message = "Prefab has skinned meshes, but the target MeshCollection has no Skeleton assigned — Reimport will bake it as a static mesh (all weight on bone 0)." });

        if (skinned.Length > 0 && (existing == null || existing.SkeletonInstance == null))
            findings.Add(new Finding { severity = Severity.Info, message = "No Skeleton on the target — this stays a Tier 1 static-style bake. Use a Tier 2 tool to bake a Skeleton if this unit needs its own rig/animations." });

        if (findings.Count == 0)
            findings.Add(new Finding { severity = Severity.Info, message = "All meshes readable and within the GPU encoding bounds." });

        return findings;
    }

    static void CheckMesh(List<Finding> findings, Mesh mesh, string rendererName)
    {
        if (mesh == null)
        {
            findings.Add(new Finding { severity = Severity.Error, message = $"'{rendererName}': no shared mesh assigned." });
            return;
        }
        if (!mesh.isReadable)
        {
            findings.Add(new Finding { severity = Severity.Error, message = $"'{mesh.name}' is not readable — enable Read/Write in its model import settings." });
            return;
        }

        var verts = mesh.vertices;
        int outOfPos = verts.Count(v => v.x < PosMin.x || v.x > PosMax.x || v.y < PosMin.y || v.y > PosMax.y || v.z < PosMin.z || v.z > PosMax.z);
        if (outOfPos > 0)
            findings.Add(new Finding { severity = Severity.Warning, message = $"'{mesh.name}': {outOfPos}/{verts.Length} vertices outside the position EncodingBBox {PosMin}-{PosMax}. Will render clamped/garbage." });

        var uvs = mesh.uv;
        int outOfUv = uvs.Count(u => u.x < UvMin.x || u.x > UvMax.x || u.y < UvMin.y || u.y > UvMax.y);
        if (outOfUv > 0)
            findings.Add(new Finding { severity = Severity.Warning, message = $"'{mesh.name}': {outOfUv}/{uvs.Length} UVs outside the UV EncodingBBox {UvMin}-{UvMax}. Will render clamped/garbage." });
    }
}

internal static class MeshCollectionBaker
{
    // ── Bake ───────────────────────────────────────────────────────────────────
    // skeletonGuidHex (optional): resolved fresh, right here, never stored as a field elsewhere.
    internal static MeshCollection Bake(GameObject prefab, MeshCollection existing, string createFolder,
        bool createAsSkeleton, string skeletonGuidHex)
    {
        Skeleton skeletonOverride = ResolveSkeletonByGuid(skeletonGuidHex);

        var target = existing;
        bool created = false;
        if (target == null)
        {
            EnsureFolder(createFolder);
            target = createAsSkeleton
                ? ScriptableObject.CreateInstance<Skeleton>()
                : ScriptableObject.CreateInstance<MeshCollection>();
            target.name = prefab.name + (createAsSkeleton ? "_Skeleton" : "_MeshCollection");
            string path = AssetDatabase.GenerateUniqueAssetPath($"{createFolder}/{target.name}.asset");
            AssetDatabase.CreateAsset(target, path);
            created = true;
        }

        Undo.RecordObject(target, "Bake MeshCollection");
        target.SetPrefab(prefab);
        if (skeletonOverride != null) SetSkeletonField(target, skeletonOverride);
        target.Reimport();
        EditorUtility.SetDirty(target);
        AssetDatabase.SaveAssets();

        Debug.Log($"[MeshBaker] {(created ? "created" : "updated")} '{target.name}' from prefab '{prefab.name}'. " +
                  $"SourcePrefab GUID: {target.SourcePrefab}  Meshes baked: {target.SkinnedMeshInfos?.Length ?? 0}");

        EditorGUIUtility.PingObject(target);
        return target;
    }

    internal static void ForcePrefabGuid(MeshCollection existing, string hex)
    {
        var guid = new Amplitude.Framework.Guid((hex ?? "").Trim());
        if (guid.IsNull) { Debug.LogError($"[MeshBaker] '{hex}' did not parse to a valid GUID."); return; }

        Undo.RecordObject(existing, "Force MeshCollection SourcePrefab GUID");
        existing.SetPrefab(guid);   // Guid overload: just stamps `prefab`, no Reimport/reload.
        EditorUtility.SetDirty(existing);
        AssetDatabase.SaveAssets();
        Debug.Log($"[MeshBaker] '{existing.name}'.SourcePrefab forced to {guid} (mesh data untouched).");
    }

    // Builds a new prefab: a single bone named after the override Skeleton's root BoneInfo (or
    // selfAuthoredBoneName if no skeletonGuidHex given), with a SkinnedMeshRenderer 100%-weighted
    // to it, combining every MeshFilter/SkinnedMeshRenderer mesh found in the source prefab. This
    // is the missing piece Reimport() needs to bone-weight by name against a real (non-null)
    // Skeleton — without it, a plain static mesh bakes zero skinnedMeshInfos entries once a
    // Skeleton override is set (see the proven ENC Zeppelin recipe, which builds exactly this kind
    // of single-bone rig before baking).
    internal static GameObject BuildSingleBoneSkinnedVariant(GameObject prefab, string skeletonGuidHex,
        string selfAuthoredBoneName, string meshNameOverride, string createFolder)
    {
        if (prefab == null) return null;
        Skeleton skeletonOverride = ResolveSkeletonByGuid(skeletonGuidHex);

        string rootBoneName;
        int rootIdx = -1;
        if (skeletonOverride != null)
        {
            var boneInfos = skeletonOverride.BoneInfos;
            if (boneInfos == null || boneInfos.Length == 0)
            {
                Debug.LogError("[MeshBaker] Skeleton override has no BoneInfos — can't pick a root bone name.");
                return null;
            }
            rootIdx = System.Array.FindIndex(boneInfos, b => b.ParentIndex < 0);
            if (rootIdx < 0) rootIdx = 0;
            rootBoneName = boneInfos[rootIdx].Name;
        }
        else
        {
            // No external Skeleton to match — self-authoring a brand-new one (bake this result with
            // "Create as Skeleton" ON), so any bone name works as long as it's used consistently below.
            rootBoneName = string.IsNullOrWhiteSpace(selfAuthoredBoneName) ? "Base" : selfAuthoredBoneName.Trim();
        }

        // Gather every mesh in the source prefab (static or already-skinned) to combine into one.
        var combine = new List<CombineInstance>();
        Material sourceMaterial = null;
        foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>())
        {
            var mr = mf.GetComponent<MeshRenderer>();
            if (mf.sharedMesh == null) continue;
            combine.Add(new CombineInstance { mesh = mf.sharedMesh, transform = mf.transform.localToWorldMatrix });
            if (sourceMaterial == null && mr != null) sourceMaterial = mr.sharedMaterial;
        }
        foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (smr.sharedMesh == null) continue;
            combine.Add(new CombineInstance { mesh = smr.sharedMesh, transform = smr.transform.localToWorldMatrix });
            if (sourceMaterial == null) sourceMaterial = smr.sharedMaterial;
        }
        if (combine.Count == 0)
        {
            Debug.LogError("[MeshBaker] no MeshFilter/SkinnedMeshRenderer with a mesh found in the source prefab.");
            return null;
        }

        // The mesh entry's name (= SkinnedMeshInfo.MeshName after Reimport) must equal the target Fragment's
        // SkinnedMeshPath exactly — that's the string-match GetFxMeshIndex(meshName) looks up at runtime.
        // Convention on vanilla units is this matches the unit's own name, NOT the source asset's name, so
        // default to the source prefab's name only as a fallback, not an assumption.
        string meshEntryName = string.IsNullOrWhiteSpace(meshNameOverride) ? prefab.name : meshNameOverride.Trim();

        var mesh = new Mesh { name = meshEntryName + "_SkinnedMesh" };
        mesh.CombineMeshes(combine.ToArray(), true, true);
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, mesh.vertexCount).ToArray();
        mesh.bindposes = new[] { Matrix4x4.identity };
        mesh.RecalculateBounds();
        if (mesh.normals == null || mesh.normals.Length == 0) mesh.RecalculateNormals();
        mesh.RecalculateTangents();

        EnsureFolder(createFolder);
        string meshPath = AssetDatabase.GenerateUniqueAssetPath($"{createFolder}/{mesh.name}.asset");
        AssetDatabase.CreateAsset(mesh, meshPath);

        var root = new GameObject(meshEntryName + "_SkinnedTo_" + rootBoneName);
        var bone = new GameObject(rootBoneName);
        bone.transform.SetParent(root.transform);
        bone.transform.localPosition = Vector3.zero;

        // This GameObject's name becomes SkinnedMeshInfo.MeshName on bake — must match SkinnedMeshPath exactly.
        var meshGO = new GameObject(meshEntryName);
        meshGO.transform.SetParent(root.transform);
        var smrNew = meshGO.AddComponent<SkinnedMeshRenderer>();
        smrNew.sharedMesh = mesh;
        smrNew.bones = new[] { bone.transform };
        smrNew.rootBone = bone.transform;
        smrNew.sharedMaterial = sourceMaterial;

        var anim = root.AddComponent<Animator>();
        anim.avatar = AvatarBuilder.BuildGenericAvatar(root, "");

        string prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{createFolder}/{root.name}.prefab");
        var savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string boneSourceInfo = skeletonOverride != null
            ? $"matches Skeleton '{skeletonOverride.name}' BoneInfos[{rootIdx}]"
            : "self-authored, no external Skeleton — bake this with 'Create as Skeleton' ON (no Existing MeshCollection)";
        Debug.Log($"[MeshBaker] built '{prefabPath}' — single bone '{rootBoneName}' ({boneSourceInfo}), mesh entry " +
            $"name '{meshEntryName}' (combined from {combine.Count} source mesh(es)). Remember: the target " +
            $"Fragment's SkinnedMeshPath must equal '{meshEntryName}' exactly.");

        EditorGUIUtility.PingObject(savedPrefab);
        return savedPrefab;
    }

    // Resolves a Skeleton from the mounted vanilla bundle by GUID, fresh, every call — never cache
    // the result in a field (see the file-level fake-null warning above).
    static Skeleton ResolveSkeletonByGuid(string skeletonGuidHex)
    {
        if (string.IsNullOrEmpty(skeletonGuidHex)) return null;
        var guid = new Amplitude.Framework.Guid(skeletonGuidHex.Trim());
        if (guid.IsNull) { Debug.LogError($"[MeshBaker] '{skeletonGuidHex}' did not parse to a valid GUID."); return null; }

        Skeleton sk;
        try { sk = Amplitude.Framework.Asset.AssetDatabase.LoadAsset<Skeleton>(guid); }
        catch (System.Exception e) { Debug.LogError($"[MeshBaker] LoadAsset<Skeleton>({guid}) threw: {e.Message}"); return null; }
        if (sk == null) Debug.LogError($"[MeshBaker] GUID {guid} did not resolve to a loadable Skeleton (wrong type, or not mounted).");
        return sk;
    }

    // MeshCollection.skeleton is `[SerializeField] private Skeleton skeleton;` with no public
    // setter — needed because Reimport() reads it via the SkeletonInstance getter to bone-weight
    // against. Reflection is the only way to write it.
    static void SetSkeletonField(MeshCollection target, Skeleton skeleton)
    {
        var f = typeof(MeshCollection).GetField("skeleton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (f == null) { Debug.LogError("[MeshBaker] MeshCollection.skeleton field not found — can't set skeleton override."); return; }
        f.SetValue(target, skeleton);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parts = path.Split('/');
        string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            var next = cur + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }
}
