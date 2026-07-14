using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-shot exporter for the core interconnected editor scripts as a Unity package. Run via
/// "Tools/Export Mod Editor Scripts Package". The package contains ONLY these files (cs + meta),
/// not the unrelated tools (FormulaProbe, Probing, AnimationManagerContent, PawnFragmentAuthor,
/// ModBuildWindow):
///   - VanillaDatabaseMount.cs    (shared vanilla-bundle mount — foundation for the other four)
///   - ArchiveTranslations.cs     (translations-bundle mount + project-override read/write)
///   - TechTreeData.cs            (tech-tree data layer)
///   - TechTreeWindow.cs          (tech-tree viewer/editor window)
///   - DatabaseBrowser.cs         (generic database browser window)
///   - DescriptorPropertyIndex.cs (descriptor property browser window)
///   - AssetExplorer.cs           (browse + import any vanilla asset bundle)
///   - DescriptorMapperPreview.cs (in-game tooltip render preview — PropertyEffectDrawer dep)
///   - PropertyEffectDrawer.cs    (PropertyEffect Odin drawer: formula autocomplete + inline render)
///   - InspectorAnalysisPanel.cs  (header host: mapper toolbar + preview + diagnostics)
///   - InspectorDiagnostics.cs    (diagnostics engine + aggregate loc foldout; DatabaseBrowser badges)
///   - DescriptorMapperGenerator.cs (generate/select paired DescriptorMapper)
///   - LocalizationKeyDrawer.cs   (inline %key translation on UIMapper / DescriptorMapper)
///   - InlineLocalizationEditor.cs (shared loc Import/edit helpers — dep of drawer + diagnostics)
///   - Docs/manual.md               (user manual — shipped with the package)
/// The translations bundle itself (Assets/Editor/Resources/Translations/...) ships with
/// ModTools and is NOT included.
/// </summary>
public static class ExportModEditorScriptsPackage
{
    const string MENU = "Tools/shakee's Tools/Export Mod Editor Scripts Package";

    static readonly string[] SCRIPT_NAMES =
    {
        "VanillaDatabaseMount.cs",
        "ArchiveTranslations.cs",
        "TechTreeData.cs",
        "TechTreeWindow.cs",
        "DatabaseBrowser.cs",
        "DescriptorPropertyIndex.cs",
        "AssetExplorer.cs",
        "DescriptorMapperPreview.cs",
        "PropertyEffectDrawer.cs",
        "InspectorAnalysisPanel.cs",
        "InspectorDiagnostics.cs",
        "DescriptorMapperGenerator.cs",
        "LocalizationKeyDrawer.cs",
        "InlineLocalizationEditor.cs",
    };

    const string ManualRelative = "Docs/manual.md";

    static string ResolveEditorScript(string fileName)
    {
        const string package = "Packages/com.shakee.hk-editorscripts/Editor";
        string packagePath = $"{package}/{fileName}";
        if (File.Exists(packagePath)) return packagePath;
        string assetsPath = $"Assets/Scripts/Editor/{fileName}";
        if (File.Exists(assetsPath)) return assetsPath;
        return packagePath;
    }

    static string ResolveManual()
    {
        const string package = "Packages/com.shakee.hk-editorscripts";
        string packagePath = $"{package}/{ManualRelative}";
        if (File.Exists(packagePath)) return packagePath;
        string assetsPath = $"Assets/Scripts/{ManualRelative}";
        if (File.Exists(assetsPath)) return assetsPath;
        return packagePath;
    }

    [MenuItem(MENU, false, 200)]
    static void Export()
    {
        var scripts = SCRIPT_NAMES.Select(ResolveEditorScript).ToList();
        scripts.Add(ResolveManual());
        var missing = scripts.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            Debug.LogError("[ExportPackage] Missing files:\n  " + string.Join("\n  ", missing));
            return;
        }

        // Export WITHOUT IncludeDependencies: these scripts only reference each other (all
        // listed above) plus Amplitude/Unity DLLs that any ModTools install already provides.
        // IncludeDependencies would risk dragging in framework assets the recipient already has.
        // The .meta files are bundled automatically so GUIDs survive import.
        string dir = System.IO.Path.GetDirectoryName(Application.dataPath);  // project root
        string outPath = System.IO.Path.Combine(dir, "ModEditorScripts.unitypackage");
        AssetDatabase.ExportPackage(scripts.ToArray(), outPath, ExportPackageOptions.Default);

        Debug.Log($"[ExportPackage] Wrote {outPath} ({scripts.Count} files: {SCRIPT_NAMES.Length} scripts + manual).");
        EditorUtility.RevealInFinder(outPath);
    }
}
