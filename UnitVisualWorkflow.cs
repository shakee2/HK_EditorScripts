using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Amplitude.Mercury.Animation;
using Amplitude.Mercury.Data.World;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

/// <summary>
/// Single step-based wizard replacing the old Tools/Pawn Fragment cluster (Author Window, Mesh
/// Collection Baker, Animation Content Populator, Model Requirements Checker — now folded in as
/// step panels calling into FragmentGuidFix / MeshViability+MeshCollectionBaker /
/// AnimationContentBuilder / FragmentValidator). Districts are a stub — the PresentationDistrict
/// pipeline hasn't been traced the way the unit pipeline has (see Docs/Workflow.md section C).
///
/// Tiers (see Docs/Custom-Unit-Visuals-Combined.md §3 for the full grounding — settled 2026-06-30
/// against Docs/ENCAccessProof-master/docs/Scaling-ManyModels-And-Scoping.md):
///   Tier0a — brand-new pawn-def + brand-new self-contained Skeleton (own rig, no vanilla
///            cross-reference). SETTLED: needs a plugin. AnimationManagerContent's arrays are
///            vanilla-locked (AssetDuplicateSolvingPolicy.Error) and shipping a Skeleton as inert
///            mod data does NOT auto-register it the way fragmentDirectories auto-discovers
///            fragments — confirmed by source, not inferred. The plugin needed is the lightest of
///            any animated tier though: just the generic AnimationResolveDependencies registration
///            merge, no per-pawn repoint, since there's no existing unit being repointed onto.
///            Whether authoring your own PresentationPawnDescription (instead of reusing a vanilla
///            one, as Tier0b/2's tested recipes do) drops even the thin runtime repoint is an
///            untested but source-suggested possibility. Texture is still a live per-frame _MainTex
///            push in every tested recipe so far (OutputLayerEntries are also vanilla-locked); an
///            OutputLayerEntry merge via the same plugin is untested.
///   Tier0b — re-skin an EXISTING vanilla unit in place. Needs the BepInEx live-mesh-patch (the
///            generic manifest-driven injector); this step only bakes the replacement asset + the
///            manifest entry the injector reads.
///   Tier1  — new skinned fragment (e.g. equipment) bound to an existing skeleton. Confirmed
///            no-plugin (project memory: fragmentDirectories auto-discovery, in-game tested). This
///            is the ONLY tier that's genuinely plugin-free — Tier0a is not, despite both starting
///            from "new asset, no vanilla skeleton edit."
///   Tier2  — new Skeleton + clips + override controller. Experimental: registration is proven to
///            work, but a genuinely new Skeleton still hits an open CPU-side index-coordination bug
///            (NOT a hard GPU/Unity limitation — see the plan's grounding section). Structurally
///            unrelated to Tier 1, which never calls AnimationManager.Register(Skeleton) at all.
/// </summary>
public class UnitVisualWorkflow : EditorWindow
{
    const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    enum Tier { Tier0a, Tier0b, Tier1, Tier2 }

    bool _districtsMode;
    Vector2 _scroll;

    // ── Step 1: choose model ───────────────────────────────────────────────────
    GameObject _sourcePrefab;
    bool _runFbxPrep;
    float _targetLength = 4f;
    Vector3 _orientEuler;
    bool _viabilityChecked;
    List<MeshViability.Finding> _viabilityFindings = new();
    FbxPrepPipeline.Result _prepResult;
    GameObject _effectivePrefab;   // _sourcePrefab, or the FBX-prep output once run

    // ── Step 2: choose target ──────────────────────────────────────────────────
    PresentationPawnDefinition _targetDefinition;
    string _targetSearchName = "";
    string _targetInfo = "";
    AdvancedDropdownState _targetMatchDdState = new();
    Amplitude.Framework.Guid _targetTemplateGuid;     // the target's rig prefab guid (Description.Template)
    string _targetDescriptionName = "";
    MeshCollection _existingMeshCollectionForTarget;  // a project MeshCollection already joined to the target, if any
    bool _targetIsProjectAsset;   // captured at resolve time — _targetDefinition itself can go fake-null across a
                                   // domain reload if it's a vanilla (non-project) object, same class of bug as the
                                   // Skeleton-field fix; this bool is a plain value, so it survives that reload.

    // ── Step 3: auto-detect tier ───────────────────────────────────────────────
    bool _autoDetectRan;
    string[] _boneRemap;           // source bone index -> target bone name, on structural match
    string _tierMismatchReason;
    Tier _tier = Tier.Tier0a;

