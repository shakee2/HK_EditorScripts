using System;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Authoring tool for custom unit/district visuals. Creates a PresentationPawnFragment
/// (Mesh or SkinnedMesh) and — the part that silently fails if skipped — populates the
/// Amplitude.Framework.Guid on its prefab/material references so they resolve at runtime.
///
/// AssetReference&lt;T&gt;.guid is Amplitude.Framework.Guid, NOT Unity's GUID, and it does
/// not auto-populate from the inspector picker. Assigning a prefab in the inspector looks
/// wired but resolves to null in-game unless this guid is filled. This tool fills it.
///
/// Workflow:
///   1. Inspect — confirm the fragment's reference field names before writing anything.
///   2. Assign &amp; Fix GUIDs — set prefab (and optional material) + their Amplitude GUIDs.
///   3. Verify — check every reference GUID is non-zero and points at a real asset.
/// Then assign the fragment onto a PresentationPawnDefinition via the normal modtool
/// inspector picker (it shows your fragment because it now exists in the project), bundle, test.
/// </summary>
public class PawnFragmentAuthor : EditorWindow
{
    const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    UnityEngine.Object _fragment;   // existing PresentationPawnFragment asset (optional)
    GameObject _prefab;
    Material   _material;
    string _skinnedMeshPath = "";   // transform path to the SkinnedMeshRenderer inside the prefab
    string _materialGuidHex = "";   // pasted vanilla material GUID (32-hex) to reuse
    int _kind;                      // 0 = SkinnedMesh, 1 = Mesh
    string _createFolder = "Assets/Resources/New Additions/Fragments";   // must be under an INCLUDED bundle root (Resources/ or Databases/), not bare Assets/

    [MenuItem("Tools/Pawn Fragment/Author Window", false, 5)]
    static void Open()
    {
        var w = GetWindow<PawnFragmentAuthor>("Pawn Fragment");
        w.minSize = new Vector2(380, 280);
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Fragment authoring + GUID fix", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Run Inspect first to confirm field names. The GUID fix is what makes references resolve in-game.", MessageType.Info);

        _fragment = EditorGUILayout.ObjectField("Fragment (optional)", _fragment, typeof(UnityEngine.Object), false);
        _prefab   = (GameObject)EditorGUILayout.ObjectField("Model Prefab", _prefab, typeof(GameObject), false);
        _material = (Material)EditorGUILayout.ObjectField("Material (optional)", _material, typeof(Material), false);
        _skinnedMeshPath = EditorGUILayout.TextField(new GUIContent("SkinnedMesh Path", "Transform/mesh name inside the prefab, e.g. 'Cube' or 'Body'"), _skinnedMeshPath);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Reuse a vanilla material by GUID (copy from the explorer):", EditorStyles.miniBoldLabel);
        _materialGuidHex = EditorGUILayout.TextField("MaterialRef GUID", _materialGuidHex);
        using (new EditorGUI.DisabledScope(_fragment == null || string.IsNullOrEmpty(_materialGuidHex)))
            if (GUILayout.Button("Set MaterialRef from GUID")) SetMaterialRefFromGuidHex();

        EditorGUILayout.Space(6);
        if (_fragment == null)
        {
            EditorGUILayout.LabelField("Create new fragment:", EditorStyles.miniBoldLabel);
            _kind = EditorGUILayout.Popup("Kind", _kind, new[] { "SkinnedMesh", "Mesh" });
            _createFolder = EditorGUILayout.TextField("Folder", _createFolder);
            if (GUILayout.Button("Create Fragment Asset") && _prefab != null) CreateFragment();
        }

