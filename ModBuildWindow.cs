using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Amplitude.Framework.Editor.Builds;
using Amplitude.Framework.Runtime;
using Amplitude.Mercury.Production.Modification;
using UnityEditor;
using UnityEngine;
using AssetBundleManifest = Amplitude.Framework.Asset.AssetBundleManifest;

/// <summary>
/// Lightweight alternative to the Mod Editor's "Build Modification" button (Mercury/Mod Editor),
/// for fast local iteration:
///  - the asset bundle is built directly via AssetBundleBuildSettings.Build (the exact same call
///    "Amplitude/Build Windows/AssetBundle Builder" makes, and just as incremental — Unity's own
///    build cache skips unchanged content);
///  - the version bump is a plain field write you control (manual override or auto-increment),
///    instead of ModuleEditor's mandatory per-build Minor++;
///  - deploying skips ModuleEditor's Distribute step (which exists so Steam Workshop/mod.io have a
///    versioned, GUID-named snapshot to upload from) and instead copies the build's stable output
///    directory straight into the game's Community folder — the same thing a symlink there would
///    give you, just as an explicit copy. Build() always writes to the same path regardless of the
///    mod's name/version (Assets/AssetBundles/&lt;platform&gt;/&lt;TargetName&gt;/, per the
///    BuildSettings asset's fixed TargetName), so no renaming is needed for this to work.
/// </summary>
public class ModBuildWindow : EditorWindow
{
    const BuildTarget Target = BuildTarget.StandaloneWindows64;

    RuntimeModule _runtimeModule;
    AssetBundleBuildSettings _buildSettings;

    bool _autoIncrementMinor = true;
    int _major, _minor, _build, _revision;
    bool _allowPatchAssetBundle = true;
    bool _copyToCommunityFolder = true;

    [MenuItem("Tools/Build And Deploy Mod", false, 1)]
    static void Open() => GetWindow<ModBuildWindow>("Build Mod");

    void OnEnable() => Locate();

    void Locate()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:RuntimeModule"))
        { _runtimeModule = AssetDatabase.LoadAssetAtPath<RuntimeModule>(AssetDatabase.GUIDToAssetPath(guid)); break; }
        foreach (var guid in AssetDatabase.FindAssets("t:AssetBundleBuildSettings"))
        { _buildSettings = AssetDatabase.LoadAssetAtPath<AssetBundleBuildSettings>(AssetDatabase.GUIDToAssetPath(guid)); break; }

