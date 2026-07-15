using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Structural bone matcher for the Unit Visual Workflow wizard's auto tier-detection step.
///
/// A registered Skeleton's BoneInfos[] is derived entirely from its source rig prefab's
/// SkinnedMeshRenderer.bones[] (Skeleton.Reimport walks exactly that hierarchy — see
/// Docs/Workflow.md). So instead of needing the runtime-only Skeleton object (which doesn't exist
/// in the editor), this walks the rig PREFABS directly — ordinary, editor-safe assets — and
/// compares bone topology (count, parent/depth structure) between a source model and a target
/// unit's rig. Same topology means the source can be skinned to the target's existing skeleton
/// (Tier0/1, no Skeleton bake needed); different topology means a new Skeleton is required
/// (Tier2), which is the actual thing that decides the tier — not how the model looks.
/// </summary>
internal static class BoneStructureMatcher
{
    internal class BoneInfo
    {
        public string Name;
        public int ParentIndex;   // index into the same list, or -1 for a root
        public int Depth;
        public Vector3 BindPoseTranslation;
    }

    const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Reads BoneInfos[] directly off a bundle-loaded MeshCollection/Skeleton object (in memory) via
    // reflection. This is the correct TARGET source: the vanilla rig PREFAB is a bare armature with no
    // SkinnedMeshRenderer (the body mesh is built at load), so Extract(rigPrefab) returns nothing — and
    // the baked .asset must NOT be put in the project (it crashes the editor; see project memory
    // baked-vanilla-assets-destabilize-project). The live MeshCollection's BoneInfos[] is populated in
    // RAM (same data HovercraftProbe read off a vanilla skeleton).
    internal static List<BoneInfo> ExtractFromMeshCollection(object meshCollection)
    {
        var infos = new List<BoneInfo>();
        if (meshCollection == null) return infos;

        var arr = GetMember(meshCollection, "BoneInfos") as IEnumerable;
        if (arr == null) { Debug.LogWarning("[BoneMatcher] target MeshCollection has no readable BoneInfos[]."); return infos; }

        foreach (var bi in arr)
        {
            if (bi == null) continue;
            string name = GetMember(bi, "Name") as string;
            int parent = ToInt(GetMember(bi, "ParentIndex"), -1);
            int depth = ToInt(GetMember(bi, "Depth"), 0);
            Vector3 trans = Vector3.zero;
            var bindPose = GetMember(bi, "BindPose");
            if (bindPose != null && GetMember(bindPose, "Translation") is Vector3 v) trans = v;
            infos.Add(new BoneInfo { Name = name, ParentIndex = parent, Depth = depth, BindPoseTranslation = trans });
        }
        return infos;
    }

    /// <summary>
    /// Bones = the UNION of every SkinnedMeshRenderer's bones[] in the hierarchy. A single renderer
    /// only references the bones that skin ITS mesh — for the vanilla Human rig the first sub-mesh is a
    /// small clothing piece skinning to ~12 bones, while the full skeleton is ~41. Unioning across all
    /// body/clothing meshes recovers the whole rig, and naturally excludes non-bone mesh nodes (they're
    /// never in any bones[] array). Use this for the TARGET rig prefab (the editor-safe stand-in when no
    /// MeshCollection is resolved). Parent/depth are derived from the bones' actual Transform hierarchy.
    /// </summary>
    internal static List<BoneInfo> ExtractAllBones(GameObject root)
    {
        var infos = new List<BoneInfo>();
        if (root == null) return infos;

        var boneList = new List<Transform>();
        var seen = new HashSet<Transform>();
        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (smr.bones != null)
                foreach (var b in smr.bones)
                    if (b != null && seen.Add(b)) boneList.Add(b);
        if (boneList.Count == 0) return infos;

        var indexOf = new Dictionary<Transform, int>();
        for (int i = 0; i < boneList.Count; i++) indexOf[boneList[i]] = i;
        for (int i = 0; i < boneList.Count; i++)
        {
            var tr = boneList[i];
            int parentIndex = (tr.parent != null && indexOf.TryGetValue(tr.parent, out var pi)) ? pi : -1;
            int depth = 0; for (var p = tr.parent; p != null && indexOf.ContainsKey(p); p = p.parent) depth++;
            infos.Add(new BoneInfo { Name = tr.name, ParentIndex = parentIndex, Depth = depth, BindPoseTranslation = tr.position });
        }
        return infos;
    }

