using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector.Editor;
using Amplitude.Framework.Simulation;              // Operation
using Amplitude.Framework.Editor.Simulation.Rpn;   // RpnTextCompiler, CompilerStatus
using Amplitude.Mercury.Production;                // PropertyEffectPropertyDrawer (Source/Target type context)
using Amplitude.Mercury.Production.Extensions;     // InspectorProperty.SerializeArray
#endif

/// <summary>
/// Inspector drawer for <see cref="Amplitude.Framework.Simulation.Description.PropertyEffect"/>.
/// Draws Amplitude's own PropertyEffect editor untouched (compact formula field, operation/property
/// dropdowns, and the type-aware Source/Target property browser), then appends the one thing Amplitude
/// doesn't provide: an "In-game render" HelpBox showing how the formula renders in-game (names resolved
/// through PropertyMapper, constant formatted signed/percent) with any applicable warnings. Property
/// selection/autocomplete is left to Amplitude's formula field, which already knows the valid properties.
///
/// IMPORTANT — why this is an Odin drawer, not a [CustomPropertyDrawer]:
/// This project runs Odin Inspector with InspectorConfig.defaultEditorBehaviour = 11
/// (UserTypes | PluginTypes | OtherTypes), so Odin OWNS the Descriptor / DescriptorMapper inspector body.
/// Odin draws PropertyEffect through its own drawer chain and does NOT invoke a type-targeted Unity
/// [CustomPropertyDrawer] for it — so a PropertyDrawer's OnGUI simply never runs here (that is why the
/// previous implementation's intellisense + inline render never appeared, while the header-hooked
/// DescriptorMapperPreview panel — a different seam, Editor.finishedDefaultHeaderGUI — did). Odin
/// auto-discovers OdinValueDrawer&lt;T&gt; subclasses, so this one actually executes inside the Odin
/// inspector. The Unity PropertyDrawer is kept below under #else purely as a fallback for a
/// no-Odin configuration.
///
/// This calls CallNextDrawer so Amplitude's editor draws exactly as it normally would, then adds the
/// render HelpBox after it. The inline render is suppressed when the DescriptorMapperPreview "Enabled"
/// toggle is off.
/// </summary>
#if ODIN_INSPECTOR
public class PropertyEffectOdinDrawer : OdinValueDrawer<Amplitude.Framework.Simulation.Description.PropertyEffect>
{
    // Inline-render cache: rebuilt only on a Layout event (or outside GUI) and when the
    // DescriptorMapperPreview generation bumps, so we don't recompute (and possibly touch the
    // translation dictionary) on every repaint.
    double _renderBuiltAt = -1;
    int _renderGen = -1;
    string _renderText = "";

