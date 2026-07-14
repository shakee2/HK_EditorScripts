using System;
using System.Reflection;
using Amplitude.Framework;
using Amplitude.Framework.Utility;
using UnityEditor;
using UnityEngine;
using IoPath = System.IO.Path;
using IoDirectory = System.IO.Directory;

/// <summary>
/// Creates a paired <see cref="Amplitude.Mercury.EffectMapper.DescriptorMapper"/> for a
/// <see cref="Amplitude.Framework.Simulation.Description.Descriptor"/> — the companion row
/// the runtime looks up by matching element name. UIMapper generation is intentionally out
/// of scope here (many definition-specific mapper subclasses).
/// </summary>
[InitializeOnLoad]
public static class DescriptorMapperGenerator
{
    const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const string DefaultMapperDir = "Assets/Databases/ModAdditions";
    const string DefaultMapperCollection = "NewDescriptorMapper";

    static Type t_Descriptor, t_DescriptorMapper, t_DescriptorMapperCollection;
    static FieldInfo f_LocalizedName, f_HideDescriptor, f_Policies;
    static bool s_resolved;

    // Mapper presence is stable until selection changes, assets change, or Generate runs.
    // DrawToolbar only reads this cache — it never re-scans the project on Repaint.
    static int s_inspectedId = -1;
    static int s_statusGeneration;
    static int s_cachedGeneration = -1;
    static MapperStatus s_cachedStatus;

    static DescriptorMapperGenerator()
    {
        TryResolve();
        Selection.selectionChanged += () => s_inspectedId = -1;
        EditorApplication.projectChanged += () => s_statusGeneration++;
        Undo.undoRedoPerformed += () => s_statusGeneration++;
    }

    public enum MapperPresence { None, Project, VanillaOnly }

    public struct MapperStatus
    {
        public MapperPresence presence;
        public UnityEngine.Object projectMapper;
        public UnityEngine.Object vanillaMapper;
    }

    public static bool WillDraw(Editor editor)
    {
        if (editor == null || editor.targets == null || editor.targets.Length != 1) return false;
        var target = editor.target;
        return target != null && TryResolve() && t_Descriptor.IsInstanceOfType(target);
    }

    /// <summary>Returns cached mapper presence for the inspected descriptor. Lookup runs only when selection or assets change.</summary>
    public static MapperStatus GetStatus(UnityEngine.Object descriptor) => EnsureStatus(descriptor);

    static MapperStatus EnsureStatus(UnityEngine.Object descriptor)
    {
        if (descriptor == null || !TryResolve() || !t_Descriptor.IsInstanceOfType(descriptor))
            return default;

        int id = descriptor.GetInstanceID();
        if (id == s_inspectedId && s_cachedGeneration == s_statusGeneration)
            return s_cachedStatus;

        s_inspectedId = id;
        s_cachedGeneration = s_statusGeneration;
        s_cachedStatus = BuildStatus(descriptor);
        return s_cachedStatus;
    }

    static void RefreshStatus(UnityEngine.Object descriptor)
    {
        if (descriptor == null || !TryResolve() || !t_Descriptor.IsInstanceOfType(descriptor))
        {
            s_inspectedId = -1;
            s_cachedStatus = default;
            return;
        }
        s_inspectedId = descriptor.GetInstanceID();
        s_cachedGeneration = s_statusGeneration;
        s_cachedStatus = BuildStatus(descriptor);
    }

    static MapperStatus BuildStatus(UnityEngine.Object descriptor)
    {
        var s = new MapperStatus();
        var mapper = DescriptorMapperPreview.FindMapperByName(descriptor.name);
        if (mapper == null) return s;
        if (VanillaDatabaseMount.IsVanillaAsset(mapper))
        {
            s.vanillaMapper = mapper;
            s.presence = MapperPresence.VanillaOnly;
        }
        else
        {
            s.projectMapper = mapper;
            s.presence = MapperPresence.Project;
        }
        return s;
    }

