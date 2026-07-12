using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Severity of a diagnostic finding. Ordered so Max() picks the worst.
public enum DiagSeverity { None = 0, Warn = 1, Crash = 2 }

// One diagnostic result for a datatable element.
public struct DiagFinding
{
    public DiagSeverity severity;
    public string message;
    public string source;               // e.g. "DataController.cs:4734" — the authoritative rule
    public UnityEngine.Object jumpTo;   // optional element to ping (missing ref target, etc.)
    public DiagFinding(DiagSeverity s, string m, string src, UnityEngine.Object jump = null)
    { severity = s; message = m; source = src; jumpTo = jump; }
}

/// <summary>
/// A GUI-free diagnostics engine plus two surfaces:
///   • an inline panel under every datatable-element inspector (Editor.finishedDefaultHeaderGUI,
///     the same seam DescriptorMapperPreview uses — layers over Odin without owning the editor), and
///   • a per-element worst-severity badge the DatabaseBrowser draws on its rows.
///
/// The engine is <see cref="Analyze"/>: a cached, exception-isolated pure function
/// element → findings. v1 validators (extensible toward the ~15 reset-capable errors in
/// Docs/CompatPatcher-LoadValidations.md):
///   - Unfilled rows in a reference list (added-but-empty 'none' row)           (🛑 crashes at load)
///   - Unlock-event constructibles: not-found :4713 / empire-wide :4718 /
///     mixed-family :4734 — the confirmed ENC+VIP crash                         (🛑 will crash load)
///   - Descriptor / DescriptorMapper: malformed RPN + wrong policy flags        (🛑, via
///     DescriptorMapperPreview.CollectFindings — reuses its resolved analysis)
///
/// The panel also hosts an inline localization editor (edit %keys in place, backed by
/// ArchiveTranslations override rows — no second window).
/// </summary>
[InitializeOnLoad]
public static class InspectorDiagnostics
{
    const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    // ── Public engine API (GUI-free; safe to call from any editor GUI) ────────
    public static IReadOnlyList<DiagFinding> Analyze(UnityEngine.Object element)
        => (IReadOnlyList<DiagFinding>)GetOrBuild(element)?.findings ?? s_empty;

    public static DiagSeverity WorstSeverity(UnityEngine.Object element)
    {
        var f = GetOrBuild(element)?.findings;
        if (f == null || f.Count == 0) return DiagSeverity.None;
        var worst = DiagSeverity.None;
        for (int i = 0; i < f.Count; i++) if (f[i].severity > worst) worst = f[i].severity;
        return worst;
    }

    public static void InvalidateAll()
    {
        s_generation++;
        s_cache.Clear();
        s_nameIndex.Clear();
        s_locDict = null;
        s_locEdit.Clear();
    }

    static readonly DiagFinding[] s_empty = Array.Empty<DiagFinding>();

    // ── Per-element cache ─────────────────────────────────────────────────────
    // finishedDefaultHeaderGUI and the browser row-draw both hammer this. Results are a pure
    // function of the element's serialized data, so cache per instanceID and refresh only when
    // the generation counter bumps (undo / asset change / import) or the TTL lapses — and only
    // ever REBUILD on a Layout event, so a panel drawn with EditorGUILayout keeps an identical
    // control count between its Layout and Repaint passes.
    sealed class Entry { public int generation; public double builtAt; public List<DiagFinding> findings; }
    static readonly Dictionary<int, Entry> s_cache = new();
    static int s_generation;
    const double TTL = 1.0;   // seconds — max staleness before a Layout pass rebuilds

    static Entry GetOrBuild(UnityEngine.Object element)
    {
        if (element == null) return null;
        int id = element.GetInstanceID();
        double now = EditorApplication.timeSinceStartup;
        bool have = s_cache.TryGetValue(id, out var e);
        bool fresh = have && e.generation == s_generation && (now - e.builtAt) < TTL;
        if (fresh) return e;

        // Only mutate the cache when we're allowed to change the drawn structure: outside GUI, or on
        // a Layout event. During Repaint/mouse events reuse whatever we have (stale is fine) so the
        // IMGUI layout/repaint control counts can't diverge. First-ever build is allowed regardless.
        bool inGui = Event.current != null;
        bool canRebuild = !inGui || Event.current.type == EventType.Layout;
        if (!canRebuild && have) return e;

        var findings = new List<DiagFinding>();
        try { RunValidators(element, findings); }
        catch (Exception ex) { findings.Add(new DiagFinding(DiagSeverity.Warn, "Diagnostics threw: " + ex.Message, "InspectorDiagnostics")); }
        findings.Sort((a, b) => ((int)b.severity).CompareTo((int)a.severity));   // worst first

        e = new Entry { generation = s_generation, builtAt = now, findings = findings };
        s_cache[id] = e;
        return e;
    }

