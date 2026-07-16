using System.Collections.Generic;
using System.Linq;
using Amplitude.Graphics;
using Amplitude.Mercury.Animation;
using Amplitude.Mercury.Data.World;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Validates a baked fragment end-to-end against the requirements confirmed by decompiling
/// Amplitude.Mercury.Animation.dll / Amplitude.Graphics.dll (see Docs/Workflow.md and
/// Docs/ENCAccessProof-master/docs/). Used by the Unit Visual Workflow wizard's "Validate" step
/// (Tools/Unit Visual Workflow). Tangents/normals are checked but are NOT crash causes — missing
/// ones are silently defaulted by FxMeshContent.InitFrom and only logged
/// (Amplitude.Graphics.Fx.FxMeshContent.EncodingIssueEnum.NoNormal/NoTangent). The checks that DO
/// cause a fragment to fail at runtime are the join/registration ones below, mirrored from:
///   - AnimationManagerContent.OutputLayerFromMaterialGuid (AnimationManagerContent.cs:101)
///   - ShaderReplacementAsset.IsSupported (ShaderReplacementAsset.cs:104)
///   - PresentationPawnFragmentSkinnedMesh.RuntimeMaterial (PresentationPawnFragmentSkinnedMesh.cs:41)
///   - MeshCollection.SourcePrefab / GetFxMeshIndex (MeshCollection.cs)
/// </summary>
internal static class FragmentValidator
{
    internal enum Severity { Info, Warning, Error }
    internal class Finding { public Severity severity; public string message; }

