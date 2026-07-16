using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// FBX-prep pipeline for the Unit Visual Workflow wizard's "Choose model" step — generalizes the
/// proven recipe in Docs/ENCAccessProof-master/baker/ZeppelinModel.cs (and the "why" write-up in
/// docs/FBX-to-Humankind-Pipeline.md §4) from that one model's hardcoded hull_0/hull_1 material
/// names to an arbitrary number of materials: combine every submesh into one mesh, atlas each
/// distinct material's main texture into one Texture2D (remapping UVs into that material's atlas
/// rect), fix inconsistent winding, and normalize scale/orientation. Needed for real-world,
/// multi-submesh/multi-material FBX imports — a clean single-material test mesh can skip the
/// atlas step (buildAtlas: false) and still gets the winding/normalize treatment.
/// </summary>
internal static class FbxPrepPipeline
{
    internal class Result
    {
        public Mesh CombinedMesh;
        public Texture2D Atlas;       // null if buildAtlas was false
        public Material Material;     // null if buildAtlas was false
        public int FlippedTriangles;
    }

    internal static Result Run(GameObject source, float targetLength, Vector3 orientEuler, bool buildAtlas)
    {
        var inst = (GameObject)UnityEngine.Object.Instantiate(source);
        try
        {
            var rootInv = inst.transform.worldToLocalMatrix;
            var parts = new List<(Mesh mesh, int sub, Material mat, Matrix4x4 local)>();

            foreach (var mf in inst.GetComponentsInChildren<MeshFilter>())
            {
                var m = mf.sharedMesh; if (m == null) continue;
                var rend = mf.GetComponent<MeshRenderer>();
                var mats = rend != null ? rend.sharedMaterials : null;
                var local = rootInv * mf.transform.localToWorldMatrix;
                for (int s = 0; s < m.subMeshCount; s++)
                    parts.Add((m, s, mats != null && s < mats.Length ? mats[s] : null, local));
            }
            foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var m = smr.sharedMesh; if (m == null) continue;
                var mats = smr.sharedMaterials;
                var local = rootInv * smr.transform.localToWorldMatrix;
                for (int s = 0; s < m.subMeshCount; s++)
                    parts.Add((m, s, mats != null && s < mats.Length ? mats[s] : null, local));
            }
            if (parts.Count == 0) { Debug.LogError("[FbxPrep] no MeshFilter/SkinnedMeshRenderer submeshes found in the prefab."); return null; }

            Texture2D atlas = null;
            Rect[] rects = null;
            Material[] distinctMats = null;
            if (buildAtlas)
            {
                distinctMats = parts.Select(p => p.mat).Where(m => m != null).Distinct().ToArray();
                if (distinctMats.Length > 0)
                {
                    var srcTextures = distinctMats.Select(GetReadableTexture).ToArray();
                    atlas = new Texture2D(2048, 2048, TextureFormat.RGBA32, false) { name = source.name + "_Atlas" };
                    rects = atlas.PackTextures(srcTextures, 2, 2048, false);
                    CleanAtlas(atlas);
                }
            }

            var combine = new List<CombineInstance>();
            foreach (var p in parts)
            {
                var sub = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, vertices = p.mesh.vertices };
                if (p.mesh.normals != null && p.mesh.normals.Length == p.mesh.vertexCount) sub.normals = p.mesh.normals;
                var uv = p.mesh.uv;
                if (buildAtlas && p.mat != null && distinctMats != null && uv != null && uv.Length == p.mesh.vertexCount)
                {
                    int matIdx = Array.IndexOf(distinctMats, p.mat);
                    var r = matIdx >= 0 ? rects[matIdx] : new Rect(0, 0, 1, 1);
                    var newUv = new Vector2[uv.Length];
                    for (int i = 0; i < uv.Length; i++) newUv[i] = new Vector2(r.x + uv[i].x * r.width, r.y + uv[i].y * r.height);
                    sub.uv = newUv;
                }
                else if (uv != null && uv.Length == p.mesh.vertexCount) sub.uv = uv;
                sub.triangles = p.mesh.GetTriangles(p.sub);
                combine.Add(new CombineInstance { mesh = sub, transform = p.local });
            }