    static void RunValidators(UnityEngine.Object element, List<DiagFinding> sink)
    {
        if (!TryResolve()) return;
        if (t_IDatatableElement == null || !t_IDatatableElement.IsInstanceOfType(element)) return;

        SafeRun(sink, () => CheckUnfilledListRows(element, sink));
        SafeRun(sink, () => CheckUnlockConstructibles(element, sink));

        // Descriptor / DescriptorMapper structural checks (malformed RPN, wrong policy flags) —
        // reuse DescriptorMapperPreview's already-resolved analysis rather than duplicating it.
        SafeRun(sink, () => DescriptorMapperPreview.CollectFindings(element, (sev, msg, src) =>
            sink.Add(new DiagFinding(sev >= 2 ? DiagSeverity.Crash : DiagSeverity.Warn, msg, src))));
    }

    static void SafeRun(List<DiagFinding> sink, Action a)
    {
        try { a(); }
        catch (Exception ex) { sink.Add(new DiagFinding(DiagSeverity.Warn, "A validator threw: " + ex.Message, "InspectorDiagnostics")); }
    }

    // ── Reflection ────────────────────────────────────────────────────────────
    static bool s_resolved, s_resolveFailed;
    static Type t_IDatatableElement, t_Ref, t_UnlockConstr, t_ConstrDef, t_EmpireWide;
    static FieldInfo f_ref_serName;          // DatatableElementReference.serializableElementName (string)
    static FieldInfo f_unlock_constrRefs;    // SimulationEventEffect_UnlockConstructible.ConstructibleReferences (DatatableElementReference[])
    static FieldInfo f_constr_serFamily;     // ConstructibleDefinition.SerializableFamily (string)

    static bool TryResolve()
    {
        if (s_resolved) return !s_resolveFailed;
        s_resolved = true;

        t_IDatatableElement = FindType("Amplitude.Framework.IDatatableElement");
        t_Ref = FindType("Amplitude.Framework.DatatableElementReference");
        t_UnlockConstr = FindType("Amplitude.Mercury.Data.Simulation.SimulationEventEffect_UnlockConstructible");
        t_ConstrDef = FindType("Amplitude.Mercury.Data.Simulation.ConstructibleDefinition");
        t_EmpireWide = FindType("Amplitude.Mercury.Data.Simulation.EmpireWideConstructionParticipationDefinition");

        if (t_Ref != null) f_ref_serName = t_Ref.GetField("serializableElementName", ALL);
        if (t_UnlockConstr != null) f_unlock_constrRefs = t_UnlockConstr.GetField("ConstructibleReferences", ALL);
        if (t_ConstrDef != null) f_constr_serFamily = t_ConstrDef.GetField("SerializableFamily", ALL);

        // The engine still runs with partial resolution; each validator guards its own fields.
        s_resolveFailed = t_IDatatableElement == null;
        return !s_resolveFailed;
    }

