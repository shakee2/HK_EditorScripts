using System.Collections.Generic;
using System.Linq;
using System.Text;
using Amplitude.Mercury.Animation;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tier 1 ("mesh swap") baker — bakes a prefab's renderers into a MeshCollection asset.
///
/// Confirmed against the decompiled Amplitude.Mercury.Animation.dll shipped with this project:
/// MeshCollection.Reimport() (public, virtual) already does the whole bake — instantiate the
/// prefab assigned via SetPrefab, walk its SkinnedMeshRenderer[]/MeshRenderer[], and pack each
/// shared mesh into a SkinnedMeshInfo.FxMeshContent. No separate FxMesh asset or "Assets ->
/// Create -> Amplitude/Fx/Mesh" step is needed for this path — that menu authors a *standalone*
/// FxMesh, which Reimport() does not consume.
///
/// What this tool adds on top of the bare API:
///   - a viability check (mesh readable, vertices/UVs within the GPU's EncodingBBox) run BEFORE
///     baking, since Reimport() performs no such validation and out-of-range data renders as
///     clamped garbage rather than failing loudly.
///   - SetPrefab + Reimport + save, for either an existing MeshCollection (mesh swap on an
///     already-registered unit, skeleton/material untouched) or a newly created one.
///
/// This only changes mesh geometry. Skeleton baking, clip baking, and texture/material work are
/// out of scope here (Workflow.md's Tier 2 / Tier 3). After baking, the asset still needs to be
/// picked up by Tools/Pawn Fragment/Animation Content Populator ("Scan & Populate") so its GUID
/// is registered in AnimationManagerContent, and a PresentationPawnFragmentSkinnedMesh's Prefab
/// (Tools/Pawn Fragment/Author Window) must reference the SAME prefab so the GUIDs join
/// (MeshCollection.SourcePrefab == fragment.Prefab.Guid is the runtime lookup key).
/// </summary>
public class Tier1MeshBaker : EditorWindow
{
    // GPU mesh-content encoding limits, confirmed in FxComponentMeshContentManager.ContentLayer.
    static readonly Vector3 PosMin = new Vector3(-8f, -16f, -8f);
    static readonly Vector3 PosMax = new Vector3(8f, 16f, 8f);
    static readonly Vector2 UvMin = new Vector2(-2f, 0f);
    static readonly Vector2 UvMax = new Vector2(6f, 1f);

    GameObject _prefab;
    MeshCollection _existing;
    string _createFolder = "Assets/Resources/New Additions/MeshCollections";
    string _forcePrefabGuidHex = "";
    bool _createAsSkeleton = false;
    Skeleton _skeletonOverride;   // assigned in-memory only (see Bake()); doesn't need to be a project asset
    string _skeletonGuidHex = "";
    string _meshNameOverride = "";
    string _selfAuthoredBoneName = "Base";

    enum Severity { Info, Warning, Error }
    class Finding { public Severity severity; public string message; }
    List<Finding> _findings = new();
    bool _checked;

    [MenuItem("Tools/Pawn Fragment/Mesh Collection Baker (Tier 1)", false, 4)]
    static void Open()
    {
        var w = GetWindow<Tier1MeshBaker>("Mesh Baker (Tier 1)");
        w.minSize = new Vector2(420, 320);
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Tier 1 — mesh swap baker", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Bakes a prefab's meshes into a MeshCollection (MeshCollection.Reimport()). Reuses " +
            "whatever skeleton/material the target already has — only the geometry changes.",
            MessageType.Info);

        EditorGUI.BeginChangeCheck();
        _prefab = (GameObject)EditorGUILayout.ObjectField("Source Prefab", _prefab, typeof(GameObject), false);
        _existing = (MeshCollection)EditorGUILayout.ObjectField("Existing MeshCollection (optional)", _existing, typeof(MeshCollection), false);
        if (EditorGUI.EndChangeCheck()) _checked = false;