    // ── Step 4: optional texture/material ─────────────────────────────────────
    Material _newMaterial;
    string _materialGuidHex = "";
    bool _shipAtlasAsTexture;

    // ── Step 5: optional rigging/skeleton ──────────────────────────────────────
    string _skeletonGuidHex = "";   // resolved fresh at bake time only — never held as an object field
    bool _createAsSkeleton;
    string _selfAuthoredBoneName = "Base";
    string _meshNameOverride = "";

    // ── Step 6: bake outputs ───────────────────────────────────────────────────
    MeshCollection _bakedMeshCollection;
    UnityEngine.Object _fragmentAsset;
    Texture2D _atlasAsset;

    // ── Step 7: validate ───────────────────────────────────────────────────────
    bool _validated;
    List<FragmentValidator.Finding> _validationFindings = new();

    // ── Step 8: build ──────────────────────────────────────────────────────────
    string _createFolder = "Assets/Resources/New Additions";
    AnimationManagerContent _content;
    string _contentName = "New Additions_AnimationManagerContent";

    [MenuItem("Tools/Unit Visual Workflow", false, 0)]
    static void Open()
    {
        var w = GetWindow<UnitVisualWorkflow>("Unit Visual Workflow");
        w.minSize = new Vector2(620, 480);
    }

    void OnGUI()
    {
        // Long labels ("Target unit", "AnimationManagerContent", etc.) next to an ObjectField get
        // clipped under Unity's default labelWidth on a window this size — widen it, scaled to the
        // window so it still shrinks gracefully if the user resizes down.
        EditorGUIUtility.labelWidth = Mathf.Clamp(position.width * 0.34f, 150f, 260f);

        EditorGUILayout.Space(4);
        int mode = GUILayout.Toolbar(_districtsMode ? 1 : 0, new[] { "Units", "Districts" });
        _districtsMode = mode == 1;
        EditorGUILayout.Space(6);

        if (_districtsMode)
        {
            EditorGUILayout.HelpBox(
                "Districts: unproven. PresentationDistrict resolves visuals through an " +
                "AssetReferenceRepository keyed by StaticString database paths (e.g. " +
                "\"*/District/GroundMaterial\"), a different model from the GUID-array registry " +
                "units use — that path hasn't been traced the way the unit pipeline has. Not implemented.",
                MessageType.Warning);
            return;
        }

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        DrawStep1_ChooseModel();
        DrawStep2_ChooseTarget();
        DrawStep3_AutoDetectTier();
        DrawStep4_TextureMaterial();
        DrawStep5_RiggingSkeleton();
        DrawStep6_Bake();
        DrawStep7_Validate();
        DrawStep8_Build();
        EditorGUILayout.EndScrollView();
    }

    // ── Step 1 ──────────────────────────────────────────────────────────────────
    void DrawStep1_ChooseModel()
    {
        EditorGUILayout.LabelField("1. Choose model", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        _sourcePrefab = (GameObject)EditorGUILayout.ObjectField("Source prefab / FBX", _sourcePrefab, typeof(GameObject), false);
        if (EditorGUI.EndChangeCheck()) { _viabilityChecked = false; _prepResult = null; _effectivePrefab = _sourcePrefab; _autoDetectRan = false; }

        _runFbxPrep = EditorGUILayout.ToggleLeft(
            new GUIContent("Run FBX-prep pipeline", "Combine multi-submesh/multi-material FBX into one mesh + atlas, fix winding, normalize scale/orientation. Skip for an already-clean single-material mesh."),
            _runFbxPrep);
        if (_runFbxPrep)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                _targetLength = EditorGUILayout.FloatField("Target long-axis length", _targetLength);
                _orientEuler = EditorGUILayout.Vector3Field("Orient Euler tweak", _orientEuler);
                using (new EditorGUI.DisabledScope(_sourcePrefab == null))
                    if (GUILayout.Button("Run FBX-Prep")) RunFbxPrep();
                if (_prepResult != null)
                    EditorGUILayout.HelpBox($"Combined mesh: {_prepResult.CombinedMesh.vertexCount} verts, " +
                        $"{_prepResult.FlippedTriangles} tris winding-fixed" +
                        (_prepResult.Atlas != null ? $", atlas {_prepResult.Atlas.width}x{_prepResult.Atlas.height}" : ""), MessageType.Info);
            }
        }