    /// <summary>
    /// Name-based match: strips namespace prefixes (e.g. "mixamorig:Hips" -> "Hips") and pairs source
    /// bones to target bones by name, case-insensitively. Tolerant of the source being a SUPERSET — a
    /// Mixamo rig carries extra finger/twist joints the vanilla rig has no slot for; those keep their
    /// own (stripped) name and simply don't drive a vanilla animation channel. Succeeds when every
    /// CORE target bone (everything except Dummy_Root / Leaf*Roll twists / Weapon_* attach points) is
    /// covered. Far more robust than bind-pose geometry when the names already correspond, which they
    /// do for Mixamo vs the vanilla Human_Skeleton. Emits a human-readable pair log for verification.
    /// Returns source-index -> target-name remap, or null + reason if a core bone is unmatched.
    /// </summary>
    internal static string[] MatchByName(List<BoneInfo> source, List<BoneInfo> target, out string reason, out string log)
    {
        reason = null;
        var sb = new StringBuilder();
        if (source.Count == 0 || target.Count == 0)
        { reason = "one of the rigs has no bones to compare."; log = ""; return null; }

        var targetByKey = new Dictionary<string, BoneInfo>();
        foreach (var t in target) targetByKey[StripName(t.Name).ToLowerInvariant()] = t;

        var remap = new string[source.Count];
        var matchedKeys = new HashSet<string>();
        int matched = 0;
        for (int i = 0; i < source.Count; i++)
        {
            var sName = source[i].Name ?? $"(bone {i})";
            var key = StripName(sName).ToLowerInvariant();
            if (targetByKey.TryGetValue(key, out var t))
            {
                remap[i] = t.Name;                 // rename source bone to the exact vanilla name
                matchedKeys.Add(key);
                matched++;
                sb.AppendLine($"  {sName}  ->  {t.Name}");
            }
            else
            {
                remap[i] = StripName(sName);        // extra source bone: keep its stripped name
                sb.AppendLine($"  {sName}  ->  (no vanilla match, kept as '{StripName(sName)}')");
            }
        }

        var unmatchedCore = new List<string>();
        var unmatchedOptional = new List<string>();
        foreach (var t in target)
        {
            if (matchedKeys.Contains(StripName(t.Name).ToLowerInvariant())) continue;
            (IsOptionalBone(t.Name) ? unmatchedOptional : unmatchedCore).Add(t.Name);
        }

        sb.Insert(0, $"matched {matched}/{source.Count} source bones; {target.Count - unmatchedCore.Count - unmatchedOptional.Count}/{target.Count} target bones covered.\n");
        if (unmatchedOptional.Count > 0)
            sb.AppendLine($"unmatched OPTIONAL target bones (twist/attach/root — fine, vanilla anim still poses the core): {string.Join(", ", unmatchedOptional)}");

        if (matched == 0)
        { reason = "no bones matched by name (the rigs' names don't correspond)."; log = sb.ToString(); return null; }

        if (unmatchedCore.Count > 0)
        {
            reason = $"source rig is missing {unmatchedCore.Count} core vanilla bone(s): {string.Join(", ", unmatchedCore)} " +
                     "— vanilla animation won't drive those joints. You can still proceed (override the tier) if they're non-essential.";
            sb.AppendLine("UNMATCHED CORE: " + string.Join(", ", unmatchedCore));
            log = sb.ToString();
            return null;
        }

        log = sb.ToString();
        return remap;
    }

    // Vanilla-rig bones a Mixamo/auto-rig source legitimately won't have — not a blocker if unmatched.
    static bool IsOptionalBone(string n)
    {
        if (string.IsNullOrEmpty(n)) return true;
        return n.StartsWith("Leaf", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Weapon_", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Dummy_Root", StringComparison.OrdinalIgnoreCase);
    }

    // "mixamorig:Hips" -> "Hips"; "Armature|Hips" -> "Hips"; trims whitespace.
    static string StripName(string n)
    {
        if (string.IsNullOrEmpty(n)) return n ?? "";
        int c = n.LastIndexOfAny(new[] { ':', '|', '/' });
        return (c >= 0 ? n.Substring(c + 1) : n).Trim();
    }

    static int ToInt(object o, int fallback) { try { return o == null ? fallback : Convert.ToInt32(o); } catch { return fallback; } }

