using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Fragment authoring + GUID-fix helpers, used by the Unit Visual Workflow wizard
/// (Tools/Unit Visual Workflow). Creates a PresentationPawnFragment (Mesh or SkinnedMesh)
/// and — the part that silently fails if skipped — populates the Amplitude.Framework.Guid
/// on its prefab/material references so they resolve at runtime.
///
/// AssetReference&lt;T&gt;.guid is Amplitude.Framework.Guid, NOT Unity's GUID, and it does
/// not auto-populate from the inspector picker. Assigning a prefab in the inspector looks
/// wired but resolves to null in-game unless this guid is filled. These helpers fill it.
/// </summary>
internal static class FragmentGuidFix
{
    const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // ── Create a fragment ScriptableObject ────────────────────────────────────
    internal static UnityEngine.Object CreateFragment(GameObject prefab, int kind, string createFolder)
    {
        string typeName = kind == 0
            ? "Amplitude.Mercury.Data.World.PresentationPawnFragmentSkinnedMesh"
            : "Amplitude.Mercury.Data.World.PresentationPawnFragmentMesh";
        var t = FindType(typeName);
        if (t == null) { Debug.LogError($"[Frag] type not found: {typeName} (check the namespace in your decompile)."); return null; }

        EnsureFolder(createFolder);
        var so = ScriptableObject.CreateInstance(t);
        so.name = prefab.name + "_Fragment";
        string path = AssetDatabase.GenerateUniqueAssetPath($"{createFolder}/{so.name}.asset");
        AssetDatabase.CreateAsset(so, path);
        AssetDatabase.SaveAssets();
        var fragment = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
        Debug.Log($"[Frag] created {t.Name} at {path}.");
        return fragment;
    }

    // ── Inspect: show the reference fields (field-name confirmation, diagnostic) ──
    internal static string Inspect(UnityEngine.Object fragment)
    {
        var sb = new StringBuilder();
        var t = fragment.GetType();
        sb.AppendLine($"=== {fragment.name} ({t.FullName}) ===");
        foreach (var f in t.GetFields(ALL))
        {
            object v = null; try { v = f.GetValue(fragment); } catch { }
            string note = "";
            string fn = f.Name.ToLowerInvariant();
            if (fn.Contains("prefab") || fn.Contains("model")) note = "  <-- MODEL ref?";
            else if (fn.Contains("material")) note = "  <-- MATERIAL ref?";
            sb.AppendLine($"  {f.Name} : {f.FieldType.Name}{note}");
            // if this looks like an AssetReference, show its inner guid field
            if (v != null && (f.FieldType.Name.Contains("Reference") || fn.Contains("prefab") || fn.Contains("model")))
            {
                var gf = FindGuidField(v.GetType());
                if (gf != null) sb.AppendLine($"        inner guid field: {gf.DeclaringType.Name}.{gf.Name} ({gf.FieldType.FullName}) = {gf.GetValue(v)}");
            }
        }
        var result = sb.ToString();
        Debug.Log(result);
        return result;
    }

    // ── Assign prefab/material and fill Amplitude GUIDs ───────────────────────
    internal static bool AssignAndFix(UnityEngine.Object fragment, GameObject prefab, string skinnedMeshPath, Material material)
    {
        if (fragment == null || prefab == null) { Debug.LogWarning("[Frag] need a fragment and a prefab."); return false; }
        Undo.RecordObject(fragment, "Assign fragment refs");
        var t = fragment.GetType();
        bool ok = true;

        // model reference: field named ModelPrefab / Prefab, type is an AssetReference wrapper
        var modelField = t.GetField("Prefab", ALL) ?? t.GetField("ModelPrefab", ALL)
                       ?? t.GetFields(ALL).FirstOrDefault(f => f.Name.ToLowerInvariant().Contains("prefab"));
        if (modelField == null) { Debug.LogError("[Frag] no model/prefab field found — run Inspect and tell me the field name."); return false; }
        if (!SetAssetReference(fragment, modelField, prefab))
        { Debug.LogError($"[Frag] failed to set model on '{modelField.Name}'."); ok = false; }
        else
            Debug.Log($"[Frag] model '{modelField.Name}' <- {prefab.name}");

        // SkinnedMeshPath: transform path to the renderer inside the prefab
        var smpField = t.GetField("SkinnedMeshPath", ALL);
        if (smpField != null && smpField.FieldType == typeof(string))
        {
            smpField.SetValue(fragment, skinnedMeshPath ?? "");
            Debug.Log($"[Frag] SkinnedMeshPath <- \"{skinnedMeshPath}\"");
        }

        // material reference: usually a raw Amplitude.Framework.Guid field named MaterialRef
        if (material != null)
        {
            var matField = t.GetField("MaterialRef", ALL)
                         ?? t.GetFields(ALL).FirstOrDefault(f =>
                               f.Name.ToLowerInvariant().Contains("material")
                               && !f.Name.ToLowerInvariant().StartsWith("runtime")   // skip runtimeMaterial cache
                               && f.FieldType.FullName == "Amplitude.Framework.Guid");
            if (matField == null) Debug.LogWarning("[Frag] no material field found; skipping (model-only test is fine).");
            else if (!SetMaterial(fragment, matField, material))
            { Debug.LogError($"[Frag] failed to set material on '{matField.Name}'."); ok = false; }
            else
                Debug.Log($"[Frag] material '{matField.Name}' <- {material.name}");
        }

        EditorUtility.SetDirty(fragment);
        AssetDatabase.SaveAssets();
        Verify(fragment, out bool guidsOk);
        return ok && guidsOk;
    }