        using (new EditorGUI.DisabledScope(_sourcePrefab == null))
            if (GUILayout.Button("Check Viability")) RunViability();
        foreach (var f in _viabilityFindings) EditorGUILayout.HelpBox(f.message, Sev(f.severity));
        EditorGUILayout.Space(8);
    }

    void RunFbxPrep()
    {
        _prepResult = FbxPrepPipeline.Run(_sourcePrefab, _targetLength, _orientEuler, buildAtlas: true);
        if (_prepResult == null) return;

        // Build a transient prefab wrapping the combined mesh, so the rest of the wizard (viability,
        // bone matching, baking) operates on one consistent GameObject regardless of FBX-prep.
        var go = new GameObject(_sourcePrefab.name + "_Prepped");
        var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = _prepResult.CombinedMesh;
        var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = _prepResult.Material;
        _effectivePrefab = go;
        _atlasAsset = _prepResult.Atlas;
        _viabilityChecked = false;
    }

    void RunViability()
    {
        var prefab = _effectivePrefab ?? _sourcePrefab;
        _viabilityFindings = MeshViability.Check(prefab, _existingMeshCollectionForTarget);
        _viabilityChecked = true;
    }

    // ── Step 2 ──────────────────────────────────────────────────────────────────
    void DrawStep2_ChooseTarget()
    {
        EditorGUILayout.LabelField("2. Choose target", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        _targetDefinition = (PresentationPawnDefinition)EditorGUILayout.ObjectField(
            new GUIContent("Target unit", "A PresentationPawnDefinition — the vanilla (or project) unit whose visual you're replacing."),
            _targetDefinition, typeof(PresentationPawnDefinition), false);
        if (EditorGUI.EndChangeCheck()) { ResolveTarget(); _autoDetectRan = false; }

        EditorGUILayout.BeginHorizontal();
        _targetSearchName = EditorGUILayout.TextField(
            new GUIContent("...or search by name", "Searches the vanilla MercuryDatabases bundle (the Database Browser's own cache) for a PresentationPawnDefinition whose name contains this."),
            _targetSearchName);
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_targetSearchName)))
            if (GUILayout.Button("Find", GUILayout.Width(60)))
            {
                // Reuses the Database Browser's own persistent, self-healing mount of the
                // MercuryDatabases bundle (VanillaDatabaseMount.LoadAllOfType) instead of a one-off
                // raw bundle scan — PresentationPawnDefinition rows live as sub-assets inside a
                // *Collection container (same shape DatabaseBrowser already handles), which a
                // top-level-descriptor-only scan would silently miss.
                string filter = _targetSearchName.Trim().ToLowerInvariant();
                var matches = VanillaDatabaseMount.LoadAllOfType(typeof(PresentationPawnDefinition))
                    .Cast<PresentationPawnDefinition>()
                    .Where(d => d.name.ToLowerInvariant().Contains(filter))
                    .OrderBy(d => d.name)
                    .ToList();

                if (matches.Count == 0)
                    Debug.LogWarning($"[UnitVisualWorkflow] no PresentationPawnDefinition matching '{_targetSearchName}' in the vanilla databases.");
                else if (matches.Count == 1)
                    PickTarget(matches[0]);
                else
                    // Multiple hits are common — culture/era variants share a base name (e.g. a unit
                    // with a Germany/France/etc. visual affinity each register their own Definition).
                    // Let the user choose instead of silently grabbing whichever loaded first.
                    new PawnDefinitionDropdown(_targetMatchDdState, matches, PickTarget).Show(GUILayoutUtility.GetLastRect());
            }
        EditorGUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(_targetInfo)) EditorGUILayout.HelpBox(_targetInfo, MessageType.Info);
        EditorGUILayout.Space(8);
    }

    void PickTarget(PresentationPawnDefinition def)
    {
        _targetDefinition = def;
        ResolveTarget();
        _autoDetectRan = false;
    }

    void ResolveTarget()
    {
        _targetInfo = ""; _targetTemplateGuid = default; _targetDescriptionName = ""; _existingMeshCollectionForTarget = null;
        if (_targetDefinition == null) return;
        _targetIsProjectAsset = AssetDatabase.Contains(_targetDefinition);

        var descRef = FindFieldValue(_targetDefinition, "Description");
        var descGuid = GuidFromRef(descRef);
        if (descGuid == null) { _targetInfo = "Couldn't resolve Description guid on this PresentationPawnDefinition (field not found by reflection)."; return; }

        // PresentationPawnDescription is a database-linking entry (same family as Definition,
        // likely co-resident in the MercuryDatabases bundle) — try the universal Amplitude loader
        // first (works for a project asset or anything in an already-mounted provider, once the
        // database bundle is mounted), and only fall back to the heavier raw multi-bundle scan
        // (VanillaAssetResolver — for true standalone bundle assets, e.g. Skeleton/MeshCollection/
        // prefabs, which are NOT database entries) if that misses.
        VanillaDatabaseMount.TryMount(out _);
        UnityEngine.Object description = Amplitude.Framework.Asset.AssetDatabase.LoadAsset<UnityEngine.Object>(descGuid.Value)
            ?? VanillaAssetResolver.LoadAssetByGuid(descGuid.Value, out _);
        if (description == null) { _targetInfo = $"Description ({descGuid}) didn't resolve to a loadable asset."; return; }
        _targetDescriptionName = description.name;

        var templateRef = FindFieldValue(description, "Template");
        var templateGuid = GuidFromRef(templateRef);
        if (templateGuid == null) { _targetInfo = $"Description '{description.name}' has no resolvable Template field."; return; }
        _targetTemplateGuid = templateGuid.Value;

        _existingMeshCollectionForTarget = FragmentValidator.FindMeshCollectionByPrefab(_targetTemplateGuid);
        _targetInfo = $"Description: {_targetDescriptionName}\nTemplate (rig prefab) GUID: {_targetTemplateGuid}\n" +
                      (_existingMeshCollectionForTarget != null
                          ? $"Existing project MeshCollection already joined to it: {_existingMeshCollectionForTarget.name}"
                          : "No project MeshCollection joined to it yet (expected for an unmodified vanilla unit).");
    }

    // ── Step 3 ──────────────────────────────────────────────────────────────────
    void DrawStep3_AutoDetectTier()
    {
        EditorGUILayout.LabelField("3. Auto-detect tier (bone-structure match)", EditorStyles.boldLabel);
        var prefab = _effectivePrefab ?? _sourcePrefab;
        using (new EditorGUI.DisabledScope(prefab == null || _targetTemplateGuid.IsNull))
            if (GUILayout.Button("Auto-Detect")) RunAutoDetect();

        if (_autoDetectRan)
        {
            // Note: this match is about whether the SOURCE can be cloned/skinned onto the TARGET's existing
            // bone topology (relevant to Tier0b's clone-and-repoint and Tier1's fragment-on-existing-skeleton) —
            // it is NOT a requirement for Tier0a, which deliberately uses its own, unrelated bone structure.
            if (_boneRemap != null)
                EditorGUILayout.HelpBox($"Matched the target's rig — every core vanilla bone is covered (remap spans {_boneRemap.Length} source bones; check the Console for the full name-pair list). Tier0b/Tier1 viable, no new Skeleton topology needed. Source bones with no vanilla equivalent keep their own name and won't drive a vanilla animation channel.", MessageType.Info);
            else
                EditorGUILayout.HelpBox($"Mismatch: {_tierMismatchReason}\nFine for Tier0a (own skeleton) or Tier2; not clean for Tier0b/Tier1 reuse. See the Console for the per-bone match log.", MessageType.Warning);
        }

        _tier = (Tier)GUILayout.Toolbar((int)_tier, new[] { "Tier0a", "Tier0b", "Tier1", "Tier2" });
        switch (_tier)
        {
            case Tier.Tier0a:
                EditorGUILayout.HelpBox(
                    "Self-contained NEW skeleton — your own rig, own bone names, no cross-reference to any vanilla " +
                    "object, no need to match any existing unit's bone structure at all.\n\n" +
                    "SETTLED (2026-06-30): needs a plugin, but the lightest of any animated tier. " +
                    "AnimationManagerContent's arrays are vanilla-locked (mods cannot edit them) and shipping a " +
                    "Skeleton as inert bundle data does NOT auto-register it — explicitly confirmed by " +
                    "Scaling-ManyModels-And-Scoping.md. The fragmentDirectories auto-discovery that works for " +
                    "skinned-mesh FRAGMENTS (Tier1) is a different mechanism and does not extend to " +
                    "MeshCollections[]/skeletons.\n\n" +
                    "Plugin needed: just the generic AnimationResolveDependencies registration merge (no per-pawn " +
                    "repoint since there is no existing unit being repointed onto). Whether authoring your own " +
                    "PresentationPawnDescription also drops the thin runtime repoint is untested but source-suggested. " +
                    "Texture is a live per-frame _MainTex push in every proven recipe (OutputLayerEntries are also " +
                    "vanilla-locked). See Docs/Custom-Unit-Visuals-Combined.md §3.",
                    MessageType.Warning);
                break;
            case Tier.Tier0b: EditorGUILayout.HelpBox("Re-skin an EXISTING vanilla unit in place. Needs the BepInEx generic injector (see Docs/ENCAccessProof-master) to apply the live MeshIndex/_MainTex patch.", MessageType.None); break;
            case Tier.Tier1: EditorGUILayout.HelpBox("New skinned fragment (e.g. equipment) bound to an EXISTING skeleton. Confirmed no-plugin-needed in-game (project memory: a skinned-mesh fragment auto-registered via build-time fragmentDirectories discovery, no manual registration tool run).", MessageType.None); break;
            case Tier.Tier2:
                EditorGUILayout.HelpBox(
                    "Experimental — confirmed working up through GPU mesh upload; the remaining failure is a CPU-side " +
                    "index-coordination bug (manual Register/LoadIFN lands at a different index than the pawn's own " +
                    "AddOn.Load re-registration), confirmed by source to be isolated to Skeleton's GPU bone-bookkeeping " +
                    "path — not a hard GPU/Unity limitation, and structurally unrelated to Tier 0/1. The untried fix is " +
                    "hooking AddOn.Load directly instead of pre-registering eagerly. Unfixed in this pass. NOTE: the " +
                    "PoC's newer Hovercraft/merge recipes (Docs/Custom-Unit-Visuals-Combined.md §3) solve this " +
                    "differently (force-reinvoke Apply(), or register before it naturally runs) — worth revisiting " +
                    "this tier against that mechanism instead of the untried AddOn.Load-hook fix below.",
                    MessageType.Warning);
                break;
        }
        EditorGUILayout.Space(8);
    }

    void RunAutoDetect()
    {
        _autoDetectRan = true; _boneRemap = null; _tierMismatchReason = null;
        var prefab = _effectivePrefab ?? _sourcePrefab;

        var sourceBones = BoneStructureMatcher.Extract(prefab);
        if (sourceBones.Count == 0)
        { _tierMismatchReason = "the source model has no SkinnedMeshRenderer/bones — assign the FBX ROOT (the rig + meshes live under it), not a bone or a mesh sub-object."; _tier = Tier.Tier2; return; }

        // Target bones: read BoneInfos[] off the bundle-loaded MeshCollection in memory (the correct,
        // editor-safe source — the rig PREFAB is a bare armature with no SkinnedMeshRenderer, and the
        // baked .asset must not be put in the project; see project memory
        // baked-vanilla-assets-destabilize-project). Fall back to a rig-prefab SMR walk only if no
        // MeshCollection was resolved.
        var targetBones = BoneStructureMatcher.ExtractFromMeshCollection(_existingMeshCollectionForTarget);
        if (targetBones.Count == 0)
        {
            // Vanilla unit (no project MeshCollection): read the rig prefab. Use the UNION of all its
            // SkinnedMeshRenderers' bones — the first sub-mesh alone is a clothing piece skinning to ~12
            // bones, not the full ~41-bone skeleton.
            var targetRig = VanillaAssetResolver.LoadGameObjectByGuid(_targetTemplateGuid);
            if (targetRig != null) targetBones = BoneStructureMatcher.ExtractAllBones(targetRig);
        }
        if (targetBones.Count == 0)
        { _tierMismatchReason = "couldn't read the target's bones from its MeshCollection or rig prefab."; _tier = Tier.Tier2; return; }

        // Name-based match first (robust when names correspond, e.g. mixamorig:Hips -> Hips, and
        // tolerant of a Mixamo superset); structural (bind-pose) match as the fallback for custom rigs
        // whose bone names don't correspond.
        var remap = BoneStructureMatcher.MatchByName(sourceBones, targetBones, out var nameReason, out var log);
        if (!string.IsNullOrEmpty(log)) Debug.Log("[UnitVisualWorkflow] bone match by name:\n" + log);
        if (remap == null)
        {
            var structRemap = BoneStructureMatcher.MatchStructurally(sourceBones, targetBones, out var structReason);
            if (structRemap == null)
            { _tierMismatchReason = nameReason + (structReason != null ? $"   (structural fallback also failed: {structReason})" : ""); _tier = Tier.Tier2; return; }
            remap = structRemap;
            Debug.Log("[UnitVisualWorkflow] names didn't correspond; structural bind-pose match succeeded.");
        }

        _boneRemap = remap;
        _tier = (_existingMeshCollectionForTarget != null || _targetIsProjectAsset) ? Tier.Tier0a : Tier.Tier0b;
    }

    // ── Step 4 ──────────────────────────────────────────────────────────────────
    void DrawStep4_TextureMaterial()
    {
        EditorGUILayout.LabelField("4. Optional — texture / material", EditorStyles.boldLabel);
        _newMaterial = (Material)EditorGUILayout.ObjectField("New material", _newMaterial, typeof(Material), false);
        _materialGuidHex = EditorGUILayout.TextField(
            new GUIContent("...or vanilla material GUID", "Reuse a vanilla material by pasting its GUID (copy it from Asset Explorer)."),
            _materialGuidHex);

        if (_tier == Tier.Tier0b && _atlasAsset != null)
            _shipAtlasAsTexture = EditorGUILayout.ToggleLeft(
                new GUIContent("Ship the FBX-prep atlas as its own Texture2D asset", "Recorded in the manifest so the BepInEx injector applies it to the target's _MainTex alongside the mesh swap."),
                _shipAtlasAsTexture);
        EditorGUILayout.Space(8);
    }

    // ── Step 5 ──────────────────────────────────────────────────────────────────
    void DrawStep5_RiggingSkeleton()
    {
        if (_tier != Tier.Tier1 && _tier != Tier.Tier2) return;
        EditorGUILayout.LabelField("5. Optional — rigging / skeleton", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Stores only the GUID here, never the loaded object — a Skeleton loaded from the mounted " +
            "vanilla bundle has no Unity GUID/fileID, so holding it in a field doesn't survive a domain " +
            "reload (Unity's window-state serialization can't re-resolve it -> \"fake null\"). Resolved " +
            "fresh, synchronously, inside Bake.", MessageType.None);
        _skeletonGuidHex = EditorGUILayout.TextField(
            new GUIContent("Skeleton GUID", "32-hex, optional — the existing skeleton to bind against."), _skeletonGuidHex);
        if (string.IsNullOrEmpty(_skeletonGuidHex))
            _selfAuthoredBoneName = EditorGUILayout.TextField(
                new GUIContent("Root bone name", "Self-authored — used only when there's no Skeleton GUID to match against."), _selfAuthoredBoneName);
        _createAsSkeleton = EditorGUILayout.ToggleLeft(
            new GUIContent("Create as Skeleton", "Self-authored new rig."), _createAsSkeleton);
        _meshNameOverride = EditorGUILayout.TextField(
            new GUIContent("Mesh entry name", "Must equal the target Fragment's SkinnedMeshPath exactly. Defaults to the source prefab's name."),
            _meshNameOverride);

        var prefab = _effectivePrefab ?? _sourcePrefab;
        using (new EditorGUI.DisabledScope(prefab == null))
            if (GUILayout.Button("Build Single-Bone-Skinned Variant"))
            {
                var built = MeshCollectionBaker.BuildSingleBoneSkinnedVariant(prefab, _skeletonGuidHex, _selfAuthoredBoneName, _meshNameOverride, _createFolder);
                if (built != null) { _sourcePrefab = built; _effectivePrefab = built; _viabilityChecked = false; }
            }
        EditorGUILayout.Space(8);
    }

    // ── Step 6 ──────────────────────────────────────────────────────────────────
    void DrawStep6_Bake()
    {
        EditorGUILayout.LabelField("6. Bake", EditorStyles.boldLabel);
        var prefab = _effectivePrefab ?? _sourcePrefab;
        bool blocked = prefab == null || !_viabilityChecked || _viabilityFindings.Any(f => f.severity == MeshViability.Severity.Error);
        if (prefab != null && !_viabilityChecked)
            EditorGUILayout.HelpBox("Run \"Check Viability\" in step 1 before baking.", MessageType.Warning);
        using (new EditorGUI.DisabledScope(blocked))
            if (GUILayout.Button("Bake")) RunBake();
        if (_bakedMeshCollection != null)
            EditorGUILayout.HelpBox($"Baked '{_bakedMeshCollection.name}'  SourcePrefab={_bakedMeshCollection.SourcePrefab}  " +
                                     $"Meshes={_bakedMeshCollection.SkinnedMeshInfos?.Length ?? 0}", MessageType.Info);
        EditorGUILayout.Space(8);
    }

    void RunBake()
    {
        var prefab = _effectivePrefab ?? _sourcePrefab;

        if (_tier == Tier.Tier0b)
        {
            // Standalone replacement MeshCollection under the SAME mesh-entry name as the vanilla
            // unit's existing one — shipped inert; the BepInEx injector loads it by GUID at runtime.
            string meshName = string.IsNullOrWhiteSpace(_meshNameOverride) ? prefab.name : _meshNameOverride.Trim();
            _bakedMeshCollection = MeshCollectionBaker.Bake(prefab, existing: null, _createFolder, createAsSkeleton: false, skeletonGuidHex: null);
            if (_bakedMeshCollection != null) RenameSoleMeshEntry(_bakedMeshCollection, meshName);
        }
        else
        {
            _bakedMeshCollection = MeshCollectionBaker.Bake(prefab, existing: _existingMeshCollectionForTarget, _createFolder, _createAsSkeleton, _skeletonGuidHex);
        }
        if (_bakedMeshCollection == null) return;

        if (_tier != Tier.Tier0b)
        {
            int kind = 0; // SkinnedMesh
            _fragmentAsset = FragmentGuidFix.CreateFragment(prefab, kind, _createFolder + "/Fragments");
            if (_fragmentAsset != null)
                FragmentGuidFix.AssignAndFix(_fragmentAsset, prefab, _meshNameOverride, _newMaterial);
            if (!string.IsNullOrEmpty(_materialGuidHex) && _fragmentAsset != null)
                FragmentGuidFix.SetMaterialRefFromGuidHex(_fragmentAsset, _materialGuidHex);
        }

        if (_shipAtlasAsTexture && _atlasAsset != null)
        {
            string atlasPath = AssetDatabase.GenerateUniqueAssetPath($"{_createFolder}/{_atlasAsset.name}.asset");
            AssetDatabase.CreateAsset(_atlasAsset, atlasPath);
            AssetDatabase.SaveAssets();
        }
    }

    // The mesh-name lookup (MeshCollection.GetFxMeshIndex) is a string match — for Tier0b the baked
    // entry's name must equal the vanilla unit's existing entry exactly, which Reimport() names after
    // the source prefab by default. Rename it post-bake via reflection (skinnedMeshInfos has no
    // public setter for MeshName).
    static void RenameSoleMeshEntry(MeshCollection mc, string meshName)
    {
        var f = typeof(MeshCollection).GetField("skinnedMeshInfos", BindingFlags.Instance | BindingFlags.NonPublic);
        if (f?.GetValue(mc) is not Array infos || infos.Length == 0) return;
        var item = infos.GetValue(0);
        var nameField = item.GetType().GetField("MeshName");
        nameField?.SetValue(item, meshName);
        infos.SetValue(item, 0);
        EditorUtility.SetDirty(mc);
        AssetDatabase.SaveAssets();
    }

    // ── Step 7 ──────────────────────────────────────────────────────────────────
    void DrawStep7_Validate()
    {
        EditorGUILayout.LabelField("7. Validate", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(_fragmentAsset == null || _content == null))
            if (GUILayout.Button("Run Checks")) RunValidate();
        foreach (var f in _validationFindings) EditorGUILayout.HelpBox(f.message, Sev(f.severity));
        EditorGUILayout.Space(8);
    }

    void RunValidate()
    {
        if (_fragmentAsset is PresentationPawnFragmentSkinnedMesh frag && _content != null)
        {
            _validationFindings = FragmentValidator.Validate(frag, _content);
            _validated = !_validationFindings.Any(f => f.severity == FragmentValidator.Severity.Error);
        }
    }

    // ── Step 8 ──────────────────────────────────────────────────────────────────
    void DrawStep8_Build()
    {
        EditorGUILayout.LabelField("8. Build into mod's AnimationManagerContent", EditorStyles.boldLabel);
        _content = (AnimationManagerContent)EditorGUILayout.ObjectField(
            new GUIContent("Content asset", "The mod's AnimationManagerContent — the registry Populate writes into."),
            _content, typeof(AnimationManagerContent), false);
        _createFolder = EditorGUILayout.TextField("Folder", _createFolder);

        if (GUILayout.Button("Create AnimationManagerContent")) _content = (AnimationManagerContent)AnimationContentBuilder.CreateContent(_createFolder, _contentName);

        bool blocked = _tier != Tier.Tier0b && (_content == null || _fragmentAsset == null || (_validationFindings.Count > 0 && !_validated));
        using (new EditorGUI.DisabledScope(blocked && _tier != Tier.Tier0b))
            if (GUILayout.Button("Build")) RunBuild();
        EditorGUILayout.Space(8);
    }

    void RunBuild()
    {
        if (_tier == Tier.Tier0b)
        {
            if (_bakedMeshCollection == null) { Debug.LogError("[UnitVisualWorkflow] bake a Tier0b replacement first."); return; }
            string mcPath = AssetDatabase.GetAssetPath(_bakedMeshCollection);
            string mcGuidHex = AssetDatabase.AssetPathToGUID(mcPath);
            string atlasGuidHex = _atlasAsset != null && AssetDatabase.Contains(_atlasAsset) ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_atlasAsset)) : "";
            string meshName = string.IsNullOrWhiteSpace(_meshNameOverride) ? (_effectivePrefab ?? _sourcePrefab).name : _meshNameOverride.Trim();
            string targetUnitName = _targetDefinition != null ? _targetDefinition.name : _targetDescriptionName;

            UnitVisualManifest.AddOrUpdate(_createFolder, new UnitVisualManifestEntry
            {
                targetUnit = targetUnitName,
                skeletonGuid = mcGuidHex,
                atlasGuid = atlasGuidHex,
                meshName = meshName,
                settings = ""
            });
            Debug.Log($"[UnitVisualWorkflow] Tier0b manifest entry written for '{targetUnitName}' -> meshCollection {mcGuidHex}.");
            return;
        }

        if (_content == null) { Debug.LogError("[UnitVisualWorkflow] no AnimationManagerContent assigned/created."); return; }
        AnimationContentBuilder.Populate(_content, _createFolder);
        Debug.Log("[UnitVisualWorkflow] build complete.");
    }

    // ── shared reflection helpers ────────────────────────────────────────────────
    static object FindFieldValue(object owner, string fieldName)
    {
        var t = owner?.GetType();
        for (var cur = t; cur != null; cur = cur.BaseType)
        {
            var f = cur.GetField(fieldName, ALL);
            if (f != null) return f.GetValue(owner);
        }
        return null;
    }

    static Amplitude.Framework.Guid? GuidFromRef(object refObj)
    {
        if (refObj == null) return null;
        if (refObj.GetType().FullName == "Amplitude.Framework.Guid") return (Amplitude.Framework.Guid)refObj;
        var gf = FindGuidField(refObj.GetType());
        return gf != null ? (Amplitude.Framework.Guid)gf.GetValue(refObj) : (Amplitude.Framework.Guid?)null;
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

    static MessageType Sev(MeshViability.Severity s) => s == MeshViability.Severity.Error ? MessageType.Error : s == MeshViability.Severity.Warning ? MessageType.Warning : MessageType.Info;
    static MessageType Sev(FragmentValidator.Severity s) => s == FragmentValidator.Severity.Error ? MessageType.Error : s == FragmentValidator.Severity.Warning ? MessageType.Warning : MessageType.Info;

    // ── "Find" match picker — multiple culture/era variants commonly share a base name ──────────
    class PawnDefinitionDropdown : AdvancedDropdown
    {
        readonly List<PresentationPawnDefinition> _matches;
        readonly Action<PresentationPawnDefinition> _onPick;

        public PawnDefinitionDropdown(AdvancedDropdownState state, List<PresentationPawnDefinition> matches, Action<PresentationPawnDefinition> onPick) : base(state)
        {
            _matches = matches; _onPick = onPick;
            minimumSize = new Vector2(320, 360);
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem($"{_matches.Count} matches");
            foreach (var m in _matches) root.AddChild(new AdvancedDropdownItem(m.name));
            return root;
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            var picked = _matches.FirstOrDefault(m => m.name == item.name);
            if (picked != null) _onPick(picked);
        }
    }
}