    static object GetMember(object obj, string name)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        var p = t.GetProperty(name, ALL);
        if (p != null && p.CanRead) return p.GetValue(obj);
        var f = t.GetField(name, ALL);
        return f?.GetValue(obj);
    }

    // Walks the first SkinnedMeshRenderer found in the prefab's hierarchy.
    internal static List<BoneInfo> Extract(GameObject rigPrefab)
    {
        var infos = new List<BoneInfo>();
        var smr = rigPrefab.GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr == null || smr.bones == null || smr.bones.Length == 0) return infos;

        var bones = smr.bones;
        var bindposes = smr.sharedMesh != null ? smr.sharedMesh.bindposes : null;
        var indexOf = new Dictionary<Transform, int>();
        for (int i = 0; i < bones.Length; i++) if (bones[i] != null) indexOf[bones[i]] = i;

        for (int i = 0; i < bones.Length; i++)
        {
            var b = bones[i];
            int parentIndex = (b != null && b.parent != null && indexOf.TryGetValue(b.parent, out var pi)) ? pi : -1;
            int depth = 0; for (var p = b?.parent; p != null && indexOf.ContainsKey(p); p = p.parent) depth++;
            Vector3 bindTrans = (bindposes != null && i < bindposes.Length) ? bindposes[i].inverse.GetColumn(3) : (b != null ? b.position : Vector3.zero);
            infos.Add(new BoneInfo { Name = b != null ? b.name : $"(null bone {i})", ParentIndex = parentIndex, Depth = depth, BindPoseTranslation = bindTrans });
        }
        return infos;
    }

    /// <summary>
    /// Matches source bones to target bones structurally (depth bucket + nearest bind-pose
    /// translation, then verifies the matched parent chain actually corresponds). Returns a
    /// source-index -> target-bone-name remap on success, or null with a diagnostic on mismatch.
    /// </summary>
    internal static string[] MatchStructurally(List<BoneInfo> source, List<BoneInfo> target, out string mismatchReason)
    {
        mismatchReason = null;

        if (source.Count == 0 || target.Count == 0)
        { mismatchReason = "one of the rigs has no SkinnedMeshRenderer/bones to compare."; return null; }

        if (source.Count != target.Count)
        { mismatchReason = $"source rig has {source.Count} bones, target has {target.Count} — different bone count."; return null; }

        var srcByDepth = source.Select((b, i) => (b, i)).GroupBy(x => x.b.Depth).ToDictionary(g => g.Key, g => g.Select(x => x.i).ToList());
        var tgtByDepth = target.Select((b, i) => (b, i)).GroupBy(x => x.b.Depth).ToDictionary(g => g.Key, g => g.Select(x => x.i).ToList());
        if (srcByDepth.Count != tgtByDepth.Count || srcByDepth.Any(kv => !tgtByDepth.TryGetValue(kv.Key, out var l) || l.Count != kv.Value.Count))
        { mismatchReason = "bone depth distribution differs between source and target — different hierarchy shape."; return null; }

        var sourceMatch = new int[source.Count];   // source index -> target index
        for (int i = 0; i < sourceMatch.Length; i++) sourceMatch[i] = -1;
        var targetTaken = new bool[target.Count];

        // Pass 1: greedy nearest bind-pose-translation pairing within each depth bucket, shallowest first.
        foreach (var depth in srcByDepth.Keys.OrderBy(d => d))
        {
            foreach (var si in srcByDepth[depth])
            {
                int best = -1; float bestDist = float.MaxValue;
                foreach (var ti in tgtByDepth[depth])
                {
                    if (targetTaken[ti]) continue;
                    float d = Vector3.Distance(source[si].BindPoseTranslation, target[ti].BindPoseTranslation);
                    if (d < bestDist) { bestDist = d; best = ti; }
                }
                if (best < 0)
                { mismatchReason = $"no free target bone left at depth {depth} for source bone '{source[si].Name}'."; return null; }
                sourceMatch[si] = best;
                targetTaken[best] = true;
            }
        }

        // Pass 2: verify the matched parent chain actually corresponds (the real topology check —
        // same depth-histogram alone doesn't guarantee the same tree shape).
        for (int si = 0; si < source.Count; si++)
        {
            int ti = sourceMatch[si];
            int srcParent = source[si].ParentIndex;
            int tgtParent = target[ti].ParentIndex;
            if (srcParent < 0 || tgtParent < 0)
            {
                if (srcParent >= 0 || tgtParent >= 0)
                { mismatchReason = $"bone '{source[si].Name}' is a root in one rig but not the other."; return null; }
                continue;
            }
            if (sourceMatch[srcParent] != tgtParent)
            {
                mismatchReason = $"structural match failed at bone '{source[si].Name}' — its parent doesn't correspond to " +
                                  $"'{target[ti].Name}'.parent ('{target[tgtParent].Name}'). Different hierarchy shape.";
                return null;
            }
        }

        var remap = new string[source.Count];
        for (int si = 0; si < source.Count; si++) remap[si] = target[sourceMatch[si]].Name;
        return remap;
    }

    /// <summary>
    /// Instantiates the source prefab (does not touch the asset on disk) and renames its
    /// SkinnedMeshRenderer.bones[] Transforms to the matched target names. The mesh's boneWeights
    /// index into bones[] by position, which renaming doesn't change, so the mesh stays correct
    /// while Skeleton.Reimport (which reads gameObject.name) bakes with the target's names.
    /// </summary>
    internal static GameObject RenameBonesOnInstance(GameObject sourcePrefab, string[] remapSourceIndexToTargetName)
    {
        var instance = UnityEngine.Object.Instantiate(sourcePrefab);
        instance.name = sourcePrefab.name;
        var smr = instance.GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr == null || smr.bones == null) return instance;
        for (int i = 0; i < smr.bones.Length && i < remapSourceIndexToTargetName.Length; i++)
            if (smr.bones[i] != null) smr.bones[i].gameObject.name = remapSourceIndexToTargetName[i];
        return instance;
    }
}
