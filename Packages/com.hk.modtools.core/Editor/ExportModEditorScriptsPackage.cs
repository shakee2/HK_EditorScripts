using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-shot exporter for the core interconnected editor scripts as a Unity package. Run via
/// "Tools/Export Mod Editor Scripts Package". The package contains ONLY these files (cs + meta),
/// not the unrelated tools (Debug/, UnitVisualWorkflow/, ModTools/ModBuildWindow.cs):
///   - Shared/VanillaDatabaseMount.cs    (shared vanilla-bundle mount — foundation for the other four)
///   - Shared/ArchiveTranslations.cs     (translations-bundle mount + project-override read/write)
///   - ModTools/TechTreeData.cs            (tech-tree data layer)
///   - ModTools/TechTreeWindow.cs          (tech-tree viewer/editor window)
///   - ModTools/DatabaseBrowser.cs         (generic database browser window)
///   - ModTools/DescriptorPropertyIndex.cs (descriptor property browser window)
///   - ModTools/AssetExplorer.cs           (browse + import any vanilla asset bundle)
///   - Upgrades/DescriptorMapperPreview.cs (in-game tooltip render preview — PropertyEffectDrawer dep)
///   - Upgrades/PropertyEffectDrawer.cs    (PropertyEffect Odin drawer: formula autocomplete + inline render)
///   - Upgrades/InspectorAnalysisPanel.cs  (header host: mapper toolbar + preview + diagnostics)
///   - Upgrades/InspectorDiagnostics.cs    (diagnostics engine + aggregate loc foldout; DatabaseBrowser badges)
///   - Upgrades/DescriptorMapperGenerator.cs (generate/select paired DescriptorMapper)
///   - Upgrades/LocalizationKeyDrawer.cs   (inline %key translation on UIMapper / DescriptorMapper)
///   - Upgrades/InlineLocalizationEditor.cs (shared loc Import/edit helpers — dep of drawer + diagnostics)
///   - Docs/manual.md               (user manual — shipped with the package)
/// The translations bundle itself (Assets/Editor/Resources/Translations/...) ships with
/// ModTools and is NOT included.
/// </summary>
public static class ExportModEditorScriptsPackage
{
    const string MENU = "Tools/shakee's Tools/Export Mod Editor Scripts Package";

    // MULTI-PACKAGE: the shared mounts now live in their own package (com.hk.modtools.shared); the
    // rest of the export set is in core. Names are "<Editor-relative path>" resolved per package.
    const string CorePackage = "Packages/com.hk.modtools.core";
    const string SharedPackage = "Packages/com.hk.modtools.shared";

    static readonly string[] SHARED_SCRIPT_NAMES =
    {
        "VanillaDatabaseMount.cs",
        "ArchiveTranslations.cs",
    };

    static readonly string[] SCRIPT_NAMES =
    {
        "ModTools/TechTreeData.cs",
        "ModTools/TechTreeWindow.cs",
        "ModTools/DatabaseBrowser.cs",
        "ModTools/DescriptorPropertyIndex.cs",
        "ModTools/AssetExplorer.cs",
        "Upgrades/DescriptorMapperPreview.cs",
        "Upgrades/PropertyEffectDrawer.cs",
        "Upgrades/InspectorAnalysisPanel.cs",
        "Upgrades/InspectorDiagnostics.cs",
        "Upgrades/DescriptorMapperGenerator.cs",
        "Upgrades/LocalizationKeyDrawer.cs",
        "Upgrades/InlineLocalizationEditor.cs",
    };

    const string ManualRelative = "Docs/manual.md";

    static string ResolveEditorScript(string packageRoot, string fileName)
    {
        string packagePath = $"{packageRoot}/Editor/{fileName}";
        if (File.Exists(packagePath)) return packagePath;
        string assetsPath = $"Assets/Scripts/Editor/{fileName}";
        if (File.Exists(assetsPath)) return assetsPath;
        return packagePath;
    }

    static string ResolveManual()
    {
        string packagePath = $"{CorePackage}/{ManualRelative}";
        if (File.Exists(packagePath)) return packagePath;
        string assetsPath = $"Assets/Scripts/{ManualRelative}";
        if (File.Exists(assetsPath)) return assetsPath;
        return packagePath;
    }

    [MenuItem(MENU, false, 200)]
    static void Export()
    {
        var scripts = SHARED_SCRIPT_NAMES.Select(n => ResolveEditorScript(SharedPackage, n))
            .Concat(SCRIPT_NAMES.Select(n => ResolveEditorScript(CorePackage, n))).ToList();
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
