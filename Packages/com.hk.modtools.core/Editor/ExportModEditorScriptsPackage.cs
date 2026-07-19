using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-shot exporter for the core interconnected editor scripts as a Unity package. Run via
/// "Tools/shakee's Tools/Export Mod Editor Scripts Package". The package contains ONLY these files
/// (cs + meta), not the unrelated tools (Debug/, ModTools/ModBuildWindow.cs, ModTools/AssetExplorer.cs):
///   - com.hk.modtools.shared: VanillaDatabaseMount.cs (shared vanilla-bundle mount — foundation for the other four)
///   - com.hk.modtools.shared: ArchiveTranslations.cs  (translations-bundle mount + project-override read/write)
///   - com.hk.modtools.shared: WindowMinimize.cs       (floating-window minimize strip + lower-right stack)
///   - com.hk.modtools.core: ModTools/TechTreeData.cs            (tech-tree data layer)
///   - com.hk.modtools.core: ModTools/TechTreeWindow.cs          (tech-tree viewer/editor window)
///   - com.hk.modtools.core: ModTools/DatabaseBrowser.cs         (generic database browser window)
///   - com.hk.modtools.core: ModTools/DescriptorPropertyIndex.cs (descriptor property browser window)
///   - com.hk.modtools.core: Upgrades/DescriptorMapperPreview.cs (tooltip breakdown preview — PropertyEffectDrawer dep)
///   - com.hk.modtools.core: Upgrades/PropertyEffectDrawer.cs    (PropertyEffect Odin drawer: formula autocomplete + inline render)
///   - com.hk.modtools.core: Upgrades/InspectorAnalysisPanel.cs  (header host: mapper toolbar + preview + diagnostics)
///   - com.hk.modtools.core: Upgrades/InspectorDiagnostics.cs    (diagnostics engine + aggregate loc foldout; DatabaseBrowser badges)
///   - com.hk.modtools.core: Upgrades/DescriptorMapperGenerator.cs (generate/select paired DescriptorMapper)
///   - com.hk.modtools.core: Upgrades/LocalizationKeyDrawer.cs   (inline %key translation on UIMapper / DescriptorMapper)
///   - com.hk.modtools.core: Upgrades/InlineLocalizationEditor.cs (shared loc Import/edit helpers — dep of drawer + diagnostics)
///   - com.hk.modtools.core: Docs/manual.md                     (user manual — shipped with the package)
/// The translations bundle itself (Assets/Editor/Resources/Translations/...) ships with
/// ModTools and is NOT included.
/// </summary>
public static class ExportModEditorScriptsPackage
{
    const string MENU = "Tools/shakee's Tools/Export Mod Editor Scripts Package";

    static readonly string[] SHARED_SCRIPT_NAMES =
    {
        "VanillaDatabaseMount.cs",
        "ArchiveTranslations.cs",
        "WindowMinimize.cs",
    };

    static readonly string[] CORE_SCRIPT_NAMES =
    {
        "ModTools/TechTreeData.cs",
        "ModTools/TechTreeWindow.cs",
        "ModTools/DatabaseBrowser.cs",
        "ModTools/DescriptorPropertyIndex.cs",
        "Upgrades/DescriptorMapperPreview.cs",
        "Upgrades/PropertyEffectDrawer.cs",
        "Upgrades/InspectorAnalysisPanel.cs",
        "Upgrades/InspectorDiagnostics.cs",
        "Upgrades/DescriptorMapperGenerator.cs",
        "Upgrades/LocalizationKeyDrawer.cs",
        "Upgrades/InlineLocalizationEditor.cs",
    };

    const string ManualRelative = "Docs/manual.md";

    static string ResolveEditorScript(string packageName, string fileName)
    {
        string packagePath = $"Packages/{packageName}/Editor/{fileName}";
        if (File.Exists(packagePath)) return packagePath;
        string assetsPath = $"Assets/Scripts/Editor/{fileName}";
        if (File.Exists(assetsPath)) return assetsPath;
        return packagePath;
    }

    static string ResolveManual()
    {
        const string package = "Packages/com.hk.modtools.core";
        string packagePath = $"{package}/{ManualRelative}";
        if (File.Exists(packagePath)) return packagePath;
        string assetsPath = $"Assets/Scripts/{ManualRelative}";
        if (File.Exists(assetsPath)) return assetsPath;
        return packagePath;
    }

    [MenuItem(MENU, false, 200)]
    static void Export()
    {
        var scripts = SHARED_SCRIPT_NAMES.Select(f => ResolveEditorScript("com.hk.modtools.shared", f))
            .Concat(CORE_SCRIPT_NAMES.Select(f => ResolveEditorScript("com.hk.modtools.core", f)))
            .ToList();
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

        int scriptCount = SHARED_SCRIPT_NAMES.Length + CORE_SCRIPT_NAMES.Length;
        Debug.Log($"[ExportPackage] Wrote {outPath} ({scripts.Count} files: {scriptCount} scripts + manual).");
        EditorUtility.RevealInFinder(outPath);
    }
}
