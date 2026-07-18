using System;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Investigates whether the presentation-pawn assets the modtools hide can be
/// reached, cloned, and mesh-swapped purely in the editor (no BepInEx).
///
/// Run the menu items top to bottom. Each one answers a single yes/no that
/// decides whether the full clone-and-swap tool is worth building:
///   1. Can we even find fragment / pawn assets and see their fields?
///   2. Which field on a fragment holds the mesh reference?
///   3. Can we duplicate one of these assets and write a changed field back?
/// </summary>
public static class PawnFragmentProbe
{
    const BindingFlags ALL =
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public   | BindingFlags.NonPublic;

    // ──────────────────────────────────────────────────────────────────────────
    // STEP 1 — Find presentation assets in the project and report their types.
    // Confirms the assets are loadable at all and shows the exact type names
    // (PresentationPawnFragmentSkinnedMesh, PresentationPawnDescription, etc.)
    // ──────────────────────────────────────────────────────────────────────────
    // Small explicit type so element names survive the LINQ chain.
    struct Hit { public string Type; public string Path; }

    [MenuItem("Tools/shakee's Tools/Debug/Pawn Probe/1. Find Presentation Assets", false, 120)]
    static void FindPresentationAssets()
    {
        var sb = new StringBuilder();
        var byType = AssetDatabase.FindAssets("")                       // every asset
            .Select(AssetDatabase.GUIDToAssetPath)
            .Distinct()
            .SelectMany(p =>
            {
                UnityEngine.Object[] objs;
                try { objs = AssetDatabase.LoadAllAssetsAtPath(p); } catch { return Enumerable.Empty<Hit>(); }
                return objs.Where(o => o != null)
                           .Select(o => new Hit { Type = o.GetType().Name, Path = p });
            })
            .Where(h => h.Type.IndexOf("PresentationPawn", StringComparison.OrdinalIgnoreCase) >= 0
                     || h.Type.IndexOf("Fragment", StringComparison.OrdinalIgnoreCase) >= 0)
            .GroupBy(h => h.Type)
            .OrderByDescending(g => g.Count())
            .ToList();

        sb.AppendLine("=== Presentation / Fragment asset types found ===");
        foreach (var g in byType)
        {
            sb.AppendLine($"\n[{g.Count()}]  {g.Key}");
            foreach (var h in g.Take(3)) sb.AppendLine($"        e.g. {h.Path}");
        }
        if (byType.Count == 0)
            sb.AppendLine("NONE found. The fragment assets likely aren't imported into this project " +
                          "(they live unextracted in bundles). Extract a unit/district bundle first, then re-run.");

        Debug.Log(sb.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // STEP 2 — Dump the selected fragment/pawn's fields so we can see which one
    // holds the mesh (and which hold the material/texture references we must NOT
    // touch). Select a fragment asset in the Project window, then run this.
    // ──────────────────────────────────────────────────────────────────────────
    [MenuItem("Tools/shakee's Tools/Debug/Dump Selected Element Fields", false, 121)]
    static void DumpFragmentFields()
    {
        var obj = Selection.activeObject;
        if (obj == null) { Debug.LogWarning("Select a fragment/pawn asset first."); return; }

        var sb = new StringBuilder();
        sb.AppendLine($"=== {obj.name}  ({obj.GetType().FullName}) ===\n");

        // Reflected C# fields (catches what the inspector hides)
        sb.AppendLine("-- reflected instance fields --");
        foreach (var f in obj.GetType().GetFields(ALL))
        {
            object v = null;
            try { v = f.GetValue(obj); } catch { v = "<unreadable>"; }
            string vt = v?.GetType().Name ?? "null";

            // Flag the ones we care about
            string tag = "";
            string fn = f.Name.ToLowerInvariant();
            if (fn.Contains("mesh"))       tag = "   <-- MESH?";
            else if (fn.Contains("material") || fn.Contains("texture")) tag = "   <-- material/texture (keep)";
            else if (fn.Contains("guid") || fn.Contains("reference"))   tag = "   <-- asset ref";
            sb.AppendLine($"  {f.Name} : {f.FieldType.Name}  =  ({vt}) {Trunc(v)}{tag}");
            if (f.Name == "Translations" || f.Name =="lineCollection")
            {
                System.Collections.IList values = (System.Collections.IList)v;
    
                if (values.Count > 0)
                {
                    object firstItem = values[0];
                    sb.AppendLine($"Translation #0 ({firstItem.GetType().Name}):");

                    // Reflect into the fields of this specific translation object
                    foreach (var subField in firstItem.GetType().GetFields(ALL))
                    {
                        object subValue = null;
                        try { subValue = subField.GetValue(firstItem); } catch { subValue = "<unreadable>"; }
                        
                        sb.AppendLine($"    -> {subField.Name} = {subValue}");
                    }
                }
                else
                {
                    sb.AppendLine($"Translation #0 = <empty list>");
                }
            }
            if (f.Name == "SimulationEventEffects")
            {
                sb.AppendLine($"\n-- SimulationEvents --");
                System.Collections.IList values = (System.Collections.IList)v;
                if (values.Count > 0)
                {
                    for (int i = 0; i < values.Count; i++)
                    {
                        object firstItem = values[i];
                        sb.AppendLine($"Event #{i} ({firstItem.GetType().Name}):");

                        // Reflect into the fields of this specific translation object
                        foreach (var subField in firstItem.GetType().GetFields(ALL))
                        {
                            object subValue = null;
                            try { subValue = subField.GetValue(firstItem); } catch { subValue = "<unreadable>"; }
                            
                            sb.AppendLine($"    -> {subField.Name} = {subValue}");
                        }
                    }
                    
                }
                else
                {
                    sb.AppendLine($"Event #0 = <empty list>");
                }

            }
            if (f.Name == "SettlementStabilityPrerequisite")
            {
                sb.AppendLine($"\n-- SettlementStabilityPrerequisite --");
                object v2 = v ?? f.GetValue(obj);
                if (v2 != null)
                {
                    foreach (var subField in v2.GetType().GetFields(ALL))
                    {
                        object subValue = null;
                        try { subValue = subField.GetValue(v2); } catch { subValue = "<unreadable>"; }

                        if (subField.FieldType.IsEnum)
                        {
                            sb.AppendLine($"    -> {subField.Name} : {subField.FieldType.Name} = {subValue}");
                            continue;
                        }

                        if (subValue is System.Collections.IList list)
                        {
                            sb.AppendLine($"    -> {subField.Name} : {subField.FieldType.Name} ({list.Count} items)");
                            if (list.Count == 0)
                                sb.AppendLine($"        [empty]");
                            else
                            {
                                for (int i = 0; i < list.Count; i++)
                                {
                                    object item = list[i];
                                    sb.AppendLine($"        [{i}] ({item?.GetType().Name ?? "null"}) {Trunc(item)}");
                                }
                            }
                            continue;
                        }

                        sb.AppendLine($"    -> {subField.Name} = {subValue}");
                    }
                }
                else
                {
                    sb.AppendLine($"SettlementStabilityPrerequisite = <null>");
                }
                sb.AppendLine($"\n-- SettlementStabilityPrerequisite END --");
            }
            if (f.Name == "Effects")
            {
                sb.AppendLine($"\n-- Effects --");
                System.Collections.IList values = (System.Collections.IList)v;
                if (values.Count > 0)
                {
                    for (int i = 0; i < values.Count; i++)
                    {
                        object firstItem = values[i];
                        sb.AppendLine($"Line #{i} ({firstItem.GetType().Name}):");

                        // Reflect into the fields of this specific translation object
                        foreach (var subField in firstItem.GetType().GetFields(ALL))
                        {                           
                            object subValue = null;
                            try { subValue = subField.GetValue(firstItem); } catch { subValue = "<unreadable>"; }
                            
                            sb.AppendLine($"    -> {subField.Name} = {subValue}");
                            if (subField.Name == "PropertyEffects" || subField.Name == "PropertyEffect")
                            {
                                object subFieldValue = null;
                                try { subFieldValue = subField.GetValue(firstItem); } catch { subFieldValue = "<unreadable>"; }
                                System.Collections.IList values2 = (System.Collections.IList)subFieldValue;
                                if (values2.Count > 0)
                                {
                                    for (int j = 0; j < values2.Count; j++)
                                    {
                                        object secondItem = values2[j];
                                        sb.AppendLine($"PropertyEffect #{j} ({secondItem.GetType().Name}):");

                                        // Reflect into the fields of this specific translation object
                                        foreach (var subField2 in secondItem.GetType().GetFields(ALL))
                                        {                           
                                            object subValue2 = null;
                                            try { subValue2 = subField2.GetValue(secondItem); } catch { subValue2 = "<unreadable>"; }
                                            
                                            sb.AppendLine($"        -> {subField2.Name} = {subValue2}");
                                        }
                                    }
                                }
                                else
                                {
                                    sb.AppendLine($"PropertyEffect #0 = <empty list>");
                                }
                            }
                        }
                    }
                }
                else
                {
                    sb.AppendLine($"Line #0 = <empty list>");
                }

            }
            /* foreach (var sub in f.FieldType.GetFields(ALL))
            {
                object v2 = null;
                try { v2 = sub.GetValue(v); } catch { v2 = "<unreadable>"; }
                string vt2 = sub?.GetType().Name ?? "null";
                sb.AppendLine($"  {sub.Name} : {sub.FieldType.Name}  =  ({vt2}) {Trunc(v2)}");
            } */
        }

        // Also dump via SerializedObject — sometimes shows serialized backing the
        // inspector would normally draw, useful to cross-check field names.
        sb.AppendLine("\n-- SerializedObject visible properties --");
        var so = new SerializedObject(obj);
        var it = so.GetIterator();
        if (it.NextVisible(true))
            do { sb.AppendLine($"  {it.propertyPath} : {it.propertyType}"); }
            while (it.NextVisible(false));

        Debug.Log(sb.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // STEP 3 — The decisive test: can we duplicate the asset and write a field
    // back? This does NOT swap a real mesh yet; it makes a copy and proves the
    // copy is editable + savable. If this works, clone-and-swap is buildable.
    // Select a fragment asset, then run.
    // ──────────────────────────────────────────────────────────────────────────
    [MenuItem("Tools/shakee's Tools/Debug/Pawn Probe/3. Try Clone Selected Fragment", false, 122)]
    static void TryCloneFragment()
    {
        var obj = Selection.activeObject;
        if (obj == null) { Debug.LogWarning("Select a fragment asset first."); return; }

        string srcPath = AssetDatabase.GetAssetPath(obj);
        if (string.IsNullOrEmpty(srcPath)) { Debug.LogWarning("No asset path."); return; }

        // Target a writable location; never write into read-only bundle/extract folders blindly.
        const string outDir = "Assets/_PawnProbe";
        if (!AssetDatabase.IsValidFolder(outDir))
            AssetDatabase.CreateFolder("Assets", "_PawnProbe");

        try
        {
            // Path A: main asset → CopyAsset duplicates the whole file (and sub-assets).
            string dst = $"{outDir}/{obj.name}_clone.asset";
            bool copied = AssetDatabase.CopyAsset(srcPath, dst);
            Debug.Log($"[Probe] CopyAsset({srcPath} -> {dst}) = {copied}");

            if (copied)
            {
                AssetDatabase.ImportAsset(dst);
                var clone = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(dst);
                Debug.Log($"[Probe] Clone loaded: {(clone != null ? clone.GetType().FullName : "NULL")}. " +
                          "If non-null, the asset duplicates cleanly — Step 3a (sub-asset clone) may also be needed " +
                          "if the fragment is a sub-asset rather than a main asset.");
            }

            // Path B (sub-asset case): Instantiate the object in memory, which is how
            // you'd clone a sub-asset that CopyAsset can't target directly.
            var mem = UnityEngine.Object.Instantiate(obj);
            Debug.Log($"[Probe] Object.Instantiate in memory = {(mem != null ? "OK (" + mem.GetType().Name + ")" : "NULL")}. " +
                      "If OK, you can build a sub-asset clone via AssetDatabase.AddObjectToAsset + a SerializedObject edit.");
            if (mem != null) UnityEngine.Object.DestroyImmediate(mem);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Probe] Clone attempt threw — this is the gate. The fragment type likely " +
                           $"resists creation/duplication outside Amplitude's import path.\n{e}");
        }
    }

    static string Trunc(object v)
    {
        if (v == null) return "null";
        string s = v.ToString();
        return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
    }
}