    internal static List<Finding> Validate(PresentationPawnFragmentSkinnedMesh fragment, AnimationManagerContent content)
    {
        var findings = new List<Finding>();
        void Add(Severity s, string msg) => findings.Add(new Finding { severity = s, message = msg });

        // 1. Material assigned at all.
        if (fragment.MaterialRef.IsNull)
        {
            Add(Severity.Error, "Fragment.MaterialRef is null — PresentationPawnFragmentSkinnedMesh.RuntimeMaterial will log " +
                "\"no Material is defined\" and return null.");
        }
        else
        {
            var mat = Amplitude.Framework.Asset.AssetDatabase.LoadAsset<Material>(fragment.MaterialRef);
            if (mat == null)
            {
                Add(Severity.Error, $"MaterialRef {fragment.MaterialRef} does not resolve to a loadable Material asset.");
            }
            else
            {
                // 2. Shader assigned + supported (mirrors ShaderReplacementAsset.IsSupported).
                if (mat.shader == null)
                {
                    Add(Severity.Error, $"Material '{mat.name}' has NO SHADER assigned (m_Shader fileID 0). " +
                        "ShaderReplacementAsset.CreateInstance will fail to build a runtime material from this — " +
                        "this is a hard render-time failure, not a warning. Common cause: importing a vanilla material " +
                        "via a generic Instantiate+CreateAsset clone, which doesn't carry the shader reference across " +
                        "the bundle boundary. Fix: reuse a vanilla material's GUID directly (Tier 1 doesn't need a new " +
                        "material), or re-author with a Humankind-compatible shader.");
                }
                else
                {
                    bool supported = ShaderReplacementAsset.Instance != null && ShaderReplacementAsset.Instance.IsSupported(mat);
                    if (!supported)
                        Add(Severity.Error, $"Material '{mat.name}' uses shader '{mat.shader.name}', which is not in " +
                            "ShaderReplacementAsset's replacement table — IsSupported() returns false. Use a shader the " +
                            "game's FX system recognizes (reuse a vanilla unit's material/shader).");
                    else
                        Add(Severity.Info, $"Material '{mat.name}' shader '{mat.shader.name}' is supported.");
                }
            }

            // 3. OutputLayerEntries must contain this material GUID (AnimationManagerContent.OutputLayerFromMaterialGuid).
            // Only checks the SELECTED content asset — if MaterialRef points at a vanilla material, its
            // OutputLayerEntry lives in the VANILLA AnimationManagerContent instead, so a miss here is
            // expected/fine for that case, not necessarily a bug.
            var entries = content.OutputLayerEntries ?? new OutputLayerEntry[0];
            bool hasEntry = entries.Any(e => e.Material == fragment.MaterialRef);
            if (!hasEntry)
                Add(Severity.Warning, $"No OutputLayerEntry in '{content.name}' matches MaterialRef {fragment.MaterialRef}. " +
                    "If this material is one you authored, AnimationManagerContent.OutputLayerFromMaterialGuid will log " +
                    "\"missing output layer for material...\" and return null — fix via materialDirectories + \"Reimport " +
                    "OutputLayers\" (needs a supported shader, check #2). If this MaterialRef points at a REUSED VANILLA " +
                    "material instead, this miss is expected — its OutputLayerEntry lives in the vanilla " +
                    "AnimationManagerContent, not this mod's, and nothing further is needed here.");
            else
                Add(Severity.Info, "OutputLayerEntry found for this material.");
        }

        // 4. SourcePrefab join: MeshCollection.SourcePrefab must equal Fragment.Prefab.Guid.
        var meshCollection = FindMeshCollectionByPrefab(fragment.Prefab.Guid);
        if (meshCollection == null)
        {
            Add(Severity.Error, $"No MeshCollection project asset has SourcePrefab == Fragment.Prefab.Guid " +
                $"({fragment.Prefab.Guid}). AnimationManager.GetMeshCollection(Prefab.Guid) is the runtime join key " +
                "(PresentationPawnDefinitionAddOn.cs) — without a match, AddOn.Load gets a null MeshCollection.");
        }
        else
        {
            Add(Severity.Info, $"MeshCollection '{meshCollection.name}' SourcePrefab matches Fragment.Prefab.");

            // 4b. SkeletonInstance must be non-null for an animated pawn. AnimationManagerContent.FillFragments
            // (AnimationManagerContent.cs:141) does:
            //   if (... GetMeshCollection(Prefab.Guid).SkeletonInstance != skeleton) continue;
            // so a null SkeletonInstance NEVER equals the pawn's real (non-null) skeleton — the fragment is
            // silently dropped from the render list. This is the #1 cause of "baked fine, invisible in-game"
            // for a Tier 1 swap onto an existing animated unit, NOT a material/shader/output-layer problem.
            if (meshCollection.SkeletonInstance == null)
                Add(Severity.Error, $"MeshCollection '{meshCollection.name}'.SkeletonInstance is NULL. " +
                    "AnimationManagerContent.FillFragments excludes any fragment whose MeshCollection.SkeletonInstance " +
                    "doesn't match the pawn's real (non-null) skeleton — null never matches, so this fragment is " +
                    "silently dropped before rendering. Fix: set a Skeleton override (the target unit's actual " +
                    "Skeleton asset) and re-bake — this writes a non-null SkeletonInstance into the asset.");
            else
                Add(Severity.Info, $"MeshCollection has a SkeletonInstance ('{meshCollection.SkeletonInstance.name}').");

            // 5. Registered in AnimationManagerContent.MeshCollections[] — confirm via its own asset GUID.
            string mcGuidPath = AssetDatabase.GetAssetPath(meshCollection);
            string mcGuidHex = AssetDatabase.AssetPathToGUID(mcGuidPath);
            var registered = content.MeshCollections ?? new Amplitude.Framework.Guid[0];
            var asAmplitudeGuid = new Amplitude.Framework.Guid(mcGuidHex);
            bool isRegistered = registered.Any(g => g == asAmplitudeGuid);
            if (!isRegistered)
                Add(Severity.Error, $"MeshCollection '{meshCollection.name}' (GUID {asAmplitudeGuid}) is not listed in " +
                    $"'{content.name}'.MeshCollections[] ({registered.Length} entries). Run \"Populate\" " +
                    "or add it via fragmentDirectories auto-discovery.");
            else
                Add(Severity.Info, "MeshCollection is registered in AnimationManagerContent.MeshCollections[].");

            // 6. SkinnedMeshPath must match a MeshName in the collection (MeshCollection.GetFxMeshIndex).
            bool nameMatches = HasMeshName(meshCollection, fragment.SkinnedMeshPath);
            if (!nameMatches)
                Add(Severity.Warning, $"Fragment.SkinnedMeshPath '{fragment.SkinnedMeshPath}' does not match any " +
                    "SkinnedMeshInfo.MeshName in the MeshCollection — GetFxMeshIndex falls back to mesh index 0 " +
                    "(wrong/empty mesh renders, not a crash, but not your model either).");
            else
                Add(Severity.Info, $"SkinnedMeshPath '{fragment.SkinnedMeshPath}' matches a baked mesh entry.");

            // 7. Tangent/normal presence — cosmetic only, never a crash (FxMeshContent.InitFrom defaults them).
            CheckTangentsNormals(findings, meshCollection);
        }

        // 8. Registered in the fragments[] list or covered by fragmentDirectories (informational — auto-discovery
        // at build time is confirmed to populate this without manual registration; see project memory).
        string fragPath = AssetDatabase.GetAssetPath(fragment);
        bool inFragmentDirs = false;
        var dirsField = typeof(AnimationManagerContent).GetField("fragmentDirectories",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (dirsField?.GetValue(content) is string[] dirs)
            inFragmentDirs = dirs.Any(d => !string.IsNullOrEmpty(d) && fragPath.Replace('\\', '/').StartsWith(d.Replace('\\', '/')));
        if (!inFragmentDirs)
            Add(Severity.Info, "Fragment's folder is not in AnimationManagerContent.fragmentDirectories — confirm the " +
                "build's auto-discovery covers it (or add the folder explicitly) so the fragment ships registered.");

        return findings;
    }

    static void CheckTangentsNormals(List<Finding> findings, MeshCollection mc)
    {
        var infosField = typeof(MeshCollection).GetField("skinnedMeshInfos",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (infosField?.GetValue(mc) is not System.Array infos || infos.Length == 0) return;

        foreach (var infoObj in infos)
        {
            var meshNameField = infoObj.GetType().GetField("MeshName");
            string meshName = meshNameField?.GetValue(infoObj) as string;
            Mesh mesh;
            try { mesh = mc.GetUnityMesh(meshName); } catch { continue; }
            if (mesh == null) continue;

            bool hasNormals = mesh.normals != null && mesh.normals.Length > 0;
            bool hasTangents = mesh.tangents != null && mesh.tangents.Length > 0;
            if (!hasNormals)
                findings.Add(new Finding { severity = Severity.Warning, message = $"Baked mesh '{meshName}': no normals — FxMeshContent.InitFrom defaults to " +
                    "Vector3.right per-vertex (EncodingIssueEnum.NoNormal). Cosmetic: wrong lighting, not a crash." });
            if (!hasTangents)
                findings.Add(new Finding { severity = Severity.Warning, message = $"Baked mesh '{meshName}': no tangents — defaults to (0,1,0,1) " +
                    "(EncodingIssueEnum.NoTangent). Cosmetic: wrong normal-map lighting, not a crash." });
        }
    }

    static bool HasMeshName(MeshCollection mc, string name)
    {
        var infosField = typeof(MeshCollection).GetField("skinnedMeshInfos",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (infosField?.GetValue(mc) is not System.Array infos) return false;
        foreach (var infoObj in infos)
        {
            var meshNameField = infoObj.GetType().GetField("MeshName");
            if ((meshNameField?.GetValue(infoObj) as string) == name) return true;
        }
        return false;
    }

    internal static MeshCollection FindMeshCollectionByPrefab(Amplitude.Framework.Guid prefabGuid)
    {
        foreach (var guid in AssetDatabase.FindAssets("t:MeshCollection"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var mc = AssetDatabase.LoadAssetAtPath<MeshCollection>(path);
            if (mc != null && mc.SourcePrefab == prefabGuid) return mc;
        }
        return null;
    }
}