        if (_existing == null)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("No existing MeshCollection selected — a new one will be created:", EditorStyles.miniBoldLabel);
            _createFolder = EditorGUILayout.TextField("Folder", _createFolder);
            _createAsSkeleton = EditorGUILayout.Toggle(
                new GUIContent("Create as Skeleton", "Skeleton : MeshCollection — same Reimport() bake, but also " +
                    "fills BoneInfos[] from the prefab's bone hierarchy. Use this to bake a clean, properly-" +
                    "indexed Skeleton asset directly from a rig prefab (e.g. Human_Male_0) instead of trying to " +
                    "clone/import an existing vanilla Skeleton asset — generic Instantiate+CreateAsset clones of " +
                    "Skeleton/MeshCollection don't get recognized by Amplitude's own asset registry."),
                _createAsSkeleton);
        }
        else if (_existing.SkeletonInstance != null)
        {
            EditorGUILayout.HelpBox($"Target has a Skeleton ({_existing.SkeletonInstance.name}) — Reimport will rebuild bone weights against it. Bone names on the new prefab must match its BoneInfos.", MessageType.None);
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Skeleton to bake bone weights against (optional)", EditorStyles.miniBoldLabel);
        EditorGUILayout.HelpBox(
            "Doesn't need to be a project asset — a vanilla Skeleton loaded via Asset Explorer works too. " +
            "In Asset Explorer, select it and click Ping (sets Unity's Selection to that live object even " +
            "though it's not an asset), then click 'Grab from Selection' below. Used only transiently during " +
            "Reimport() to compute correct bone indices; the result is baked into the mesh bytes, so the " +
            "Skeleton field itself doesn't need to survive a save/reload afterward.", MessageType.None);
        EditorGUILayout.BeginHorizontal();
        _skeletonOverride = (Skeleton)EditorGUILayout.ObjectField("Skeleton override", _skeletonOverride, typeof(Skeleton), false);
        if (GUILayout.Button("Grab from Selection", GUILayout.Width(140)))
        {
            if (Selection.activeObject is Skeleton sk) { _skeletonOverride = sk; Debug.Log($"[Tier1Baker] grabbed '{sk.name}' from Selection."); }
            else Debug.LogWarning("[Tier1Baker] Selection.activeObject is not a Skeleton — Ping it in Asset Explorer first.");
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        _skeletonGuidHex = EditorGUILayout.TextField("...or by GUID (32-hex)", _skeletonGuidHex);
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_skeletonGuidHex)))
            if (GUILayout.Button("Load by GUID", GUILayout.Width(140))) LoadSkeletonByGuid();
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.HelpBox(
            "The GUID copied from Asset Explorer's right-click menu (e.g. 'Copy GUID' on a Skeleton row) loads " +
            "the live vanilla Skeleton directly via Amplitude's AssetDatabase — no Selection/Ping dance needed.",
            MessageType.None);

        if (_prefab != null)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Reimport() bone-weights by NAME: it needs a SkinnedMeshRenderer in the prefab whose bones[] " +
                "Transforms are named to match the target Skeleton's BoneInfos[].Name. A plain MeshRenderer " +
                "(no rig) matches nothing once a Skeleton is involved, and bakes ZERO entries into " +
                "skinnedMeshInfos (empty list, even though the bake 'succeeds'). If your source prefab has no " +
                "SkinnedMeshRenderer, use the button below to auto-build a single-bone-skinned variant. " +
                "If a Skeleton override above is set, it binds to THAT skeleton's root bone name (reusing an " +
                "existing skeleton — only do this with a Skeleton that's a real persisted asset, not a cloned " +
                "vanilla one, which Amplitude's registry won't recognize). If no Skeleton override is set, it " +
                "picks the bone name below instead — bake that result with 'Create as Skeleton' ON (no Existing " +
                "MeshCollection) to self-author a brand-new, reliably-recognized Skeleton, the same way the " +
                "proven Zeppelin recipe avoided cloning vanilla's skeleton entirely.",
                MessageType.Warning);
            if (_skeletonOverride == null)
                _selfAuthoredBoneName = EditorGUILayout.TextField(
                    new GUIContent("Root bone name (no Skeleton override)", "Used only when self-authoring a " +
                        "brand-new Skeleton (no external skeleton to match names against)."),
                    _selfAuthoredBoneName);
            _meshNameOverride = EditorGUILayout.TextField(
                new GUIContent("Mesh entry name", "The resulting SkinnedMeshInfo.MeshName — must equal the " +
                    "Fragment's SkinnedMeshPath exactly (it's a string-match lookup, GetFxMeshIndex(meshName)). " +
                    "Convention seen on vanilla units: this matches the unit's own prefab/Description name " +
                    "(e.g. 'Unit_Era6_Common_HelicopterGunships_01'), not your source asset's name. Leave blank " +
                    "to default to the source prefab's name."),
                _meshNameOverride);
            if (GUILayout.Button("Build Single-Bone-Skinned Variant"))
                BuildSingleBoneSkinnedVariant();
        }

        EditorGUILayout.Space(6);
        using (new EditorGUI.DisabledScope(_prefab == null))
            if (GUILayout.Button("Check Viability")) RunViability();

        if (_checked) DrawFindings();

        EditorGUILayout.Space(6);
        bool blocked = !_checked || _findings.Any(f => f.severity == Severity.Error);
        using (new EditorGUI.DisabledScope(_prefab == null || blocked))
            if (GUILayout.Button("Bake / Reimport")) Bake();

        if (_existing != null)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Identity override (experimental)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Stamps this MeshCollection's SourcePrefab GUID to an arbitrary value WITHOUT re-baking — " +
                "does not touch the mesh data you already baked above. For testing whether a mod-authored " +
                "MeshCollection can stand in for an EXISTING (e.g. vanilla) skeleton/rig by sharing its " +
                "SourcePrefab GUID. Bake your mesh first (above), THEN force the identity — forcing first and " +
                "baking after would make Reimport() try to load a GameObject from the forced GUID instead of " +
                "your prefab.", MessageType.Warning);
            _forcePrefabGuidHex = EditorGUILayout.TextField("SourcePrefab GUID (32-hex)", _forcePrefabGuidHex);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_forcePrefabGuidHex)))
                if (GUILayout.Button("Force SourcePrefab GUID (no reimport)")) ForcePrefabGuid();
        }
    }

    void DrawFindings()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Viability", EditorStyles.boldLabel);
        if (_findings.Count == 0)
        {
            EditorGUILayout.HelpBox("No issues found.", MessageType.Info);
            return;
        }
        foreach (var f in _findings)
        {
            var type = f.severity == Severity.Error ? MessageType.Error
                      : f.severity == Severity.Warning ? MessageType.Warning
                      : MessageType.Info;
            EditorGUILayout.HelpBox(f.message, type);
        }
    }

    // ── Viability gate ─────────────────────────────────────────────────────────
    void RunViability()
    {
        _findings = new List<Finding>();
        _checked = true;

        var skinned = _prefab.GetComponentsInChildren<SkinnedMeshRenderer>();
        var statics = _prefab.GetComponentsInChildren<MeshRenderer>()
            .Where(mr => mr.GetComponent<MeshFilter>() != null).ToArray();

        if (skinned.Length == 0 && statics.Length == 0)
        {
            _findings.Add(new Finding { severity = Severity.Error, message = "No SkinnedMeshRenderer or MeshRenderer found in the prefab." });
            return;
        }

        foreach (var smr in skinned) CheckMesh(smr.sharedMesh, smr.name);
        foreach (var mr in statics) CheckMesh(mr.GetComponent<MeshFilter>().sharedMesh, mr.name);

        if (skinned.Length > 0 && _existing != null && _existing.SkeletonInstance == null)
            _findings.Add(new Finding { severity = Severity.Warning, message = "Prefab has skinned meshes, but the target MeshCollection has no Skeleton assigned — Reimport will bake it as a static mesh (all weight on bone 0)." });

        if (skinned.Length > 0 && (_existing == null || _existing.SkeletonInstance == null))
            _findings.Add(new Finding { severity = Severity.Info, message = "No Skeleton on the target — this stays a Tier 1 static-style bake. Use a Tier 2 tool to bake a Skeleton if this unit needs its own rig/animations." });

        if (_findings.Count == 0)
            _findings.Add(new Finding { severity = Severity.Info, message = "All meshes readable and within the GPU encoding bounds." });
    }

    void CheckMesh(Mesh mesh, string rendererName)
    {
        if (mesh == null)
        {
            _findings.Add(new Finding { severity = Severity.Error, message = $"'{rendererName}': no shared mesh assigned." });
            return;
        }
        if (!mesh.isReadable)
        {
            _findings.Add(new Finding { severity = Severity.Error, message = $"'{mesh.name}' is not readable — enable Read/Write in its model import settings." });
            return;
        }

        var verts = mesh.vertices;
        int outOfPos = verts.Count(v => v.x < PosMin.x || v.x > PosMax.x || v.y < PosMin.y || v.y > PosMax.y || v.z < PosMin.z || v.z > PosMax.z);
        if (outOfPos > 0)
            _findings.Add(new Finding { severity = Severity.Warning, message = $"'{mesh.name}': {outOfPos}/{verts.Length} vertices outside the position EncodingBBox {PosMin}-{PosMax}. Will render clamped/garbage." });

        var uvs = mesh.uv;
        int outOfUv = uvs.Count(u => u.x < UvMin.x || u.x > UvMax.x || u.y < UvMin.y || u.y > UvMax.y);
        if (outOfUv > 0)
            _findings.Add(new Finding { severity = Severity.Warning, message = $"'{mesh.name}': {outOfUv}/{uvs.Length} UVs outside the UV EncodingBBox {UvMin}-{UvMax}. Will render clamped/garbage." });
    }

    // ── Bake ───────────────────────────────────────────────────────────────────
    void Bake()
    {
        var target = _existing;
        bool created = false;
        if (target == null)
        {
            EnsureFolder(_createFolder);
            target = _createAsSkeleton
                ? ScriptableObject.CreateInstance<Skeleton>()
                : ScriptableObject.CreateInstance<MeshCollection>();
            target.name = _prefab.name + (_createAsSkeleton ? "_Skeleton" : "_MeshCollection");
            string path = AssetDatabase.GenerateUniqueAssetPath($"{_createFolder}/{target.name}.asset");
            AssetDatabase.CreateAsset(target, path);
            created = true;
        }

        Undo.RecordObject(target, "Bake MeshCollection");
        target.SetPrefab(_prefab);
        if (_skeletonOverride != null) SetSkeletonField(target, _skeletonOverride);
        target.Reimport();
        EditorUtility.SetDirty(target);
        AssetDatabase.SaveAssets();

        var sb = new StringBuilder();
        sb.AppendLine($"[Tier1Baker] {(created ? "created" : "updated")} '{target.name}' from prefab '{_prefab.name}'.");
        sb.AppendLine($"  SourcePrefab GUID: {target.SourcePrefab}");
        sb.AppendLine($"  Meshes baked: {target.SkinnedMeshInfos?.Length ?? 0}");
        sb.AppendLine("Next: run Tools/Pawn Fragment/Animation Content Populator (Scan & Populate) so this " +
                       "GUID is registered, and confirm a PresentationPawnFragmentSkinnedMesh's Prefab references " +
                       "this same prefab so the join resolves.");
        Debug.Log(sb.ToString());

        EditorGUIUtility.PingObject(target);
        if (_existing == null) _existing = target;
    }

    void ForcePrefabGuid()
    {
        var hex = (_forcePrefabGuidHex ?? "").Trim();
        var guid = new Amplitude.Framework.Guid(hex);
        if (guid.IsNull) { Debug.LogError($"[Tier1Baker] '{hex}' did not parse to a valid GUID."); return; }

        Undo.RecordObject(_existing, "Force MeshCollection SourcePrefab GUID");
        _existing.SetPrefab(guid);   // Guid overload: just stamps `prefab`, no Reimport/reload.
        EditorUtility.SetDirty(_existing);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Tier1Baker] '{_existing.name}'.SourcePrefab forced to {guid} (mesh data untouched).");
    }

    // ── Skeleton override helpers ───────────────────────────────────────────────
    void LoadSkeletonByGuid()
    {
        var hex = (_skeletonGuidHex ?? "").Trim();
        var guid = new Amplitude.Framework.Guid(hex);
        if (guid.IsNull) { Debug.LogError($"[Tier1Baker] '{hex}' did not parse to a valid GUID."); return; }

        Skeleton sk = null;
        try { sk = Amplitude.Framework.Asset.AssetDatabase.LoadAsset<Skeleton>(guid); }
        catch (System.Exception e) { Debug.LogError($"[Tier1Baker] LoadAsset<Skeleton>({guid}) threw: {e.Message}"); return; }

        if (sk == null) { Debug.LogError($"[Tier1Baker] GUID {guid} did not resolve to a loadable Skeleton (wrong type, or not mounted — open it in Asset Explorer first)."); return; }
        _skeletonOverride = sk;
        Debug.Log($"[Tier1Baker] loaded Skeleton '{sk.name}' (BonesCount={sk.BonesCount}) from GUID {guid}.");
    }

    // Builds a new prefab: a single bone named after the override Skeleton's root BoneInfo, with a
    // SkinnedMeshRenderer 100%-weighted to it, combining every MeshFilter/SkinnedMeshRenderer mesh
    // found in the source prefab. This is the missing piece Reimport() needs to bone-weight by name
    // against a real (non-null) Skeleton — without it, a plain static mesh bakes zero
    // skinnedMeshInfos entries once a Skeleton override is set (see the proven ENC Zeppelin recipe,
    // which builds exactly this kind of single-bone rig before baking).
    void BuildSingleBoneSkinnedVariant()
    {
        if (_prefab == null) return;

        string rootBoneName;
        int rootIdx = -1;
        if (_skeletonOverride != null)
        {
            var boneInfos = _skeletonOverride.BoneInfos;
            if (boneInfos == null || boneInfos.Length == 0)
            {
                Debug.LogError("[Tier1Baker] Skeleton override has no BoneInfos — can't pick a root bone name.");
                return;
            }
            rootIdx = System.Array.FindIndex(boneInfos, b => b.ParentIndex < 0);
            if (rootIdx < 0) rootIdx = 0;
            rootBoneName = boneInfos[rootIdx].Name;
        }
        else
        {
            // No external Skeleton to match — self-authoring a brand-new one (bake this result with
            // "Create as Skeleton" ON), so any bone name works as long as it's used consistently below.
            rootBoneName = string.IsNullOrWhiteSpace(_selfAuthoredBoneName) ? "Base" : _selfAuthoredBoneName.Trim();
        }

        // Gather every mesh in the source prefab (static or already-skinned) to combine into one.
        var combine = new List<CombineInstance>();
        Material sourceMaterial = null;
        foreach (var mf in _prefab.GetComponentsInChildren<MeshFilter>())
        {
            var mr = mf.GetComponent<MeshRenderer>();
            if (mf.sharedMesh == null) continue;
            combine.Add(new CombineInstance { mesh = mf.sharedMesh, transform = mf.transform.localToWorldMatrix });
            if (sourceMaterial == null && mr != null) sourceMaterial = mr.sharedMaterial;
        }
        foreach (var smr in _prefab.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (smr.sharedMesh == null) continue;
            combine.Add(new CombineInstance { mesh = smr.sharedMesh, transform = smr.transform.localToWorldMatrix });
            if (sourceMaterial == null) sourceMaterial = smr.sharedMaterial;
        }
        if (combine.Count == 0)
        {
            Debug.LogError("[Tier1Baker] no MeshFilter/SkinnedMeshRenderer with a mesh found in the source prefab.");
            return;
        }

        // The mesh entry's name (= SkinnedMeshInfo.MeshName after Reimport) must equal the target Fragment's
        // SkinnedMeshPath exactly — that's the string-match GetFxMeshIndex(meshName) looks up at runtime.
        // Convention on vanilla units is this matches the unit's own name, NOT the source asset's name, so
        // default to the source prefab's name only as a fallback, not an assumption.
        string meshEntryName = string.IsNullOrWhiteSpace(_meshNameOverride) ? _prefab.name : _meshNameOverride.Trim();

        var mesh = new Mesh { name = meshEntryName + "_SkinnedMesh" };
        mesh.CombineMeshes(combine.ToArray(), true, true);
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, mesh.vertexCount).ToArray();
        mesh.bindposes = new[] { Matrix4x4.identity };
        mesh.RecalculateBounds();
        if (mesh.normals == null || mesh.normals.Length == 0) mesh.RecalculateNormals();
        mesh.RecalculateTangents();

        EnsureFolder(_createFolder);
        string meshPath = AssetDatabase.GenerateUniqueAssetPath($"{_createFolder}/{mesh.name}.asset");
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

        string prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{_createFolder}/{root.name}.prefab");
        var savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string boneSourceInfo = _skeletonOverride != null
            ? $"matches Skeleton '{_skeletonOverride.name}' BoneInfos[{rootIdx}]"
            : "self-authored, no external Skeleton — bake this with 'Create as Skeleton' ON (no Existing MeshCollection)";
        Debug.Log($"[Tier1Baker] built '{prefabPath}' — single bone '{rootBoneName}' ({boneSourceInfo}), mesh entry " +
            $"name '{meshEntryName}' (combined from {combine.Count} source mesh(es)). Set as Source Prefab below, " +
            $"then Bake/Reimport. Remember: the target Fragment's SkinnedMeshPath must equal '{meshEntryName}' exactly.");

        _prefab = savedPrefab;
        _checked = false;
        EditorGUIUtility.PingObject(savedPrefab);
    }

    // MeshCollection.skeleton is `[SerializeField] private Skeleton skeleton;` with no public
    // setter — needed because Reimport() reads it via the SkeletonInstance getter to bone-weight
    // against. Reflection is the only way to write it (mirrors the pattern used throughout
    // PawnFragmentAuthor.cs/AnimationManagerContent.cs for fields without a public setter).
    static void SetSkeletonField(MeshCollection target, Skeleton skeleton)
    {
        var f = typeof(MeshCollection).GetField("skeleton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (f == null) { Debug.LogError("[Tier1Baker] MeshCollection.skeleton field not found — can't set skeleton override."); return; }
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