    /// <summary>Draws the mapper toolbar row for a Descriptor inspector header.</summary>
    public static void DrawToolbar(Editor editor)
    {
        var descriptor = editor?.target;
        if (descriptor == null || !TryResolve() || !t_Descriptor.IsInstanceOfType(descriptor)) return;

        var status = GetStatus(descriptor);
        EditorGUILayout.Space(2);
        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
        GUILayout.Label("DescriptorMapper", EditorStyles.miniBoldLabel, GUILayout.Width(108));

        switch (status.presence)
        {
            case MapperPresence.Project:
                EditorGUILayout.LabelField($"project · {status.projectMapper.name}", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Select mapper", EditorStyles.miniButton, GUILayout.Width(96)))
                    Select(status.projectMapper);
                break;

            case MapperPresence.VanillaOnly:
                EditorGUILayout.LabelField("vanilla only (read-only)", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Create project mapper", EditorStyles.miniButton, GUILayout.Width(132)))
                    TryGenerate(descriptor);
                break;

            default:
                EditorGUILayout.LabelField("none", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Generate DescriptorMapper", EditorStyles.miniButton, GUILayout.Width(168)))
                    TryGenerate(descriptor);
                break;
        }

        EditorGUILayout.EndHorizontal();
    }

    /// <summary>
    /// Ensures a writable project DescriptorMapper exists for <paramref name="descriptor"/>.
    /// Duplicates a vanilla template when one exists; otherwise creates a blank row.
    /// </summary>
    public static UnityEngine.Object TryGenerate(UnityEngine.Object descriptor)
    {
        if (descriptor == null || !TryResolve() || !t_Descriptor.IsInstanceOfType(descriptor))
        {
            Debug.LogWarning("[DescriptorMapperGenerator] Target is not a Descriptor.");
            return null;
        }

        var status = GetStatus(descriptor);
        if (status.projectMapper != null)
        {
            Select(status.projectMapper);
            return status.projectMapper;
        }

        try
        {
            var collection = GetOrCreateTargetCollection(descriptor, out string colPath);
            if (collection == null)
            {
                Debug.LogError("[DescriptorMapperGenerator] Could not get-or-create a DescriptorMapperCollection.");
                return null;
            }

            UnityEngine.Object mapper = null;
            if (status.vanillaMapper is IDatatableElement vanillaEl)
            {
                IDatatableElement[] dups = null;
                var colRef = collection;
                bool ok = DatatableElementCollectionUtility.TryDuplicateDatatableElements(
                    new[] { vanillaEl }, ref colRef, ref dups,
                    showWarningDialogThresholdCount: false, ensureUniqueName: false, reimport: false);
                collection = colRef;
                if (ok && dups != null && dups.Length > 0) mapper = dups[0] as UnityEngine.Object;
            }

            if (mapper == null)
                mapper = CreateBlankMapper(collection, descriptor.name);

            if (mapper == null)
            {
                Debug.LogError($"[DescriptorMapperGenerator] Failed to create DescriptorMapper for '{descriptor.name}'.");
                return null;
            }

            if (mapper is IDatatableElement editable) editable.SetEditable(true);
            EditorUtility.SetDirty(mapper);
            EditorUtility.SetDirty(collection as UnityEngine.Object);
            AssetDatabase.SaveAssets();

            DescriptorMapperPreview.InvalidateNameCache();
            InspectorDiagnostics.InvalidateAll();
            s_statusGeneration++;
            RefreshStatus(descriptor);

            Debug.Log($"[DescriptorMapperGenerator] Created DescriptorMapper '{descriptor.name}' in '{colPath}'.");
            Select(mapper);
            return mapper;
        }
        catch (Exception e)
        {
            Debug.LogError($"[DescriptorMapperGenerator] Generate failed for '{descriptor.name}': {e.Message}");
            return null;
        }
    }

    static UnityEngine.Object CreateBlankMapper(DatatableElementCollection collection, string name)
    {
        if (collection == null || t_DescriptorMapper == null) return null;
        if (collection.CreateDatatableElement(t_DescriptorMapper, name) is not UnityEngine.Object mapper) return null;
        if (f_LocalizedName != null) f_LocalizedName.SetValue(mapper, "%" + name);
        if (f_HideDescriptor != null) f_HideDescriptor.SetValue(mapper, false);
        if (f_Policies != null)
        {
            var policyType = t_DescriptorMapper.GetNestedType("PropertyEffectPolicy", ALL);
            f_Policies.SetValue(mapper, policyType != null
                ? Array.CreateInstance(policyType, 0)
                : Array.Empty<object>());
        }
        return mapper;
    }

    static DatatableElementCollection GetOrCreateTargetCollection(UnityEngine.Object descriptor, out string collectionAssetPath)
    {
        collectionAssetPath = null;
        if (t_DescriptorMapperCollection == null) return null;

        string descPath = AssetDatabase.GetAssetPath(descriptor);
        string dir = string.IsNullOrEmpty(descPath)
            ? DefaultMapperDir
            : IoPath.GetDirectoryName(descPath)?.Replace('\\', '/');

        if (string.IsNullOrEmpty(dir) || !dir.StartsWith("Assets/", StringComparison.Ordinal))
            dir = DefaultMapperDir;

        // Prefer an existing DescriptorMapperCollection beside the descriptor.
        if (IoDirectory.Exists(dir))
        {
            foreach (var file in IoDirectory.GetFiles(dir, "*.asset"))
            {
                string assetPath = file.Replace('\\', '/');
                if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                {
                    if (obj != null && t_DescriptorMapperCollection.IsInstanceOfType(obj)
                        && obj is DatatableElementCollection col)
                    {
                        collectionAssetPath = assetPath;
                        return col;
                    }
                }
            }
        }

        string collectionName = DeriveMapperCollectionName(descPath);
        var created = DatatableElementCollectionUtility.GetOrCreateDatatableElementCollection(
            t_DescriptorMapperCollection, dir, collectionName, startNameEditing: false);
        collectionAssetPath = dir + "/" + collectionName + ".asset";
        return created;
    }

    static string DeriveMapperCollectionName(string descriptorAssetPath)
    {
        if (string.IsNullOrEmpty(descriptorAssetPath)) return DefaultMapperCollection;
        string baseName = IoPath.GetFileNameWithoutExtension(descriptorAssetPath);
        if (baseName.EndsWith("Descriptor", StringComparison.Ordinal))
            return baseName + "Mapper";
        return baseName + "DescriptorMapper";
    }

    static void Select(UnityEngine.Object obj)
    {
        if (obj == null) return;
        Selection.activeObject = obj;
        EditorGUIUtility.PingObject(obj);
    }

    static bool TryResolve()
    {
        if (s_resolved) return t_Descriptor != null && t_DescriptorMapper != null;
        s_resolved = true;

        t_Descriptor = FindType("Amplitude.Framework.Simulation.Description.Descriptor");
        t_DescriptorMapper = FindType("Amplitude.Mercury.EffectMapper.DescriptorMapper");
        t_DescriptorMapperCollection = FindType("Amplitude.Mercury.EffectMapper.DescriptorMapperCollection");

        if (t_DescriptorMapper != null)
        {
            f_LocalizedName = t_DescriptorMapper.GetField("LocalizedName", ALL);
            f_HideDescriptor = t_DescriptorMapper.GetField("HideDescriptor", ALL);
            f_Policies = t_DescriptorMapper.GetField("PropertyEffectPolicies", ALL);
        }
        return t_Descriptor != null && t_DescriptorMapper != null;
    }

    static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        { try { var t = asm.GetType(fullName); if (t != null) return t; } catch { } }
        return null;
    }
}