    // Sets an AssetReference-wrapped reference: its inner Amplitude.Framework.Guid + any cached object.
    internal static bool SetAssetReference(UnityEngine.Object owner, FieldInfo refField, UnityEngine.Object target)
    {
        object refObj = refField.GetValue(owner);
        if (refObj == null)
        {
            // try to instantiate the reference wrapper
            try { refObj = Activator.CreateInstance(refField.FieldType); }
            catch { Debug.LogError($"[Frag] couldn't create {refField.FieldType.Name}; is it a struct/abstract?"); return false; }
        }

        var guidField = FindGuidField(refObj.GetType());
        if (guidField == null) { Debug.LogError($"[Frag] no Amplitude Guid field on {refObj.GetType().Name}."); return false; }
        object amp = GetAmplitudeGuid(target);
        if (amp == null) return false;
        guidField.SetValue(refObj, amp);

        // best-effort: set a cached UnityEngine.Object field so the inspector shows it
        var objField = refObj.GetType().GetFields(ALL)
            .FirstOrDefault(f => typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType));
        if (objField != null) { try { objField.SetValue(refObj, target); } catch { } }

        refField.SetValue(owner, refObj);   // write back (covers value-type wrappers)
        return true;
    }

    // Material is typically a raw Amplitude.Framework.Guid field (FakeAssetReference).
    internal static bool SetMaterial(UnityEngine.Object owner, FieldInfo matField, Material mat)
    {
        object amp = GetAmplitudeGuid(mat);
        if (amp == null) return false;
        if (matField.FieldType.FullName == "Amplitude.Framework.Guid")
        { matField.SetValue(owner, amp); return true; }
        // else it's an AssetReference wrapper around Material
        return SetAssetReference(owner, matField, mat);
    }

    // Write a pasted vanilla material GUID (32-hex from the explorer) into MaterialRef.
    // Vanilla materials are bundle-locked (not pickable), but their GUID is copyable,
    // and Amplitude.Framework.Guid has a ctor(string) that parses the 32-hex form.
    internal static bool SetMaterialRefFromGuidHex(UnityEngine.Object fragment, string hex)
    {
        hex = (hex ?? "").Trim();
        var gType = FindType("Amplitude.Framework.Guid");
        var ctor = gType?.GetConstructor(new[] { typeof(string) });
        if (ctor == null) { Debug.LogError("[Frag] Guid(string) ctor not found."); return false; }

        object amp;
        try { amp = ctor.Invoke(new object[] { hex }); }
        catch (Exception e) { Debug.LogError($"[Frag] '{hex}' is not a valid 32-hex GUID: {e.Message}"); return false; }
        if (amp == null || IsZeroGuid(amp)) { Debug.LogError("[Frag] parsed GUID is empty/zero."); return false; }

        var t = fragment.GetType();
        var matField = t.GetField("MaterialRef", ALL);
        if (matField == null) { Debug.LogError("[Frag] no MaterialRef field on the fragment."); return false; }

        Undo.RecordObject(fragment, "Set MaterialRef GUID");
        if (matField.FieldType.FullName == "Amplitude.Framework.Guid")
            matField.SetValue(fragment, amp);
        else  // wrapped AssetReference<Material>
        {
            var refObj = matField.GetValue(fragment) ?? Activator.CreateInstance(matField.FieldType);
            var gf = FindGuidField(refObj.GetType());
            if (gf == null) { Debug.LogError("[Frag] MaterialRef wrapper has no Guid field."); return false; }
            gf.SetValue(refObj, amp);
            matField.SetValue(fragment, refObj);
        }
        EditorUtility.SetDirty(fragment);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Frag] MaterialRef <- vanilla GUID {hex}. (Its output layer / shader / proxies are already registered in-game.)");
        Verify(fragment, out _);
        return true;
    }

    // ── The crux: derive the Amplitude.Framework.Guid for a Unity asset ───────
    internal static object GetAmplitudeGuid(UnityEngine.Object asset)
    {
        // Preferred: the editor utility the inspector drawer itself uses.
        var au = FindType("Amplitude.Framework.Editor.Asset.AssetUtility");
        var m = au?.GetMethod("GetAssetGuid", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                              null, new[] { typeof(UnityEngine.Object) }, null);
        if (m != null)
        {
            try
            {
                var g = m.Invoke(null, new object[] { asset });
                if (g != null && !IsZeroGuid(g)) return g;
                Debug.LogWarning($"[Frag] AssetUtility.GetAssetGuid returned empty for '{asset.name}'.");
            }
            catch (Exception e) { Debug.LogWarning($"[Frag] GetAssetGuid threw ({e.Message})."); }
        }
        else Debug.LogWarning("[Frag] AssetUtility.GetAssetGuid not found — is the Amplitude editor assembly loaded?");

        // Fallback (last resort): path-based method, then Unity-GUID string into the Guid(string) ctor.
        string path = AssetDatabase.GetAssetPath(asset);
        var adb = FindType("Amplitude.Framework.Asset.AssetDatabase");
        var pm = adb?.GetMethod("GetGuidFromAssetPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                               null, new[] { typeof(string) }, null);
        if (pm != null)
        {
            try { var g = pm.Invoke(null, new object[] { path }); if (g != null && !IsZeroGuid(g)) return g; } catch { }
        }
        // The Guid(string) ctor parses a 32-hex string — feed Unity's GUID hex as a last resort.
        var gType = FindType("Amplitude.Framework.Guid");
        var ctor = gType?.GetConstructor(new[] { typeof(string) });
        if (ctor != null)
        {
            Debug.LogWarning("[Frag] using Unity-GUID-string fallback via Guid(string) ctor — verify it resolves in-game.");
            try { return ctor.Invoke(new object[] { AssetDatabase.AssetPathToGUID(path) }); } catch { }
        }
        Debug.LogError($"[Frag] could not derive an Amplitude GUID for '{asset.name}'.");
        return null;
    }

    // ── Verify ────────────────────────────────────────────────────────────────
    internal static List<string> Verify(UnityEngine.Object fragment, out bool ok)
    {
        var t = fragment.GetType();
        var lines = new List<string>();
        ok = true;
        foreach (var f in t.GetFields(ALL))
        {
            string fn = f.Name.ToLowerInvariant();
            if (fn.StartsWith("runtime")) continue;                       // skip runtime caches
            bool isModel = fn.Contains("prefab") || fn.Contains("model");
            bool isMat   = f.FieldType.FullName == "Amplitude.Framework.Guid" && fn.Contains("material");
            if (!isModel && !isMat) continue;
            object v = f.GetValue(fragment);
            object guid = (v != null && f.FieldType.FullName == "Amplitude.Framework.Guid") ? v
                        : (v != null ? FindGuidField(v.GetType())?.GetValue(v) : null);
            bool zero = guid == null || IsZeroGuid(guid);
            lines.Add($"{f.Name}: guid {(zero ? "EMPTY" : "set")}");
            if (zero) ok = false;
        }
        var sb = new StringBuilder($"=== Verify {fragment.name} ===\n");
        foreach (var l in lines) sb.AppendLine("  " + l);
        sb.AppendLine(ok ? "All reference GUIDs populated." : "Some GUIDs are EMPTY — these will load as null in-game.");
        Debug.Log(sb.ToString());
        return lines;
    }

    // ── helpers ───────────────────────────────────────────────────────────────
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
        var t = guid.GetType();
        bool any = false;
        foreach (var n in new[] { "a", "b", "c", "d" })
        {
            var f = t.GetField(n, ALL);
            if (f != null) { any = true; if (Convert.ToInt64(f.GetValue(guid)) != 0) return false; }
        }
        return any;   // all four were zero (or none found -> treat as zero/unknown)
    }

    static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        { var t = asm.GetType(fullName); if (t != null) return t; }
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
