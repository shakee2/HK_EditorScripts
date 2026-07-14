using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Shared inline translation editor for %key localization fields — the same Import-for-editing
/// + editable TextArea flow as TechTreeWindow and InspectorDiagnostics, but drawn directly
/// below the key field in the inspector (via <see cref="LocalizationKeyStringDrawer"/>).
/// Writes through <see cref="ArchiveTranslations"/> override rows.
/// </summary>
public static class InlineLocalizationEditor
{
    static readonly Dictionary<string, string> s_editBuffers = new();
    static readonly Dictionary<string, bool> s_importedCache = new();
    static Dictionary<string, string> s_resolveDict;
    static int s_dictGeneration = -1;
    static int s_mergedVersion = -1;

    static InlineLocalizationEditor()
    {
        Undo.undoRedoPerformed += OnExternalChange;
        EditorApplication.projectChanged += OnExternalChange;
    }

    static void OnExternalChange()
    {
        s_resolveDict = null;
        s_editBuffers.Clear();
        s_importedCache.Clear();
        s_mergedVersion = -1;
    }

    public static void InvalidateCaches()
    {
        s_resolveDict = null;
        s_importedCache.Clear();
        s_mergedVersion = -1;
    }

    /// <summary>True when <paramref name="root"/> is a UIMapper (or subclass) or DescriptorMapper.</summary>
    public static bool AppliesTo(UnityEngine.Object root)
    {
        if (root == null) return false;
        if (!TryResolveTypes()) return false;
        return (t_UIMapper != null && t_UIMapper.IsInstanceOfType(root))
            || (t_DescriptorMapper != null && t_DescriptorMapper.IsInstanceOfType(root));
    }

    /// <summary>
    /// Draws a compact translation block below a %key field: resolved preview + Import, or an
    /// editable TextArea once imported into a project override row.
    /// </summary>
    public static void DrawBelowField(string key, bool isMultiline = false)
    {
        if (string.IsNullOrEmpty(key) || key[0] != '%') return;

        EditorGUI.indentLevel++;
        try
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Translation", EditorStyles.miniBoldLabel, GUILayout.Width(72));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Copy key", EditorStyles.miniButton, GUILayout.Width(64)))
                EditorGUIUtility.systemCopyBuffer = key;
            EditorGUILayout.EndHorizontal();

            bool imported = IsImported(key);
            string text = ResolveText(key);

            if (!imported)
            {
                EditorGUILayout.LabelField(
                    string.IsNullOrEmpty(text) ? "(vanilla / unresolved)" : text,
                    EditorStyles.wordWrappedMiniLabel);
                if (GUILayout.Button("Import for editing", EditorStyles.miniButton, GUILayout.Width(140)))
                {
                    if (ArchiveTranslations.EnsureOverride(key, text) != null)
                    {
                        s_importedCache.Remove(key);
                        s_editBuffers.Remove(key);
                        s_resolveDict = null;
                        NotifyDependents();
                    }
                }
            }
            else
            {
                if (!s_editBuffers.TryGetValue(key, out var buf))
                {
                    buf = text;
                    s_editBuffers[key] = buf;
                }

                var style = EditorStyles.textArea;
                float availW = Mathf.Max(40f, EditorGUIUtility.currentViewWidth - 80f);
                float h = style.CalcHeight(new GUIContent(buf ?? ""), availW);
                h = Mathf.Max(h, isMultiline ? 50f : 34f);

                EditorGUI.BeginChangeCheck();
                string edited = EditorGUILayout.TextArea(buf, style, GUILayout.Height(h));
                if (EditorGUI.EndChangeCheck())
                {
                    s_editBuffers[key] = edited;
                    try { ArchiveTranslations.SetOverrideText(key, edited); } catch { }
                    s_resolveDict = null;
                    NotifyDependents();
                }
            }

            EditorGUILayout.EndVertical();
        }
        finally { EditorGUI.indentLevel--; }
    }

    /// <summary>Full localization row (key line + translation) for aggregate panels.</summary>
    public static void DrawLocField(string key, string text, bool imported, bool isMultiline = false)
    {
        if (string.IsNullOrEmpty(key)) return;

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.SelectableLabel(key, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(44)))
            EditorGUIUtility.systemCopyBuffer = key;
        EditorGUILayout.EndHorizontal();

        if (!imported)
        {
            EditorGUILayout.LabelField(string.IsNullOrEmpty(text) ? "(vanilla / unresolved)" : text, EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Import for editing", EditorStyles.miniButton, GUILayout.Width(140)))
            {
                if (ArchiveTranslations.EnsureOverride(key, text) != null)
                {
                    s_importedCache.Remove(key);
                    s_editBuffers.Remove(key);
                    s_resolveDict = null;
                    NotifyDependents();
                }
            }
            EditorGUILayout.Space(3);
            return;
        }

        if (!s_editBuffers.TryGetValue(key, out var buf)) { buf = text; s_editBuffers[key] = buf; }
        var style = EditorStyles.textArea;
        float availW = Mathf.Max(40f, EditorGUIUtility.currentViewWidth - 60f);
        float h = style.CalcHeight(new GUIContent(buf ?? ""), availW);
        h = Mathf.Max(h, isMultiline ? 50f : 34f);
        EditorGUI.BeginChangeCheck();
        string edited = EditorGUILayout.TextArea(buf, style, GUILayout.Height(h));
        if (EditorGUI.EndChangeCheck())
        {
            s_editBuffers[key] = edited;
            try { ArchiveTranslations.SetOverrideText(key, edited); } catch { }
            s_resolveDict = null;
            NotifyDependents();
        }
        EditorGUILayout.Space(3);
    }

    public static bool IsImported(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (!s_importedCache.TryGetValue(key, out var v))
            s_importedCache[key] = v = ArchiveTranslations.HasOverride(key);
        return v;
    }

    public static string ResolveText(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        var dict = GetResolveDict();
        return dict != null && dict.TryGetValue(key, out var t) ? t : "";
    }

    static Dictionary<string, string> GetResolveDict()
    {
        int gen = DescriptorMapperPreview.Generation;
        int merged = ArchiveTranslations.MergedDictVersion;
        if (s_resolveDict != null && s_dictGeneration == gen && s_mergedVersion == merged) return s_resolveDict;
        s_dictGeneration = gen;
        s_mergedVersion = merged;
        try { s_resolveDict = ArchiveTranslations.BuildKeyToTextDict(); }
        catch { s_resolveDict = new Dictionary<string, string>(); }
        return s_resolveDict;
    }

    static void NotifyDependents()
    {
        InlineLocalizationEditor.InvalidateCaches();
        InspectorDiagnostics.InvalidateAll();
        DescriptorMapperPreview.InvalidateTranslationCaches();
    }

    // ── Type resolution (no hard assembly refs) ───────────────────────────────
    static bool s_typesResolved;
    static Type t_UIMapper, t_DescriptorMapper;

    static bool TryResolveTypes()
    {
        if (s_typesResolved) return t_UIMapper != null || t_DescriptorMapper != null;
        s_typesResolved = true;
        t_UIMapper = FindType("Amplitude.UI.UIMapper");
        t_DescriptorMapper = FindType("Amplitude.Mercury.EffectMapper.DescriptorMapper");
        return true;
    }

    static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        { try { var t = asm.GetType(fullName); if (t != null) return t; } catch { } }
        return null;
    }
}