        SyncVersionFields();
    }

    void SyncVersionFields()
    {
        if (_runtimeModule == null) return;
        var v = _runtimeModule.Version;
        _major = v.Major; _minor = v.Minor; _build = v.Build; _revision = v.Revision;
    }

    void OnGUI()
    {
        if (_runtimeModule == null || _buildSettings == null)
        {
            EditorGUILayout.HelpBox("Couldn't find a RuntimeModule and/or AssetBundleBuildSettings asset in the project.", MessageType.Error);
            if (GUILayout.Button("Retry")) Locate();
            return;
        }

        EditorGUILayout.LabelField("Mod", _runtimeModule.name, EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Build settings", AssetDatabase.GetAssetPath(_buildSettings), EditorStyles.miniLabel);
        EditorGUILayout.Space(8);

        EditorGUILayout.LabelField("Version", EditorStyles.boldLabel);
        var current = _runtimeModule.Version;
        EditorGUILayout.LabelField("Current", $"{current.Major}.{current.Minor}.{current.Build}.{current.Revision}", EditorStyles.miniLabel);
        _autoIncrementMinor = EditorGUILayout.ToggleLeft("Auto-increment Minor", _autoIncrementMinor);
        using (new EditorGUI.DisabledScope(_autoIncrementMinor))
        {
            EditorGUILayout.BeginHorizontal();
            _major = EditorGUILayout.IntField("Major", _major);
            _minor = EditorGUILayout.IntField("Minor", _minor);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            _build = EditorGUILayout.IntField("Build", _build);
            _revision = EditorGUILayout.IntField("Revision", _revision);
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Build", EditorStyles.boldLabel);
        _allowPatchAssetBundle = EditorGUILayout.ToggleLeft("Allow patch asset bundle (incremental)", _allowPatchAssetBundle);
        _copyToCommunityFolder = EditorGUILayout.ToggleLeft("Copy to game's Community mods folder", _copyToCommunityFolder);

        EditorGUILayout.Space(12);
        if (GUILayout.Button("Build && Deploy (local)", GUILayout.Height(30)))
            DoBuild();

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Build For Publish (Workshop / mod.io)", GUILayout.Height(30)))
            DoPublishBuild();
    }

    void ApplyVersion()
    {
        var version = _runtimeModule.Version;
        if (_autoIncrementMinor) version.Minor++;
        else { version.Major = (short)_major; version.Minor = (short)_minor; version.Build = (short)_build; version.Revision = (short)_revision; }
        _runtimeModule.Version = version;
        EditorUtility.SetDirty(_runtimeModule);
        AssetDatabase.SaveAssets();
        SyncVersionFields();  // keep the displayed fields in sync with what was just written
        Debug.Log($"[ModBuild] Version set to {version.Major}.{version.Minor}.{version.Build}.{version.Revision}.");
    }

    void DoBuild()
    {
        // 1. Version — plain field write, no rename, no mandatory bump.
        ApplyVersion();

        // 2. Build — same call "AssetBundle Builder" makes; incremental via Unity's own build cache.
        // Always writes to Assets/AssetBundles/<platform>/<TargetName>/, independent of the mod's
        // name/guid/version, so there's nothing to rename for this to land somewhere predictable.
        try
        {
            _buildSettings.Build(new AssetBundleBuildOptions { BuildTarget = Target, AllowPatchAssetBundle = _allowPatchAssetBundle });
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ModBuild] AssetBundle build failed: {ex}");
            return;
        }
        Debug.Log("[ModBuild] AssetBundle build complete.");

        // 3. Copy the build output straight into the Community folder — what a symlink there would
        // give you anyway, just explicit. Skips ModuleEditor's Distribute/rename dance entirely;
        // that exists for Steam Workshop/mod.io's versioned upload snapshot, not for local loading.
        if (_copyToCommunityFolder)
        {
            if (!TryCopyToCommunityFolder(out var error))
            { Debug.LogError($"[ModBuild] Copy-to-Community step failed: {error}"); return; }
            Debug.Log("[ModBuild] Copied to the Community mods folder.");
        }

        Debug.Log("[ModBuild] Done.");
    }

    bool TryCopyToCommunityFolder(out string error)
    {
        error = null;
        if (!_buildSettings.TryGetOutputDirectoryPath(Target, out var outputDirectoryPath) || !Directory.Exists(outputDirectoryPath))
        {
            error = $"Build output directory not found: {outputDirectoryPath}";
            return false;
        }
        // The real local-mods folder is under the user's Documents folder, not the Steam install
        // directory — ModuleEditor's own GetCommunityFolderPath() assumes Application.GameDirectory
        // is already Documents-rooted, which doesn't hold for every install layout.
        string documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string platformFolderName = Amplitude.Framework.Asset.AssetDatabase.GetAssetBundlePlatformFolderName(Target);
        string destinationRoot = Path.Combine(documentsFolder, "Humankind", "Community", $"{_runtimeModule.name}.{_runtimeModule.GUID}");
        string destination = Path.Combine(destinationRoot, platformFolderName, _buildSettings.TargetName);

        try
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            CopyDirectoryRecursive(outputDirectoryPath, destination);
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
        return true;
    }

    static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var filePath in Directory.GetFiles(sourceDir))
            File.Copy(filePath, Path.Combine(destinationDir, Path.GetFileName(filePath)), overwrite: true);
        foreach (var subDir in Directory.GetDirectories(sourceDir))
            CopyDirectoryRecursive(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
    }

    // ── Publish build: matches ModuleEditor.TryBuildDatabasePlugin exactly, so Distribute (and
    // therefore Steam Workshop / mod.io, which upload from the Distribution folder) can find the
    // output. Unlike the local build, this temporarily renames AssetBundleBuildSettings.TargetName
    // and AssetBundleManifest.AssetBundleName to "<name>.<guid>.<major>.<minor>" — NOT the
    // RuntimeModule asset itself — builds, then restores both, wrapped in try/finally so neither
    // is ever left renamed if something throws.
    void DoPublishBuild()
    {
        ApplyVersion();

        var databasePlugin = _runtimeModule.RuntimePlugins.OfType<DatabasePlugin>().FirstOrDefault();
        if (databasePlugin == null) { Debug.LogError("[ModBuild] No DatabasePlugin runtime plugin on the RuntimeModule."); return; }
        var manifest = databasePlugin.AssetBundleManifest;
        if (manifest == null) { Debug.LogError("[ModBuild] DatabasePlugin has no AssetBundleManifest assigned."); return; }

        // Clear stale build/distribution output before producing a fresh one.
        ClearSubdirectories(manifest.AssetsBundleDirectoryPath);
        string distributionRoot = Path.Combine(Directory.GetCurrentDirectory(), "Distribution", "Modification");
        ClearSubdirectories(distributionRoot);

        var version = _runtimeModule.Version;
        string uniqueName = $"{_runtimeModule.name}.{_runtimeModule.GUID}.{version.Major}.{version.Minor}";
        string previousTargetName = _buildSettings.TargetName;
        string previousManifestName = manifest.AssetBundleName;

        bool buildOk = true;
        try
        {
            _buildSettings.TargetName = uniqueName;
            EditorUtility.SetDirty(_buildSettings);
            manifest.AssetBundleName = uniqueName;
            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssets();

            _buildSettings.BuildFull(new AssetBundleBuildOptions { BuildTarget = Target });
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ModBuild] Publish build failed: {ex}");
            buildOk = false;
        }
        finally
        {
            _buildSettings.TargetName = previousTargetName;
            EditorUtility.SetDirty(_buildSettings);
            manifest.AssetBundleName = previousManifestName;
            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssets();
        }
        if (!buildOk) return;
        Debug.Log("[ModBuild] Publish build complete.");

        if (!TryInvokeDistributeModification(out var error))
        { Debug.LogError($"[ModBuild] Distribute step failed: {error}"); return; }
        Debug.Log($"[ModBuild] Distribution folder ready for Workshop/mod.io publish: {distributionRoot}");
    }

    static void ClearSubdirectories(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
            Directory.Delete(dir, recursive: true);
    }

    bool TryInvokeDistributeModification(out string error)
    {
        error = null;
        var method = typeof(ModuleEditor).GetMethod("DistributeModification",
            BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(RuntimeModule), typeof(BuildTarget), typeof(bool) }, null);
        if (method == null)
        {
            error = "ModuleEditor.DistributeModification(RuntimeModule, BuildTarget, bool) not found (Mod Editor version mismatch?).";
            return false;
        }
        object result = method.Invoke(null, new object[] { _runtimeModule, Target, false });
        if (result is bool ok && !ok) { error = "Method returned false (see prior Debug.LogError for details)."; return false; }
        return true;
    }
}