    protected override void DrawPropertyLayout(GUIContent label)
    {
        // Consume a click on last frame's floating suggestion dropdown BEFORE Amplitude's own controls
        // (the note field it overlaps) can steal the mousedown.
        HandleOverlayClick();
        // Amplitude's own PropertyEffect drawer (compact formula editor + dropdowns) untouched…
        CallNextDrawer(label);
        // …then: (A) compute the floating autocomplete (needs the field rect), (B) our backup additive
        // field (collapsed), and the in-game render preview…
        ComputeFormulaOverlay();
        DrawAutocompleteFormula();
        DrawInlineRender();
        // …and finally paint the dropdown LAST so nothing overdraws it (top layer).
        DrawFormulaOverlayVisuals();
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  (A) Autocomplete ON Amplitude's own formula field
    //  Amplitude draws the formula as EditorGUILayout.TextArea (transparent) under a highlighted overlay,
    //  named "{drawerHashCode}.{PropertyEffect.FullName}.FormulaTextArea". We find that drawer instance in
    //  this property's Odin drawer chain to reconstruct the exact control name (scopes to THIS element),
    //  detect when it's focused, read its caret/text from Unity's internal recycled TextEditor (reflection
    //  — acceptable here: the game is EOL so the editor won't change), and write completions straight into
    //  that editor so Amplitude's own Parse/Compile picks them up.
    // ════════════════════════════════════════════════════════════════════════════
    readonly List<(int start, int len, string text)> _ovItems = new();
    Rect _ovRect;   // floating dropdown rect (0 when hidden); also used by HandleOverlayClick

    // Compute the suggestions + the dropdown rect (anchored under Amplitude's formula field). No drawing —
    // the visuals are painted last (DrawFormulaOverlayVisuals) so nothing overdraws them.
    void ComputeFormulaOverlay()
    {
        try
        {
            _ovRect = Rect.zero;
            if (!PropertyEffectPropertyDrawer.SourceType.TryGetValue(out Type src) || src == null) { _ovItems.Clear(); return; }
            PropertyEffectPropertyDrawer.TargetType.TryGetValue(out Type tgt);

            string focused = GUI.GetNameOfFocusedControl();
            string expected = ExpectedFormulaControlName();
            bool formulaFocused = expected != null
                ? focused == expected
                : !string.IsNullOrEmpty(focused) && focused.EndsWith(".FormulaTextArea", StringComparison.Ordinal);

            var te = formulaFocused ? ActiveRecycledEditor() : null;
            if (Event.current.type == EventType.Layout)
            {
                _ovItems.Clear();
                if (te != null) ComputeSuggestions(te.text ?? "", te.cursorIndex, src, tgt, _ovItems);
            }
            if (_ovItems.Count == 0 || te == null || te.position.width < 2f) return;

            float rowH = EditorGUIUtility.singleLineHeight;
            var field = te.position;
            _ovRect = new Rect(field.x, field.yMax + 1f, Mathf.Max(field.width, 200f), _ovItems.Count * rowH + 2f);
        }
        catch { _ovRect = Rect.zero; }
    }

    static readonly Color OvBackDark = new Color(0.22f, 0.22f, 0.22f, 1f);
    static readonly Color OvBackLight = new Color(0.78f, 0.78f, 0.78f, 1f);
    static readonly Color OvBorder = new Color(0f, 0f, 0f, 0.6f);
    static readonly Color OvHover = new Color(0.3f, 0.5f, 0.9f, 0.45f);

    void DrawFormulaOverlayVisuals()
    {
        if (Event.current.type != EventType.Repaint || _ovRect.width < 2f || _ovItems.Count == 0) return;
        // Opaque fill + border so the note/error underneath don't bleed through; drawn last = top layer.
        EditorGUI.DrawRect(_ovRect, EditorGUIUtility.isProSkin ? OvBackDark : OvBackLight);
        DrawBorder(_ovRect, OvBorder);
        float rowH = EditorGUIUtility.singleLineHeight;
        var mouse = Event.current.mousePosition;
        for (int i = 0; i < _ovItems.Count; i++)
        {
            var r = new Rect(_ovRect.x + 1f, _ovRect.y + 1f + i * rowH, _ovRect.width - 2f, rowH);
            if (r.Contains(mouse)) EditorGUI.DrawRect(r, OvHover);
            GUI.Label(r, _ovItems[i].text, AcStyle);
        }
    }

    static void DrawBorder(Rect r, Color c)
    {
        EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1f), c);
        EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), c);
        EditorGUI.DrawRect(new Rect(r.x, r.y, 1f, r.height), c);
        EditorGUI.DrawRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), c);
    }

    // Runs before CallNextDrawer: if a click lands in last frame's dropdown, apply it and swallow the event
    // so Amplitude's overlapped note field never sees the mousedown.
    void HandleOverlayClick()
    {
        var e = Event.current;
        if (e.type != EventType.MouseDown || e.button != 0 || _ovItems.Count == 0 || _ovRect.width < 2f) return;
        if (!_ovRect.Contains(e.mousePosition)) return;
        int idx = Mathf.FloorToInt((e.mousePosition.y - (_ovRect.y + 1f)) / EditorGUIUtility.singleLineHeight);
        if (idx >= 0 && idx < _ovItems.Count)
        {
            var (start, len, text) = _ovItems[idx];
            InsertIntoFormula(start, len, text, ExpectedFormulaControlName());
            e.Use();
        }
    }

    // Amplitude's own PropertyEffectPropertyDrawer instance for THIS element (its sibling in our drawer
    // chain). We use it both to build the exact control name and to drive its compiler on insert.
    PropertyEffectPropertyDrawer FindAmpDrawer()
    {
        try
        {
            foreach (var d in Property.GetActiveDrawerChain().BakedDrawerArray)
                if (d is PropertyEffectPropertyDrawer amp) return amp;
        }
        catch { }
        return null;
    }

    string ExpectedFormulaControlName()
    {
        var amp = FindAmpDrawer();
        return amp != null
            ? amp.GetHashCode() + "." + typeof(Amplitude.Framework.Simulation.Description.PropertyEffect).FullName + ".FormulaTextArea"
            : null;
    }

    // Reflected access to PropertyEffectPropertyDrawer's private compiler + Compile() so we can re-sync it
    // after an external insert (its EndChangeCheck won't fire for our edit): Parse() updates its text +
    // highlight, and Compile() writes the RPN arrays when the formula is valid.
    static bool s_ampReflected;
    static FieldInfo s_ampCompiler;
    static MethodInfo s_ampCompile;
    static void ReflectAmp()
    {
        if (s_ampReflected) return;
        s_ampReflected = true;
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var t = typeof(PropertyEffectPropertyDrawer);
        s_ampCompiler = t.GetField("compiler", F);
        s_ampCompile = t.GetMethod("Compile", F, null, Type.EmptyTypes, null);
    }

    // Unity's active editor text field state lives in an internal static TextEditor on EditorGUI. Locate it
    // once by scanning for a TextEditor-typed static field (robust across the known field names).
    static FieldInfo s_recycled;
    static bool s_recycledSearched;
    static TextEditor ActiveRecycledEditor()
    {
        if (!s_recycledSearched)
        {
            s_recycledSearched = true;
            const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            foreach (var name in new[] { "s_RecycledEditor", "activeEditor" })
            {
                var f = typeof(EditorGUI).GetField(name, F);
                if (f != null && typeof(TextEditor).IsAssignableFrom(f.FieldType)) { s_recycled = f; break; }
            }
            if (s_recycled == null)
                foreach (var f in typeof(EditorGUI).GetFields(F))
                    if (typeof(TextEditor).IsAssignableFrom(f.FieldType)) { s_recycled = f; break; }
        }
        return s_recycled?.GetValue(null) as TextEditor;
    }

    void InsertIntoFormula(int start, int len, string completion, string ctrlName)
    {
        // Re-fetch (not the captured ref): the button click steals keyboard focus, but the shared recycled
        // editor still holds the formula's text/caret until another text field is focused.
        var te = ActiveRecycledEditor();
        if (te == null) return;
        string t = te.text ?? "";
        start = Mathf.Clamp(start, 0, t.Length);
        len = Mathf.Clamp(len, 0, t.Length - start);
        string nt = t.Substring(0, start) + completion + t.Substring(start + len);
        te.text = nt;
        te.cursorIndex = te.selectIndex = Mathf.Clamp(start + completion.Length, 0, nt.Length);

        // Our edit didn't go through Amplitude's TextArea, so its EndChangeCheck never fired. Drive its OWN
        // compiler: Parse() sets ControlValue + the highlight tokens for ANY text (so the field shows our
        // insert and never reverts — even mid-completion like "5 + Source."), and only when it's a valid,
        // complete formula do we Compile() to write the RPN arrays. We deliberately do NOT call Decompile()
        // (it rebuilds the text from the old arrays and would wipe an incomplete insert).
        var amp = FindAmpDrawer();
        ReflectAmp();
        if (amp != null && s_ampCompiler?.GetValue(amp) is RpnTextCompiler comp)
        {
            try
            {
                if (comp.Parse(nt) == CompilerStatus.Ok && comp.Validate() == CompilerStatus.Ok)
                    s_ampCompile?.Invoke(amp, null);
            }
            catch { }
        }
        // Suggestions rebuild on the next Layout (moved caret); don't clear here (control-count safety).
        if (ctrlName != null) EditorGUI.FocusTextInControl(ctrlName);   // keep editing the formula field
        GUI.changed = true;
    }

    // Shared token→completions: after "Source."/"Target." suggest that type's properties; a bare word
    // suggests the keywords. Fills outItems with (replaceStart, replaceLen, completion).
    static void ComputeSuggestions(string text, int caret, Type src, Type tgt, List<(int, int, string)> outItems)
    {
        if (string.IsNullOrEmpty(text)) return;
        caret = Mathf.Clamp(caret, 0, text.Length);
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_' || text[start - 1] == '.')) start--;
        string token = text.Substring(start, caret - start);
        if (token.Length == 0) return;

        int dot = token.LastIndexOf('.');
        IEnumerable<string> matches; int rs, rl;
        if (dot >= 0)
        {
            string keyword = token.Substring(0, dot), partial = token.Substring(dot + 1);
            Type ty = keyword == "Source" ? src : keyword == "Target" ? tgt : null;
            if (ty == null) return;   // World/Variable not resolved in v1
            rs = start + dot + 1; rl = partial.Length;
            matches = RpnTextCompiler.GetTypeFieldLabels(ty)?
                .Where(n => !string.IsNullOrEmpty(n) && n.IndexOf(partial, StringComparison.OrdinalIgnoreCase) >= 0 && n != partial)
                .OrderBy(n => n.StartsWith(partial, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>();
        }
        else
        {
            rs = start; rl = token.Length;
            matches = new[] { "Source.", "Target.", "World." }.Where(k => k.StartsWith(token, StringComparison.OrdinalIgnoreCase) && k != token);
        }
        foreach (var m in matches.Take(8)) outItems.Add((rs, rl, m));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Autocomplete formula field (v1)
    //  Reuses Amplitude's OWN compiler (RpnTextCompiler) for parse/validate/compile/decompile, so the
    //  grammar/precedence is never reimplemented and — because we only ever write after Parse()==Ok &&
    //  Validate()==Ok — it is impossible to write invalid RPN. Source/Target types come from the same
    //  GlobalContextValues that Amplitude's PropertyEffectPropertyDrawer reads (published by the
    //  enclosing EffectPropertyDrawer during this pass). Suggestions come from RpnTextCompiler
    //  .GetTypeFieldLabels(type). Additive & collapsed by default; Amplitude's field stays authoritative.
    // ════════════════════════════════════════════════════════════════════════════
    RpnTextCompiler _compiler;
    Type _cSrc, _cTgt;
    string _formula;            // editable text buffer (null => (re)seed from current RPN)
    bool _formulaOk;
    string _formulaError;
    bool _acFold;               // collapsed by default
    bool _acDirty;              // text changed → re-parse on next Layout
    string _acCtrl;
    readonly List<(int start, int len, string text)> _acItems = new();   // rebuilt on Layout only

    void DrawAutocompleteFormula()
    {
        try
        {
            // Types come from Amplitude's context; if absent (element drawn outside an Effect), skip.
            if (!PropertyEffectPropertyDrawer.SourceType.TryGetValue(out Type src) || src == null) return;
            PropertyEffectPropertyDrawer.TargetType.TryGetValue(out Type tgt);

            _acFold = EditorGUILayout.Foldout(_acFold, "Formula (autocomplete)", true);
            if (!_acFold) return;
            EditorGUI.indentLevel++;
            try
            {
                if (_compiler == null || _cSrc != src || _cTgt != tgt)
                {
                    _compiler = new RpnTextCompiler();
                    _compiler.SetSourceType(src);
                    if (tgt != null) _compiler.SetTargetType(tgt);
                    _cSrc = src; _cTgt = tgt; _formula = null;
                }
                if (_formula == null) { _formula = SeedFromRpn(src, tgt); _acDirty = true; }
                _acCtrl ??= "peac$" + Property.Path;

                GUI.SetNextControlName(_acCtrl);
                EditorGUI.BeginChangeCheck();
                // GUILayout.TextField (NOT EditorGUILayout.TextField): the runtime control keeps its
                // TextEditor at GUIUtility.GetStateObject(typeof(TextEditor), controlID), so we can read
                // the real caret/text. The editor variant uses an internal recycled editor we can't reach.
                string txt = GUILayout.TextField(_formula, EditorStyles.textField);
                if (EditorGUI.EndChangeCheck()) { _formula = txt; _acDirty = true; }

                bool focused = GUI.GetNameOfFocusedControl() == _acCtrl;
                // All count-affecting state (validation error box, suggestion list) is recomputed ONLY on
                // Layout, so the control count is identical on Layout and Repaint (else IMGUI blanks out).
                if (Event.current.type == EventType.Layout)
                {
                    if (_acDirty)
                    {
                        _formulaOk = _compiler.Parse(_formula) == CompilerStatus.Ok && _compiler.Validate() == CompilerStatus.Ok;
                        _formulaError = _formulaOk ? null : _compiler.StatusMessage;
                        _acDirty = false;
                    }
                    RebuildSuggestions(focused, src, tgt);
                }
                DrawSuggestionList();

                using (new EditorGUI.DisabledScope(!_formulaOk))
                    if (GUILayout.Button("Apply to RPN", EditorStyles.miniButton)) ApplyFormula(src, tgt);
                if (GUILayout.Button("Reload from RPN", EditorStyles.miniButton)) _formula = null;

                if (_formulaError != null) EditorGUILayout.HelpBox(_formulaError, MessageType.Warning);
            }
            finally { EditorGUI.indentLevel--; }
        }
        catch (Exception e) { EditorGUILayout.HelpBox("autocomplete error: " + e.Message, MessageType.None); }
    }

    string SeedFromRpn(Type src, Type tgt)
    {
        var rpn = ChildArray<Operation>("RpnOperationStack");
        var cst = ChildArray<Amplitude.FixedPoint>("ConstantStack");
        var pln = ChildArray<string>("PropertyLocalName");
        _compiler.Decompile(src, tgt, rpn, cst, pln, null, null);
        return _compiler.ControlValue ?? "";
    }

    void ApplyFormula(Type src, Type tgt)
    {
        if (_compiler.Parse(_formula) != CompilerStatus.Ok || _compiler.Validate() != CompilerStatus.Ok) return;
        Operation[] rpn = null; Amplitude.FixedPoint[] cst = null; string[] pln = null, vars = null;
        Type s = src, t = tgt;
        _compiler.Compile(ref s, ref t, ref rpn, ref cst, ref pln, ref vars);
        Property.Children["RpnOperationStack"].SerializeArray(ref rpn);
        Property.Children["ConstantStack"].SerializeArray(ref cst);
        Property.Children["PropertyLocalName"].SerializeArray(ref pln);
        GUI.FocusControl(null);
    }

    T[] ChildArray<T>(string name) => Property.Children[name]?.ValueEntry?.WeakSmartValue as T[] ?? Array.Empty<T>();

    // Token under the caret → candidate completions. Two contexts: after "Keyword." suggest that type's
    // properties; a bare word suggests the keywords. Computed on Layout only so the control count matches
    // between Layout/Repaint (avoids the IMGUI control-id mismatch that would blank the inspector).
    void RebuildSuggestions(bool focused, Type src, Type tgt)
    {
        _acItems.Clear();
        if (!focused || string.IsNullOrEmpty(_formula)) return;
        var te = GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) as TextEditor;
        int caret = te != null ? te.cursorIndex : _formula.Length;
        ComputeSuggestions(_formula, caret, src, tgt, _acItems);
    }

    void DrawSuggestionList()
    {
        if (_acItems.Count == 0) return;
        EditorGUI.indentLevel++;
        foreach (var (start, len, text) in _acItems)
        {
            var r = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight));
            if (GUI.Button(r, text, AcStyle)) InsertCompletion(start, len, text);
        }
        EditorGUI.indentLevel--;
    }

    void InsertCompletion(int start, int len, string completion)
    {
        start = Mathf.Clamp(start, 0, _formula.Length);
        len = Mathf.Clamp(len, 0, _formula.Length - start);
        _formula = _formula.Substring(0, start) + completion + _formula.Substring(start + len);
        int caret = start + completion.Length;
        var te = GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) as TextEditor;
        if (te != null) { te.text = _formula; te.cursorIndex = te.selectIndex = Mathf.Clamp(caret, 0, _formula.Length); }
        // Re-parse and rebuild the suggestion list on the next Layout (not here), so the control count
        // stays consistent within this event pass.
        _acDirty = true;
    }

    static GUIStyle s_acStyle;
    static GUIStyle AcStyle => s_acStyle ??= new GUIStyle(EditorStyles.miniButton) { alignment = TextAnchor.MiddleLeft };

    // ── Inline in-game render ───────────────────────────────────────────────────
    void DrawInlineRender()
    {
        string text = GetRenderTextCached();
        if (string.IsNullOrEmpty(text)) return;
        EditorGUILayout.HelpBox(text, text.Contains("⚠") ? MessageType.Warning : MessageType.Info);
    }

    string GetRenderTextCached()
    {
        int gen = DescriptorMapperPreview.Generation;
        double now = EditorApplication.timeSinceStartup;
        if (_renderGen == gen && (now - _renderBuiltAt) < 1.0) return _renderText;

        bool canRebuild = Event.current == null || Event.current.type == EventType.Layout;
        if (!canRebuild) return _renderText;

        _renderText = ComputeRenderText(Root, EffectIndex, PeIndex);
        _renderGen = gen;
        _renderBuiltAt = now;
        return _renderText;
    }

    // ── Odin tree → (root object, effect index, property-effect index) ──────────
    // PropertyEffect element -> parent is the PropertyEffects collection -> its parent is the Effect
    // element (whose Index is the effect index within Effects[]).
    int PeIndex => Property.Index;
    int EffectIndex => Property.Parent?.Parent?.Index ?? -1;
    UnityEngine.Object Root =>
        Property.Tree?.WeakTargets != null && Property.Tree.WeakTargets.Count > 0
            ? Property.Tree.WeakTargets[0] as UnityEngine.Object
            : null;

    static string ComputeRenderText(UnityEngine.Object root, int ei, int pi)
    {
        try
        {
            if (!DescriptorMapperPreview.IsActive) return "";
            if (root == null || ei < 0 || pi < 0) return "";
            var res = DescriptorMapperPreview.ResolvePropertyEffect(root, ei, pi);
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(res.resolvedFormula)) lines.Add("In-game render:  " + res.resolvedFormula);
            if (res.warnings != null) foreach (var w in res.warnings) lines.Add("⚠ " + w);
            return string.Join("\n", lines);
        }
        catch (Exception e) { return "render error: " + e.Message; }
    }
}
#else
[CustomPropertyDrawer(typeof(Amplitude.Framework.Simulation.Description.PropertyEffect))]
public class PropertyEffectDrawer : PropertyDrawer
{
    const float PICK_BTN_W = 22f;
    const double TTL = 1.0;
    static readonly Regex s_indexRegex = new(@"Array\.data\[(\d+)\]", RegexOptions.Compiled);