    static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        { try { var t = asm.GetType(fullName); if (t != null) return t; } catch { } }
        return null;
    }

    static string RefName(object boxedRef)
        => f_ref_serName == null || boxedRef == null ? null : f_ref_serName.GetValue(boxedRef) as string;

    // ── Validator: unfilled rows in a reference list ──────────────────────────
    // A single reference left at 'none' is usually a legitimately-optional field, so we DON'T flag those
    // (too noisy). But an unfilled ROW inside a list — you added a row and never set it, so it defaults to
    // 'none'/empty — is dereferenced during Initialize and crashes the mod at load. That's the real problem,
    // so it's 🛑. Handles both a list of bare references and a list of struct rows that carry references
    // (a row whose reference(s) are all empty is an added-but-unfilled row).
    static void CheckUnfilledListRows(UnityEngine.Object element, List<DiagFinding> sink)
    {
        if (t_Ref == null || f_ref_serName == null) return;
        foreach (var f in element.GetType().GetFields(ALL))
        {
            if (f.IsStatic || !f.FieldType.IsArray) continue;
            var elemType = f.FieldType.GetElementType();
            if (elemType == null) continue;

            Array arr; try { arr = f.GetValue(element) as Array; } catch { continue; }
            if (arr == null || arr.Length == 0) continue;

            if (elemType == t_Ref)
            {
                for (int i = 0; i < arr.Length; i++)
                    if (string.IsNullOrEmpty(RefName(arr.GetValue(i))))
                        sink.Add(new DiagFinding(DiagSeverity.Crash,
                            $"Unfilled row [{i}] in list '{f.Name}' — an empty ('none') reference in a list is dereferenced at load and crashes the mod. Fill it or remove the row.",
                            "empty list row"));
                continue;
            }

            // Rows that are structs/classes carrying references (e.g. a list of descriptor-reference rows).
            // Only flag a row that's ENTIRELY default — "added but never touched." A row with other data
            // set but an optional reference left at 'none' is legitimate, so we leave it alone (keeps the
            // 🛑 label trustworthy). GetRowRefFields also gates out row types that carry no references.
            if (GetRowRefFields(elemType).Count == 0) continue;
            object defaultRow = elemType.IsValueType ? SafeDefault(elemType) : null;
            for (int i = 0; i < arr.Length; i++)
            {
                object row = arr.GetValue(i);
                if (row == null)
                {
                    if (!elemType.IsValueType)
                        sink.Add(new DiagFinding(DiagSeverity.Crash, $"Null row [{i}] in list '{f.Name}' — a null entry crashes at load. Remove or fill it.", "null list entry"));
                    continue;
                }
                if (elemType.IsValueType && defaultRow != null && SafeEquals(row, defaultRow))
                    sink.Add(new DiagFinding(DiagSeverity.Crash,
                        $"Unfilled row [{i}] in list '{f.Name}' — the row is empty (default, no references set); an unfilled list row crashes at load. Fill it or remove the row.",
                        "empty list row"));
            }
        }
    }

    // Cached: the DatatableElementReference fields declared on a list's row type (empty for row types
    // that don't carry references, so those lists are skipped). Type-structural — never invalidated.
    static readonly Dictionary<Type, List<FieldInfo>> s_rowRefFields = new();
    static readonly List<FieldInfo> s_noFields = new();
    static List<FieldInfo> GetRowRefFields(Type t)
    {
        if (t == null || t == typeof(string) || t.IsPrimitive || t.IsEnum || typeof(UnityEngine.Object).IsAssignableFrom(t))
            return s_noFields;
        if (s_rowRefFields.TryGetValue(t, out var list)) return list;
        list = new List<FieldInfo>();
        try { foreach (var f in t.GetFields(ALL)) if (!f.IsStatic && f.FieldType == t_Ref) list.Add(f); } catch { }
        s_rowRefFields[t] = list;
        return list;
    }

    static object SafeDefault(Type t) { try { return Activator.CreateInstance(t); } catch { return null; } }
    // ValueType.Equals does a reflective field-by-field compare, so a freshly-added default struct row
    // equals a fresh default instance. Guarded because some exotic field types can throw in Equals.
    static bool SafeEquals(object a, object b) { try { return a.Equals(b); } catch { return false; } }

    // ── Validator: unlock-event constructibles (:4713 / :4718 / :4734) ─────────
    // Mirrors DataController.SetConstructibleAsNeededToBeUnlocked over the merged element table: for each
    // SimulationEventEffect_UnlockConstructible, resolve every referenced constructible; the first sets the
    // family baseline, any resolved constructible with a different family is the :4734 crash.
    static void CheckUnlockConstructibles(UnityEngine.Object element, List<DiagFinding> sink)
    {
        if (t_UnlockConstr == null || f_unlock_constrRefs == null || t_ConstrDef == null || f_constr_serFamily == null) return;

        foreach (var effects in EnumerateEventEffectArrays(element))
        {
            if (effects == null) continue;
            for (int e = 0; e < effects.Length; e++)
            {
                var eff = effects.GetValue(e);
                if (eff == null || !t_UnlockConstr.IsInstanceOfType(eff)) continue;
                if (f_unlock_constrRefs.GetValue(eff) is not Array refs) continue;

                string baseline = null; bool baselineSet = false;
                for (int i = 0; i < refs.Length; i++)
                {
                    string name = RefName(refs.GetValue(i));
                    if (string.IsNullOrEmpty(name))
                    {
                        sink.Add(new DiagFinding(DiagSeverity.Crash, $"Unlock event has an empty constructible reference [{i}] — resolves to nothing at load.", "DataController.cs:4713"));
                        continue;
                    }
                    var constr = FindByName(t_ConstrDef, name);
                    if (constr == null)
                    {
                        sink.Add(new DiagFinding(DiagSeverity.Crash, $"Unlock event references constructible '{name}', which isn't in the project or mounted vanilla bundle — \"Constructible not found\" at load.", "DataController.cs:4713"));
                        continue;
                    }
                    if (t_EmpireWide != null && t_EmpireWide.IsInstanceOfType(constr))
                    {
                        sink.Add(new DiagFinding(DiagSeverity.Crash, $"Unlock event targets '{name}', an EmpireWideConstructionParticipationDefinition — illegal unlock target.", "DataController.cs:4718", constr));
                        continue;
                    }
                    string family = f_constr_serFamily.GetValue(constr) as string ?? "";
                    if (!baselineSet) { baseline = family; baselineSet = true; }
                    else if (family != baseline)
                        sink.Add(new DiagFinding(DiagSeverity.Crash,
                            $"Mixed families in one unlock event: '{name}' is family '{family}' but the event's first constructible is family '{baseline}'. All constructibles in one unlock event must share a family (the confirmed ENC+VIP crash).",
                            "DataController.cs:4734", constr));
                }
            }
        }
    }

    // Yields every SimulationEventEffect[] reachable from an element: a direct SimulationEventEffects
    // field, plus one held per entry of a Choices array (Civic / Narrative choices). Bounded, name-based
    // reflection so it works across the definition types that carry unlock events.
    static IEnumerable<Array> EnumerateEventEffectArrays(UnityEngine.Object element)
    {
        var type = element.GetType();
        var direct = type.GetField("SimulationEventEffects", ALL);
        if (direct != null && direct.GetValue(element) is Array a) yield return a;

        var choicesField = type.GetField("Choices", ALL);
        if (choicesField != null && choicesField.GetValue(element) is Array choices)
            foreach (var choice in choices)
            {
                if (choice == null) continue;
                var cf = choice.GetType().GetField("SimulationEventEffects", ALL);
                if (cf != null && cf.GetValue(choice) is Array ca) yield return ca;
            }
    }

    // ── Name index (project + mounted vanilla), cached per generation ─────────
    static readonly Dictionary<Type, Dictionary<string, UnityEngine.Object>> s_nameIndex = new();

    static UnityEngine.Object FindByName(Type type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name)) return null;
        if (!s_nameIndex.TryGetValue(type, out var map))
        {
            map = new Dictionary<string, UnityEngine.Object>();
            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (obj != null && type.IsInstanceOfType(obj) && !map.ContainsKey(obj.name)) map[obj.name] = obj;
            }
            try { foreach (var obj in VanillaDatabaseMount.LoadAllOfType(type)) if (obj != null && !map.ContainsKey(obj.name)) map[obj.name] = obj; }
            catch { }
            s_nameIndex[type] = map;
        }
        return map.TryGetValue(name, out var found) ? found : null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Inspector panel (finishedDefaultHeaderGUI) + inline localization editor
    // ══════════════════════════════════════════════════════════════════════════
    static bool s_foldDiag = true, s_foldLoc = false;

    static InspectorDiagnostics()
    {
        Editor.finishedDefaultHeaderGUI += OnHeaderGUI;
        Undo.undoRedoPerformed += InvalidateAll;
        EditorApplication.projectChanged += InvalidateAll;
    }

    static void OnHeaderGUI(Editor editor)
    {
        if (editor.targets == null || editor.targets.Length != 1) return;
        var target = editor.target;
        if (target == null || !TryResolve()) return;
        if (t_IDatatableElement == null || !t_IDatatableElement.IsInstanceOfType(target)) return;

        var findings = Analyze(target);
        var loc = GetLocModel(target);
        if (findings.Count == 0 && loc.Count == 0) return;

        EditorGUILayout.Space(2);

        if (findings.Count > 0)
        {
            int crash = 0; for (int i = 0; i < findings.Count; i++) if (findings[i].severity == DiagSeverity.Crash) crash++;
            int warn = findings.Count - crash;
            string title = "Diagnostics — " + (crash > 0 ? crash + " will crash" : "") + (crash > 0 && warn > 0 ? ", " : "") + (warn > 0 ? warn + " warning" + (warn > 1 ? "s" : "") : "");
            s_foldDiag = EditorGUILayout.Foldout(s_foldDiag, title, true);
            if (s_foldDiag)
            {
                EditorGUI.indentLevel++;
                foreach (var f in findings)
                {
                    var mt = f.severity == DiagSeverity.Crash ? MessageType.Error : MessageType.Warning;
                    string body = (f.severity == DiagSeverity.Crash ? "WILL CRASH LOAD — " : "") + f.message
                                  + (string.IsNullOrEmpty(f.source) ? "" : "\n[" + f.source + "]");
                    EditorGUILayout.HelpBox(body, mt);
                    if (f.jumpTo != null)
                        if (GUILayout.Button("Select referenced element", EditorStyles.miniButton))
                        { Selection.activeObject = f.jumpTo; EditorGUIUtility.PingObject(f.jumpTo); }
                }
                EditorGUI.indentLevel--;
            }
        }

        if (loc.Count > 0)
        {
            s_foldLoc = EditorGUILayout.Foldout(s_foldLoc, $"Localization ({loc.Count})", true);
            if (s_foldLoc)
            {
                EditorGUI.indentLevel++;
                foreach (var lf in loc) DrawLocField(lf);
                EditorGUI.indentLevel--;
            }
        }
    }

    // ── Inline localization editor ────────────────────────────────────────────
    struct LocField { public string key; public bool imported; public string text; }
    sealed class LocEntry { public int generation; public double builtAt; public List<LocField> fields; }
    static readonly Dictionary<int, LocEntry> s_locCache = new();
    static readonly Dictionary<string, string> s_locEdit = new();   // key -> in-progress edit buffer
    static Dictionary<string, string> s_locDict;                    // %key -> text, per generation

    static Dictionary<string, string> LocDict => s_locDict ??= SafeBuildLocDict();
    static Dictionary<string, string> SafeBuildLocDict()
    { try { return ArchiveTranslations.BuildKeyToTextDict(); } catch { return new Dictionary<string, string>(); } }

    static string ResolveText(string key)
        => key != null && LocDict.TryGetValue(key, out var t) ? t : "";

    static List<LocField> GetLocModel(UnityEngine.Object element)
    {
        int id = element.GetInstanceID();
        double now = EditorApplication.timeSinceStartup;
        bool have = s_locCache.TryGetValue(id, out var e);
        bool fresh = have && e.generation == s_generation && (now - e.builtAt) < TTL;
        if (fresh) return e.fields;

        bool inGui = Event.current != null;
        bool canRebuild = !inGui || Event.current.type == EventType.Layout;
        if (!canRebuild && have) return e.fields;

        var keys = new HashSet<string>();
        try { CollectLocKeys(element, keys, 1); } catch { }
        var fields = new List<LocField>();
        foreach (var key in keys.Take(16))
        {
            bool imported; try { imported = ArchiveTranslations.HasOverride(key); } catch { imported = false; }
            fields.Add(new LocField { key = key, imported = imported, text = ResolveText(key) });
        }
        s_locCache[id] = new LocEntry { generation = s_generation, builtAt = now, fields = fields };
        return fields;
    }

    // Collects %-prefixed localization keys from string fields (top level + one nested level).
    static void CollectLocKeys(object obj, HashSet<string> keys, int depth)
    {
        if (obj == null || depth < 0 || keys.Count >= 16) return;
        foreach (var f in obj.GetType().GetFields(ALL))
        {
            if (f.IsStatic) continue;
            object v; try { v = f.GetValue(obj); } catch { continue; }
            if (v == null) continue;
            if (v is string s) { if (s.Length > 1 && s[0] == '%') keys.Add(s); }
            else if (depth > 0)
            {
                var ft = f.FieldType;
                if (ft.IsClass && ft != typeof(string) && !ft.IsArray && !typeof(UnityEngine.Object).IsAssignableFrom(ft))
                    CollectLocKeys(v, keys, depth - 1);
            }
        }
    }

    static void DrawLocField(LocField lf)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.SelectableLabel(lf.key, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(44)))
            EditorGUIUtility.systemCopyBuffer = lf.key;
        EditorGUILayout.EndHorizontal();

        if (!lf.imported)
        {
            EditorGUILayout.LabelField(string.IsNullOrEmpty(lf.text) ? "(vanilla / unresolved)" : lf.text, EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Import for editing", EditorStyles.miniButton, GUILayout.Width(140)))
            {
                if (ArchiveTranslations.EnsureOverride(lf.key, lf.text) != null) { s_locEdit.Remove(lf.key); InvalidateAll(); }
            }
            EditorGUILayout.Space(3);
            return;
        }

        if (!s_locEdit.TryGetValue(lf.key, out var buf)) { buf = lf.text; s_locEdit[lf.key] = buf; }
        EditorGUI.BeginChangeCheck();
        string edited = EditorGUILayout.TextArea(buf, EditorStyles.textArea, GUILayout.MinHeight(34));
        if (EditorGUI.EndChangeCheck())
        {
            s_locEdit[lf.key] = edited;
            try { ArchiveTranslations.SetOverrideText(lf.key, edited); } catch { }
        }
        EditorGUILayout.Space(3);
    }
}
