using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-shot exporter for the six interconnected tech-tree / browser editor scripts as a
/// Unity package. Run via "Tools/Export Mod Editor Scripts Package". The package contains
/// ONLY these six files (cs + meta), not the unrelated tools (FormulaProbe, Probing,
/// AnimationManagerContent, PawnFragmentAuthor, ModBuildWindow):
///   - VanillaDatabaseMount.cs   (shared vanilla-bundle mount — foundation for the other four)
///   - ArchiveTranslations.cs    (translations-bundle mount + project-override read/write)
///   - TechTreeData.cs           (tech-tree data layer)
///   - TechTreeWindow.cs         (tech-tree viewer/editor window)
///   - DatabaseBrowser.cs        (generic database browser window)
///   - DescriptorPropertyIndex.cs (descriptor property browser window)
/// The translations bundle itself (Assets/Editor/Resources/Translations/...) ships with
/// ModTools and is NOT included.
/// </summary>
public static class ExportModEditorScriptsPackage
{
    const string MENU = "Tools/Export Mod Editor Scripts Package";

    static readonly string[] SCRIPTS =
    {
        "Assets/Scripts/Editor/VanillaDatabaseMount.cs",
        "Assets/Scripts/Editor/ArchiveTranslations.cs",
        "Assets/Scripts/Editor/TechTreeData.cs",
        "Assets/Scripts/Editor/TechTreeWindow.cs",
        "Assets/Scripts/Editor/DatabaseBrowser.cs",
        "Assets/Scripts/Editor/DescriptorPropertyIndex.cs",
    };

    [MenuItem(MENU, false, 200)]
    static void Export()
    {
        var missing = SCRIPTS.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            Debug.LogError("[ExportPackage] Missing files:\n  " + string.Join("\n  ", missing));
            return;
        }

        // Export WITHOUT IncludeDependencies: the six scripts only reference each other (all
        // six are in the list) plus Amplitude/Unity DLLs that any ModTools install already
        // provides. IncludeDependencies would risk dragging in framework assets the recipient
        // already has. The .meta files are bundled automatically so GUIDs survive import.
        string dir = System.IO.Path.GetDirectoryName(Application.dataPath);  // project root
        string outPath = System.IO.Path.Combine(dir, "ModEditorScripts.unitypackage");
        AssetDatabase.ExportPackage(SCRIPTS, outPath, ExportPackageOptions.Default);

        Debug.Log($"[ExportPackage] Wrote {outPath} ({SCRIPTS.Length} scripts).");
        EditorUtility.RevealInFinder(outPath);
    }
}