    static float Line => EditorGUIUtility.singleLineHeight;
    static float Spacing => EditorGUIUtility.standardVerticalSpacing;

    // ── Height ────────────────────────────────────────────────────────────────
    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float h = Line; // foldout header
        if (property.isExpanded)
        {
            h += Spacing + ChildHeight(property, "Note");
            h += Spacing + Line;                                // TargetProperty (autocomplete)
            h += Spacing + ChildHeight(property, "ToTargetOperation");
            h += Spacing + ChildHeight(property, "RpnOperationStack");
            h += Spacing + ChildHeight(property, "ConstantStack");
            h += Spacing + LocalNamesHeight(property);          // PropertyLocalName (autocomplete list)
        }
        float rh = RenderHeight(property);
        if (rh > 0f) h += Spacing + rh;
        return h;
    }

    static float ChildHeight(SerializedProperty property, string name)
    {
        var p = property.FindPropertyRelative(name);
        return p != null ? EditorGUI.GetPropertyHeight(p, true) : 0f;
    }

    static float LocalNamesHeight(SerializedProperty property)
    {
        var arr = property.FindPropertyRelative("PropertyLocalName");
        if (arr == null) return 0f;
        float h = Line; // header foldout
        if (arr.isExpanded)
        {
            h += Spacing + Line;                                // size field
            for (int i = 0; i < arr.arraySize; i++) h += Spacing + Line;
        }
        return h;
    }

    static float RenderHeight(SerializedProperty property)
    {
        string text = GetRenderText(property);
        if (string.IsNullOrEmpty(text)) return 0f;
        return Mathf.Max(EditorStyles.helpBox.CalcHeight(new GUIContent(text), RenderWidth()), 3 * Line);
    }

    // Conservative (narrow) width so CalcHeight over-estimates a little — avoids clipping the HelpBox.
    static float RenderWidth() => Mathf.Max(EditorGUIUtility.currentViewWidth - 60f, 120f);

    // ── Draw ──────────────────────────────────────────────────────────────────
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        float y = position.y;
        var headerRect = new Rect(position.x, y, position.width, Line);
        property.isExpanded = EditorGUI.Foldout(headerRect, property.isExpanded, label, true);
        y += Line;

        if (property.isExpanded)
        {
            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel++;

            y += Spacing; DrawChild(ref y, position, property, "Note");
            y += Spacing; DrawAutocomplete(ref y, position, property.FindPropertyRelative("TargetProperty"), new GUIContent("Target Property"));
            y += Spacing; DrawChild(ref y, position, property, "ToTargetOperation");
            y += Spacing; DrawChild(ref y, position, property, "RpnOperationStack");
            y += Spacing; DrawChild(ref y, position, property, "ConstantStack");
            y += Spacing; DrawLocalNames(ref y, position, property);

            EditorGUI.indentLevel = indent;
        }

        string text = GetRenderText(property);
        if (!string.IsNullOrEmpty(text))
        {
            y += Spacing;
            float rh = Mathf.Max(EditorStyles.helpBox.CalcHeight(new GUIContent(text), RenderWidth()), 3 * Line);
            var rr = new Rect(position.x, y, position.width, rh);
            EditorGUI.HelpBox(rr, text, text.Contains("⚠") ? MessageType.Warning : MessageType.Info);
        }
    }

    static void DrawChild(ref float y, Rect position, SerializedProperty property, string name)
    {
        var p = property.FindPropertyRelative(name);
        if (p == null) return;
        float ph = EditorGUI.GetPropertyHeight(p, true);
        EditorGUI.PropertyField(new Rect(position.x, y, position.width, ph), p, true);
        y += ph;
    }

    // Text field + a ▼ button that opens a searchable dropdown of all PropertyMapper names.
    static void DrawAutocomplete(ref float y, Rect position, SerializedProperty strProp, GUIContent label)
    {
        if (strProp == null) return;
        var fieldRect = new Rect(position.x, y, position.width - PICK_BTN_W - 2f, Line);
        var btnRect = new Rect(position.x + position.width - PICK_BTN_W, y, PICK_BTN_W, Line);
        EditorGUI.PropertyField(fieldRect, strProp, label);
        if (GUI.Button(btnRect, "▼", EditorStyles.miniButton))
            ShowPicker(btnRect, strProp);
        y += Line;
    }

    static void DrawLocalNames(ref float y, Rect position, SerializedProperty property)
    {
        var arr = property.FindPropertyRelative("PropertyLocalName");
        if (arr == null) return;

        var hr = new Rect(position.x, y, position.width, Line);
        var foldRect = new Rect(hr.x, hr.y, hr.width - 44f, hr.height);
        arr.isExpanded = EditorGUI.Foldout(foldRect, arr.isExpanded, $"Property Local Name ({arr.arraySize})", true);
        if (GUI.Button(new Rect(hr.xMax - 42f, hr.y, 20f, hr.height), "+", EditorStyles.miniButton)) arr.arraySize++;
        if (GUI.Button(new Rect(hr.xMax - 20f, hr.y, 20f, hr.height), "-", EditorStyles.miniButton) && arr.arraySize > 0) arr.arraySize--;
        y += Line;

        if (arr.isExpanded)
        {
            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel++;

            y += Spacing;
            int newSize = EditorGUI.IntField(new Rect(position.x, y, position.width, Line), "Size", arr.arraySize);
            if (newSize >= 0 && newSize != arr.arraySize) arr.arraySize = newSize;
            y += Line;

            for (int i = 0; i < arr.arraySize; i++)
            {
                y += Spacing;
                DrawAutocomplete(ref y, position, arr.GetArrayElementAtIndex(i), new GUIContent($"  Element {i}"));
            }

            EditorGUI.indentLevel = indent;
        }
    }

    static void ShowPicker(Rect rect, SerializedProperty strProp)
    {
        var so = strProp.serializedObject;
        string path = strProp.propertyPath;
        var names = DescriptorMapperPreview.GetAvailablePropertyNames();
        var dd = new PropertyNameDropdown(new AdvancedDropdownState(), names, picked =>
        {
            var p = so.FindProperty(path);
            if (p != null) { p.stringValue = picked; so.ApplyModifiedProperties(); }
        });
        dd.Show(rect);
    }

    // ── Render text (cached; only rebuilt on Layout or outside GUI, TTL-refreshed) ──
    sealed class RenderCache { public int generation; public double builtAt; public string text; }
    static readonly Dictionary<string, RenderCache> s_render = new();

    static string GetRenderText(SerializedProperty property)
    {
        var target = property.serializedObject.targetObject;
        string key = (target != null ? target.GetInstanceID() : 0) + ":" + property.propertyPath;
        int gen = DescriptorMapperPreview.Generation;
        double now = EditorApplication.timeSinceStartup;

        bool have = s_render.TryGetValue(key, out var c);
        if (have && c.generation == gen && (now - c.builtAt) < TTL) return c.text;

        bool canRebuild = Event.current == null || Event.current.type == EventType.Layout;
        if (!canRebuild && have) return c.text;

        string text = ComputeRenderText(property);
        if (s_render.Count > 512) s_render.Clear();
        s_render[key] = new RenderCache { generation = gen, builtAt = now, text = text };
        return text;
    }

    static string ComputeRenderText(SerializedProperty property)
    {
        try
        {
            if (!DescriptorMapperPreview.IsActive) return "";
            var target = property.serializedObject.targetObject;
            if (target == null) return "";

            var matches = s_indexRegex.Matches(property.propertyPath);
            int ei = -1, pi = -1;
            if (matches.Count >= 1) int.TryParse(matches[0].Groups[1].Value, out ei);
            if (matches.Count >= 2) int.TryParse(matches[1].Groups[1].Value, out pi);
            if (ei < 0 || pi < 0) return "";

            var res = DescriptorMapperPreview.ResolvePropertyEffect(target, ei, pi);
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(res.resolvedFormula)) lines.Add("In-game render:  " + res.resolvedFormula);
            if (res.warnings != null) foreach (var w in res.warnings) lines.Add("⚠ " + w);
            return string.Join("\n", lines);
        }
        catch (Exception e) { return "render error: " + e.Message; }
    }
}
#endif

/// <summary>Searchable dropdown of PropertyMapper names for the PropertyEffect intellisense button.</summary>
class PropertyNameDropdown : AdvancedDropdown
{
    readonly IList<string> _names;
    readonly Action<string> _onPick;

    public PropertyNameDropdown(AdvancedDropdownState state, IList<string> names, Action<string> onPick) : base(state)
    {
        _names = names;
        _onPick = onPick;
        minimumSize = new Vector2(260, 340);
    }

    protected override AdvancedDropdownItem BuildRoot()
    {
        var root = new AdvancedDropdownItem("Property");
        if (_names != null)
            foreach (var n in _names)
                root.AddChild(new AdvancedDropdownItem(n));
        return root;
    }

    protected override void ItemSelected(AdvancedDropdownItem item) => _onPick?.Invoke(item.name);
}