// ── Tier0b manifest — the contract the BepInEx generic injector reads ──────────
[Serializable]
internal class UnitVisualManifestEntry
{
    public string targetUnit;
    public string skeletonGuid;
    public string atlasGuid;
    public string meshName;
    public string settings;
}

[Serializable]
internal class UnitVisualManifestData
{
    public List<UnitVisualManifestEntry> entries = new();
}

internal static class UnitVisualManifest
{
    const string FileName = "UnitVisualManifest.json";

    internal static void AddOrUpdate(string folder, UnitVisualManifestEntry entry)
    {
        if (!AssetDatabase.IsValidFolder(folder)) EnsureFolder(folder);
        string path = $"{folder}/{FileName}";
        var data = Load(path);
        int idx = data.entries.FindIndex(e => e.targetUnit == entry.targetUnit && e.meshName == entry.meshName);
        if (idx >= 0) data.entries[idx] = entry; else data.entries.Add(entry);

        string json = JsonUtility.ToJson(data, true);
        File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), path), json);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
    }

    static UnitVisualManifestData Load(string path)
    {
        string full = Path.Combine(Directory.GetCurrentDirectory(), path);
        if (!File.Exists(full)) return new UnitVisualManifestData();
        try { return JsonUtility.FromJson<UnitVisualManifestData>(File.ReadAllText(full)) ?? new UnitVisualManifestData(); }
        catch { return new UnitVisualManifestData(); }
    }

    static void EnsureFolder(string path)
    {
        var parts = path.Split('/'); string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        { var next = cur + "/" + parts[i]; if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]); cur = next; }
    }
}