        EditorGUILayout.Space(8);
        using (new EditorGUI.DisabledScope(_fragment == null))
        {
            if (GUILayout.Button("Inspect Fragment")) Inspect();
            if (GUILayout.Button("Assign & Fix GUIDs")) AssignAndFix();
            if (GUILayout.Button("Verify")) Verify();
        }

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Probe: find GUID conversion")) ProbeGuidConversion();
    }

    // Discover how Amplitude converts a Unity asset/GUID -> Amplitude.Framework.Guid,
    // since GetGuidFromAssetPath doesn't exist. Lists Guid ctors/factories and any
    // method anywhere that returns an Amplitude.Framework.Guid from a string/asset.
    void ProbeGuidConversion()
    {
        var gType = FindType("Amplitude.Framework.Guid");
        var sb = new StringBuilder();
        sb.AppendLine("=== Amplitude.Framework.Guid conversion probe ===");
        if (gType == null) { sb.AppendLine("Guid type NOT FOUND."); Debug.Log(sb.ToString()); return; }

        sb.AppendLine($"\n-- constructors of {gType.FullName} --");
        foreach (var c in gType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            sb.AppendLine("  ctor(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");

        sb.AppendLine("\n-- static methods on Guid (factories: Parse/From/Create...) --");
        foreach (var m in gType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            sb.AppendLine($"  {m.ReturnType.Name} {m.Name}(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");

        sb.AppendLine("\n-- any static method ANYWHERE returning Amplitude.Framework.Guid --");
        int found = 0;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types; try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types)
            {
                MethodInfo[] ms; try { ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly); } catch { continue; }
                foreach (var m in ms)
                {
                    if (m.ReturnType != gType) continue;
                    var ps = m.GetParameters();
                    // interested in ones taking a string (path/hex) or a Unity object
                    if (ps.Any(p => p.ParameterType == typeof(string) || typeof(UnityEngine.Object).IsAssignableFrom(p.ParameterType)
                                 || p.ParameterType.Name.Contains("GUID")))
                    {
                        sb.AppendLine($"  {t.FullName}.{m.Name}(" + string.Join(", ", ps.Select(p => p.ParameterType.Name)) + ")");
                        if (++found > 40) { sb.AppendLine("  ...(truncated)"); goto done; }
                    }
                }
            }
        }
        done:
        if (found == 0) sb.AppendLine("  none found — conversion may be a ctor above, or via implicit operator / UnityEngine.GUID reinterpret.");
        Debug.Log(sb.ToString());
    }

    // ── Create a fragment ScriptableObject ────────────────────────────────────
    void CreateFragment()
    {
        string typeName = _kind == 0
            ? "Amplitude.Mercury.Data.World.PresentationPawnFragmentSkinnedMesh"
            : "Amplitude.Mercury.Data.World.PresentationPawnFragmentMesh";
        var t = FindType(typeName);
        if (t == null) { Debug.LogError($"[Frag] type not found: {typeName} (check the namespace in your decompile)."); return; }

        EnsureFolder(_createFolder);
        var so = ScriptableObject.CreateInstance(t);
        so.name = _prefab.name + "_Fragment";
        string path = AssetDatabase.GenerateUniqueAssetPath($"{_createFolder}/{so.name}.asset");
        AssetDatabase.CreateAsset(so, path);
        AssetDatabase.SaveAssets();
        _fragment = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
        Debug.Log($"[Frag] created {t.Name} at {path}. Now 'Assign & Fix GUIDs'.");
    }

    // ── Inspect: show the reference fields so we confirm names before writing ──
    void Inspect()
    {
        var sb = new StringBuilder();
        var t = _fragment.GetType();
        sb.AppendLine($"=== {_fragment.name} ({t.FullName}) ===");
        foreach (var f in t.GetFields(ALL))
        {
            object v = null; try { v = f.GetValue(_fragment); } catch { }
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
        Debug.Log(sb.ToString());
    }

    // ── Assign prefab/material and fill Amplitude GUIDs ───────────────────────
    void AssignAndFix()
    {
        if (_fragment == null || _prefab == null) { Debug.LogWarning("[Frag] need a fragment and a prefab."); return; }
        Undo.RecordObject(_fragment, "Assign fragment refs");
        var t = _fragment.GetType();

        // model reference: field named ModelPrefab / Prefab, type is an AssetReference wrapper
        var modelField = t.GetField("Prefab", ALL) ?? t.GetField("ModelPrefab", ALL)
                       ?? t.GetFields(ALL).FirstOrDefault(f => f.Name.ToLowerInvariant().Contains("prefab"));
        if (modelField == null) { Debug.LogError("[Frag] no model/prefab field found — run Inspect and tell me the field name."); return; }
        if (!SetAssetReference(_fragment, modelField, _prefab))
            Debug.LogError($"[Frag] failed to set model on '{modelField.Name}'.");
        else
            Debug.Log($"[Frag] model '{modelField.Name}' <- {_prefab.name}");

        // SkinnedMeshPath: transform path to the renderer inside the prefab
        var smpField = t.GetField("SkinnedMeshPath", ALL);
        if (smpField != null && smpField.FieldType == typeof(string))
        {
            smpField.SetValue(_fragment, _skinnedMeshPath ?? "");
            Debug.Log($"[Frag] SkinnedMeshPath <- \"{_skinnedMeshPath}\"");
        }

        // material reference: usually a raw Amplitude.Framework.Guid field named MaterialRef
        if (_material != null)
        {
            var matField = t.GetField("MaterialRef", ALL)
                         ?? t.GetFields(ALL).FirstOrDefault(f =>
                               f.Name.ToLowerInvariant().Contains("material")
                               && !f.Name.ToLowerInvariant().StartsWith("runtime")   // skip runtimeMaterial cache
                               && f.FieldType.FullName == "Amplitude.Framework.Guid");
            if (matField == null) Debug.LogWarning("[Frag] no material field found; skipping (model-only test is fine).");
            else if (!SetMaterial(_fragment, matField, _material))
                Debug.LogError($"[Frag] failed to set material on '{matField.Name}'.");
            else
                Debug.Log($"[Frag] material '{matField.Name}' <- {_material.name}");
        }

        EditorUtility.SetDirty(_fragment);
        AssetDatabase.SaveAssets();
        Verify();
    }

    // Sets an AssetReference-wrapped reference: its inner Amplitude.Framework.Guid + any cached object.
    bool SetAssetReference(UnityEngine.Object owner, FieldInfo refField, UnityEngine.Object target)
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
    bool SetMaterial(UnityEngine.Object owner, FieldInfo matField, Material mat)
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
    void SetMaterialRefFromGuidHex()
    {
        var hex = (_materialGuidHex ?? "").Trim();
        var gType = FindType("Amplitude.Framework.Guid");
        var ctor = gType?.GetConstructor(new[] { typeof(string) });
        if (ctor == null) { Debug.LogError("[Frag] Guid(string) ctor not found."); return; }

        object amp;
        try { amp = ctor.Invoke(new object[] { hex }); }
        catch (Exception e) { Debug.LogError($"[Frag] '{hex}' is not a valid 32-hex GUID: {e.Message}"); return; }
        if (amp == null || IsZeroGuid(amp)) { Debug.LogError("[Frag] parsed GUID is empty/zero."); return; }

        var t = _fragment.GetType();
        var matField = t.GetField("MaterialRef", ALL);
        if (matField == null) { Debug.LogError("[Frag] no MaterialRef field on the fragment."); return; }

        Undo.RecordObject(_fragment, "Set MaterialRef GUID");
        if (matField.FieldType.FullName == "Amplitude.Framework.Guid")
            matField.SetValue(_fragment, amp);
        else  // wrapped AssetReference<Material>
        {
            var refObj = matField.GetValue(_fragment) ?? Activator.CreateInstance(matField.FieldType);
            var gf = FindGuidField(refObj.GetType());
            if (gf == null) { Debug.LogError("[Frag] MaterialRef wrapper has no Guid field."); return; }
            gf.SetValue(refObj, amp);
            matField.SetValue(_fragment, refObj);
        }
        EditorUtility.SetDirty(_fragment);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Frag] MaterialRef <- vanilla GUID {hex}. (Its output layer / shader / proxies are already registered in-game.)");
        Verify();
    }

    // ── The crux: derive the Amplitude.Framework.Guid for a Unity asset ───────
    object GetAmplitudeGuid(UnityEngine.Object asset)
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

    static object BuildAmpGuidFromUnity(string unityGuidHex)
    {
        var gType = FindType("Amplitude.Framework.Guid");
        if (gType == null || unityGuidHex == null || unityGuidHex.Length != 32)
        { Debug.LogError("[Frag] cannot build Amplitude Guid (type missing or bad Unity GUID)."); return null; }

        // 32 hex -> 16 bytes -> 4 int32 (a,b,c,d). Order is a best guess; verify in-game.
        var bytes = new byte[16];
        for (int i = 0; i < 16; i++) bytes[i] = Convert.ToByte(unityGuidHex.Substring(i * 2, 2), 16);
        int a = BitConverter.ToInt32(bytes, 0), b = BitConverter.ToInt32(bytes, 4),
            c = BitConverter.ToInt32(bytes, 8), d = BitConverter.ToInt32(bytes, 12);

        // try a 4-int constructor, else set fields a/b/c/d
        var ctor = gType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
        if (ctor != null) return ctor.Invoke(new object[] { a, b, c, d });
        object g = Activator.CreateInstance(gType);
        gType.GetField("a", ALL)?.SetValue(g, a); gType.GetField("b", ALL)?.SetValue(g, b);
        gType.GetField("c", ALL)?.SetValue(g, c); gType.GetField("d", ALL)?.SetValue(g, d);
        Debug.LogWarning("[Frag] used Unity-GUID fallback with guessed int packing — if the model doesn't load, the packing order is the suspect.");
        return g;
    }

    // ── Verify ────────────────────────────────────────────────────────────────
    void Verify()
    {
        var t = _fragment.GetType();
        var sb = new StringBuilder($"=== Verify {_fragment.name} ===\n");
        bool ok = true;
        foreach (var f in t.GetFields(ALL))
        {
            string fn = f.Name.ToLowerInvariant();
            if (fn.StartsWith("runtime")) continue;                       // skip runtime caches
            bool isModel = fn.Contains("prefab") || fn.Contains("model");
            bool isMat   = f.FieldType.FullName == "Amplitude.Framework.Guid" && fn.Contains("material");
            if (!isModel && !isMat) continue;
            object v = f.GetValue(_fragment);
            object guid = (v != null && f.FieldType.FullName == "Amplitude.Framework.Guid") ? v
                        : (v != null ? FindGuidField(v.GetType())?.GetValue(v) : null);
            bool zero = guid == null || IsZeroGuid(guid);
            sb.AppendLine($"  {f.Name}: guid {(zero ? "EMPTY ✗" : "set ✓")}");
            if (zero) ok = false;
        }
        sb.AppendLine(ok ? "All reference GUIDs populated." : "Some GUIDs are EMPTY — these will load as null in-game.");
        Debug.Log(sb.ToString());
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