            var mesh = new Mesh { name = source.name + "_CombinedMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.CombineMeshes(combine.ToArray(), true, true);

            NormalizeScaleAndOrientation(mesh, targetLength, orientEuler);
            int flipped = FixWinding(mesh);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();

            Material material = null;
            if (buildAtlas && atlas != null)
                material = new Material(Shader.Find("Standard")) { name = source.name + "_Mat", mainTexture = atlas };

            Debug.Log($"[FbxPrep] combined {parts.Count} submesh(es), verts={mesh.vertexCount}, winding-fixed {flipped} tris" +
                      (atlas != null ? $", atlas={atlas.width}x{atlas.height}" : ""));

            return new Result { CombinedMesh = mesh, Atlas = atlas, Material = material, FlippedTriangles = flipped };
        }
        finally { UnityEngine.Object.DestroyImmediate(inst); }
    }

    // Re-reads a non-readable Texture2D via a RenderTexture blit (mirrors ZeppelinModel's PNG
    // round-trip, generalized to whatever's already on the material rather than fixed file paths).
    static Texture2D GetReadableTexture(Material mat)
    {
        var tex = mat.mainTexture as Texture2D;
        if (tex == null) return Texture2D.whiteTexture;
        if (tex.isReadable) return tex;
        try
        {
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            readable.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return readable;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FbxPrep] couldn't read texture on material '{mat.name}': {e.Message}");
            return Texture2D.whiteTexture;
        }
    }

    // Force opaque (AlbedoTransparency alpha can read as cutout) and repaint near-black pixels —
    // some source textures have a UV dead-zone that renders as a black patch once atlased.
    static void CleanAtlas(Texture2D atlas)
    {
        var px = atlas.GetPixels32();
        for (int i = 0; i < px.Length; i++)
        {
            px[i].a = 255;
            if (px[i].r < 32 && px[i].g < 32 && px[i].b < 32) { px[i].r = 160; px[i].g = 160; px[i].b = 168; }
        }
        atlas.SetPixels32(px);
        atlas.Apply();
    }

    // Recenter, uniform-scale the longest axis to targetLength, auto-align the longest axis to Y,
    // then apply a configurable Euler tweak — third-party models have arbitrary scale/orientation.
    static void NormalizeScaleAndOrientation(Mesh mesh, float targetLength, Vector3 orientEuler)
    {
        mesh.RecalculateBounds();
        var bb = mesh.bounds; var size = bb.size;
        float longest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        float scl = (longest > 0f && targetLength > 0f) ? targetLength / longest : 1f;
        Quaternion align = (size.x >= size.y && size.x >= size.z) ? Quaternion.FromToRotation(Vector3.right, Vector3.up)
                         : (size.z >= size.x && size.z >= size.y) ? Quaternion.FromToRotation(Vector3.forward, Vector3.up)
                         : Quaternion.identity;
        Quaternion rot = Quaternion.Euler(orientEuler) * align;
        var vv = mesh.vertices; var nn = mesh.normals;
        for (int i = 0; i < vv.Length; i++) vv[i] = rot * ((vv[i] - bb.center) * scl);
        if (nn != null && nn.Length == vv.Length) for (int i = 0; i < nn.Length; i++) nn[i] = rot * nn[i];
        mesh.vertices = vv;
        if (nn != null && nn.Length == vv.Length) mesh.normals = nn;
    }

    // Backface culling uses winding, not normals. For each triangle, flip it if its geometric
    // normal points toward the mesh's centred origin — proven model-normal-independent (works even
    // when the source's authored normals are themselves unreliable).
    static int FixWinding(Mesh mesh)
    {
        var v = mesh.vertices; var t = mesh.triangles; int flipped = 0;
        for (int i = 0; i < t.Length; i += 3)
        {
            int a = t[i], b = t[i + 1], c = t[i + 2];
            Vector3 geo = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
            Vector3 outward = v[a] + v[b] + v[c];
            if (Vector3.Dot(geo, outward) < 0f) { t[i + 1] = c; t[i + 2] = b; flipped++; }
        }
        mesh.triangles = t;
        return flipped;
    }
}
