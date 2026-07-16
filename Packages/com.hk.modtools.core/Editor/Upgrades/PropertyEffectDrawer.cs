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
using Amplitude.Framework.Simulation.Description;  // Effect, PropertyEffect
using Amplitude.Framework.Editor.Simulation.Rpn;   // RpnTextCompiler, CompilerStatus
using Amplitude.Mercury.Production;                // PropertyEffectPropertyDrawer (Source/Target type context)
using Amplitude.Mercury.Production.Extensions;     // InspectorProperty.SerializeArray
#endif

/// <summary>
/// Inspector drawer for <see cref="Amplitude.Framework.Simulation.Description.PropertyEffect"/>.
/// Draws Amplitude's own PropertyEffect editor untouched (compact formula field, operation/property
/// dropdowns, and the type-aware Source/Target property browser), then appends the one thing Amplitude
/// doesn't provide: a "Rendered" HelpBox showing the same tooltip line the header preview computes
/// (template with ordered params substituted) with any applicable warnings. Property
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
        HandleSuggestionKeyboard();
        HandleOverlayClickEarly();
        CallNextDrawer(label);
        ComputeFormulaOverlay();
        DrawAutocompleteFormula();
        DrawInlineRender();
        DrawFormulaOverlayVisuals();
        DrawAcSuggestionPopupVisuals();
    }

    void OnOverlayPick(int index)
    {
        if (FormulaSuggestionOverlaySession.Kind == FormulaSuggestionOverlaySession.OverlayKind.Amp)
        {
            var (start, len, text) = FormulaSuggestionOverlaySession.GetItem(index);
            InsertIntoFormula(start, len, text, ExpectedFormulaControlName());
        }
        else
        {
            var (start, len, text) = FormulaSuggestionOverlaySession.GetItem(index);
            if (_acCtrl != null) EditorGUI.FocusTextInControl(_acCtrl);
            InsertCompletion(start, len, text);
        }
        RepaintFocusedWindow();
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
    Rect _ovAnchor;     // Amplitude formula field rect (anchor for the popup)
    Rect _ovRect;       // floating dropdown rect; used by HandleOverlayClick + drawing
    Vector2 _ovScroll;
    int _ovSel = -1;
    string _ovHeader = "";
    string _ovFilterKey = "";
    bool _ovScrollResetThisLayout;
    int _formulaControlId;

    // Compute the suggestions + the dropdown rect (anchored under Amplitude's formula field). No drawing —
    // the visuals are painted last (DrawFormulaOverlayVisuals) so nothing overdraws them.
    void ComputeFormulaOverlay()
    {
        try
        {
            _ovRect = Rect.zero;
            if (!PropertyEffectPropertyDrawer.SourceType.TryGetValue(out Type src) || src == null)
            {
                _ovItems.Clear();
                _ovAnchor = Rect.zero;
                return;
            }
            PropertyEffectPropertyDrawer.TargetType.TryGetValue(out Type tgt);

            string focused = GUI.GetNameOfFocusedControl();
            string expected = ExpectedFormulaControlName();
            bool formulaFocused = expected != null
                ? focused == expected
                : !string.IsNullOrEmpty(focused) && focused.EndsWith(".FormulaTextArea", StringComparison.Ordinal);

            var te = formulaFocused ? ActiveRecycledEditor() : null;
            if (Event.current.type == EventType.Layout)
            {
                if (te != null)
                {
                    string text = te.text ?? "";
                    int caret = te.cursorIndex;
                    _ovItems.Clear();
                    ComputeSuggestions(text, caret, src, tgt, _ovItems);
                    _ovHeader = SuggestionHeader(text, caret);
                    string filterKey = SuggestionFilterKey(text, caret);
                    if (filterKey != _ovFilterKey)
                    {
                        _ovFilterKey = filterKey;
                        _ovSel = _ovItems.Count > 0 ? 0 : -1;
                        _ovScroll = Vector2.zero;
                        _ovScrollResetThisLayout = true;
                    }
                    else
                        _ovSel = _ovItems.Count > 0 ? Mathf.Clamp(_ovSel, 0, _ovItems.Count - 1) : -1;
                    _formulaControlId = GUIUtility.keyboardControl;
                }
                else
                {
                    _ovItems.Clear();
                    _ovFilterKey = "";
                    _formulaControlId = 0;
                    _ovAnchor = Rect.zero;
                }
            }
            if (_ovItems.Count == 0 || te == null || te.position.width < 2f) return;

            _ovAnchor = te.position;
            FormulaSuggestionDropdownGui.Layout(_ovAnchor, _ovItems.Count, out _ovRect, out _, out _);
        }
        catch { _ovRect = Rect.zero; _ovAnchor = Rect.zero; }
    }

    void HandleOverlayClickEarly()
    {
        var e = Event.current;
        if (e.type != EventType.MouseDown || e.button != 0) return;
        // Don't steal clicks while a control is mid-drag (e.g. text selection in the formula field).
        if (GUIUtility.hotControl != 0) return;

        if (_ovItems.Count > 0 && _ovAnchor.width > 2f)
        {
            FormulaSuggestionDropdownGui.Layout(_ovAnchor, _ovItems.Count, out Rect popupRect, out _, out _);
            if (popupRect.Contains(e.mousePosition))
            {
                e.Use();
                if (FormulaSuggestionDropdownGui.TryPick(popupRect, _ovItems.Count, _ovScroll, e.mousePosition, out int idx))
                {
                    var (start, len, text) = _ovItems[idx];
                    InsertIntoFormula(start, len, text, ExpectedFormulaControlName());
                    RepaintFocusedWindow();
                }
                return;
            }
        }

        if (_acShowPopup && _acItems.Count > 0 && _acFieldRect.width > 2f)
        {
            FormulaSuggestionDropdownGui.Layout(_acFieldRect, _acItems.Count, out Rect popupRect, out _, out _);
            if (popupRect.Contains(e.mousePosition))
            {
                e.Use();
                if (FormulaSuggestionDropdownGui.TryPick(popupRect, _acItems.Count, _acScroll, e.mousePosition, out int idx))
                {
                    var (start, len, text) = _acItems[idx];
                    if (_acCtrl != null) EditorGUI.FocusTextInControl(_acCtrl);
                    InsertCompletion(start, len, text);
                    RepaintFocusedWindow();
                }
            }
        }
    }

    void DrawFormulaOverlayVisuals()
    {
        if (_ovItems.Count == 0 || _ovAnchor.width < 2f)
        {
            FormulaSuggestionOverlaySession.ClearIfOwner(Property.Path, FormulaSuggestionOverlaySession.OverlayKind.Amp);
            return;
        }

        if (Event.current.type == EventType.Repaint)
        {
            // Wheel updates land in the session; pull them in only for painting.
            _ovScroll = FormulaSuggestionOverlaySession.Scroll;
            FormulaSuggestionDropdownGui.ClampScroll(ref _ovScroll, _ovItems.Count);
            FormulaSuggestionDropdownGui.PaintPopup(
                _ovAnchor, _ovHeader, _ovItems, _ovScroll, out _ovRect, _ovSel);
            FormulaSuggestionOverlaySession.UpdateGeometry(_ovRect, _ovScroll);
        }
        else if (Event.current.type == EventType.Layout)
        {
            if (!_ovScrollResetThisLayout)
                _ovScroll = FormulaSuggestionOverlaySession.Scroll;
            FormulaSuggestionDropdownGui.ClampScroll(ref _ovScroll, _ovItems.Count);
            FormulaSuggestionDropdownGui.Layout(_ovAnchor, _ovItems.Count, out Rect popupRect, out _, out _);
            FormulaSuggestionOverlaySession.Publish(
                Property.Path, FormulaSuggestionOverlaySession.OverlayKind.Amp,
                popupRect, _ovScroll, _ovItems, _ovSel, _ovHeader);
            FormulaSuggestionOverlaySession.SetOwnerPick(Property.Path, OnOverlayPick);
            FormulaSuggestionOverlaySession.EnsureEndOfFrameBlocker(Property.Tree);
            _ovScrollResetThisLayout = false;
        }
    }

    void HandleSuggestionKeyboard()
    {
        var e = Event.current;
        if (e.type != EventType.KeyDown) return;
        if (e.keyCode is not (KeyCode.UpArrow or KeyCode.DownArrow) && !IsConfirmShortcut(e)) return;

        if (IsAmplitudeFormulaFocused())
        {
            RefreshOvItemsForKeyboard();
            if (_ovItems.Count == 0) return;

            bool nav = e.keyCode is KeyCode.UpArrow or KeyCode.DownArrow;
            if (HandleListKeyboard(e, _ovItems.Count, ref _ovSel, ref _ovScroll, out int pick))
            {
                var (start, len, text) = _ovItems[pick];
                InsertIntoFormula(start, len, text, ExpectedFormulaControlName());
            }
            else if (nav)
            {
                FormulaSuggestionOverlaySession.SyncNavigation(_ovScroll, _ovSel);
                RepaintFocusedWindow();
            }
            return;
        }

        if (_acCtrl != null && IsAcFormulaFocused())
        {
            RefreshAcItemsForKeyboard();
            if (_acItems.Count == 0) return;

            bool nav = e.keyCode is KeyCode.UpArrow or KeyCode.DownArrow;
            if (HandleListKeyboard(e, _acItems.Count, ref _acSel, ref _acScroll, out int pick))
            {
                var (start, len, text) = _acItems[pick];
                InsertCompletion(start, len, text);
            }
            else if (nav)
            {
                FormulaSuggestionOverlaySession.SyncNavigation(_acScroll, _acSel);
                RepaintFocusedWindow();
            }
        }
    }

    bool IsAcFormulaFocused() =>
        _acCtrl != null && (GUI.GetNameOfFocusedControl() == _acCtrl || GUIUtility.keyboardControl == _acControlId);

    static void RepaintFocusedWindow() => EditorWindow.focusedWindow?.Repaint();

    bool IsAmplitudeFormulaFocused()
    {
        string expected = ExpectedFormulaControlName();
        if (expected == null) return false;
        if (GUI.GetNameOfFocusedControl() == expected) return true;
        return _formulaControlId != 0 && GUIUtility.keyboardControl == _formulaControlId;
    }

    void RefreshOvItemsForKeyboard()
    {
        if (!PropertyEffectPropertyDrawer.SourceType.TryGetValue(out Type src) || src == null) return;
        PropertyEffectPropertyDrawer.TargetType.TryGetValue(out Type tgt);
        var te = ActiveRecycledEditor();
        if (te == null) return;
        _ovItems.Clear();
        string text = te.text ?? "";
        ComputeSuggestions(text, te.cursorIndex, src, tgt, _ovItems);
        _ovHeader = SuggestionHeader(text, te.cursorIndex);
        if (_ovItems.Count > 0)
            _ovSel = Mathf.Clamp(_ovSel, 0, _ovItems.Count - 1);
        else
            _ovSel = -1;
    }

    void RefreshAcItemsForKeyboard()
    {
        if (!PropertyEffectPropertyDrawer.SourceType.TryGetValue(out Type src) || src == null) return;
        PropertyEffectPropertyDrawer.TargetType.TryGetValue(out Type tgt);
        var te = GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) as TextEditor;
        int caret = te != null ? te.cursorIndex : (_formula?.Length ?? 0);
        _acItems.Clear();
        if (string.IsNullOrEmpty(_formula)) return;
        ComputeSuggestions(_formula, caret, src, tgt, _acItems);
        _acHeader = SuggestionHeader(_formula, caret);
        _acSel = _acItems.Count > 0 ? Mathf.Clamp(_acSel, 0, _acItems.Count - 1) : -1;
    }

    // Rebindable via Tools/shakee's Tools/Options — Formula Autocomplete section. These are raw
    // KeyDown checks inside the drawer, not [MenuItem]s, so Unity's Shortcuts manager can't see or
    // rebind them; ToolsOptionsWindow (a different assembly) reads/writes the same EditorPrefs keys
    // by string literal rather than referencing this class.
    internal const string PrefRightArrowConfirms = "HKModTools.Autocomplete.RightArrowConfirms";
    internal const string PrefCtrlEDConfirms = "HKModTools.Autocomplete.CtrlEDConfirms";

    static bool IsConfirmShortcut(Event e) =>
        (EditorPrefs.GetBool(PrefRightArrowConfirms, true) && e.keyCode == KeyCode.RightArrow)
        || (EditorPrefs.GetBool(PrefCtrlEDConfirms, true) && e.control && (e.keyCode == KeyCode.E || e.keyCode == KeyCode.D));

    // Returns true when → / Ctrl+E / Ctrl+D confirms an insert; sets pick to the chosen index.
    static bool HandleListKeyboard(Event e, int count, ref int sel, ref Vector2 scroll, out int pick)
    {
        pick = -1;
        if (count == 0) return false;

        if (e.keyCode == KeyCode.DownArrow)
        {
            sel = sel < 0 ? 0 : Mathf.Min(sel + 1, count - 1);
            FormulaSuggestionDropdownGui.ScrollToIndex(ref scroll, sel, count);
            e.Use();
            return false;
        }
        if (e.keyCode == KeyCode.UpArrow)
        {
            sel = sel < 0 ? 0 : Mathf.Max(sel - 1, 0);
            FormulaSuggestionDropdownGui.ScrollToIndex(ref scroll, sel, count);
            e.Use();
            return false;
        }
        if (IsConfirmShortcut(e))
        {
            pick = sel >= 0 ? sel : 0;
            e.Use();
            GUI.changed = true;
            return true;
        }
        return false;
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
        if (ctrlName != null) EditorGUI.FocusTextInControl(ctrlName);

        var te = ActiveRecycledEditor();
        string t = te?.text;
        if (string.IsNullOrEmpty(t))
        {
            var amp = FindAmpDrawer();
            ReflectAmp();
            if (amp != null && s_ampCompiler?.GetValue(amp) is RpnTextCompiler comp)
                t = comp.ControlValue;
        }
        if (string.IsNullOrEmpty(t)) return;

        start = Mathf.Clamp(start, 0, t.Length);
        len = Mathf.Clamp(len, 0, t.Length - start);
        string nt = t.Substring(0, start) + completion + t.Substring(start + len);
        if (te != null)
        {
            te.text = nt;
            te.cursorIndex = te.selectIndex = Mathf.Clamp(start + completion.Length, 0, nt.Length);
        }

        var ampDrawer = FindAmpDrawer();
        ReflectAmp();
        if (ampDrawer != null && s_ampCompiler?.GetValue(ampDrawer) is RpnTextCompiler comp2)
        {
            try
            {
                comp2.Parse(nt);
                if (comp2.Validate() == CompilerStatus.Ok)
                    s_ampCompile?.Invoke(ampDrawer, null);
            }
            catch { }
        }
        if (ctrlName != null) EditorGUI.FocusTextInControl(ctrlName);
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
        foreach (var m in matches) outItems.Add((rs, rl, m));
    }

    static string SuggestionFilterKey(string text, int caret)
    {
        if (string.IsNullOrEmpty(text)) return "";
        caret = Mathf.Clamp(caret, 0, text.Length);
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_' || text[start - 1] == '.')) start--;
        string token = text.Substring(start, caret - start);
        return token.Length == 0 ? "" : start + "|" + token;
    }

    static string SuggestionHeader(string text, int caret)
    {
        if (string.IsNullOrEmpty(text)) return "Formula";
        caret = Mathf.Clamp(caret, 0, text.Length);
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_' || text[start - 1] == '.')) start--;
        string token = text.Substring(start, caret - start);
        int dot = token.LastIndexOf('.');
        if (dot >= 0) return token.Substring(0, dot);
        return "Keyword";
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
    readonly List<(int start, int len, string text)> _acItems = new();
    Vector2 _acScroll;
    int _acSel = -1;
    string _acHeader = "";
    string _acFilterKey = "";
    bool _acScrollResetThisLayout;
    int _acControlId;
    Rect _acFieldRect;
    bool _acShowPopup;

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
                _acFieldRect = GUILayoutUtility.GetLastRect();
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
                    _acShowPopup = focused && _acItems.Count > 0;
                }

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
        if (!focused || string.IsNullOrEmpty(_formula))
        {
            _acFilterKey = "";
            return;
        }
        var te = GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) as TextEditor;
        int caret = te != null ? te.cursorIndex : _formula.Length;
        ComputeSuggestions(_formula, caret, src, tgt, _acItems);
        _acHeader = SuggestionHeader(_formula, caret);
        string filterKey = SuggestionFilterKey(_formula, caret);
        if (filterKey != _acFilterKey)
        {
            _acFilterKey = filterKey;
            _acSel = _acItems.Count > 0 ? 0 : -1;
            _acScroll = Vector2.zero;
            _acScrollResetThisLayout = true;
        }
        else
            _acSel = _acItems.Count > 0 ? Mathf.Clamp(_acSel, 0, _acItems.Count - 1) : -1;
        _acControlId = GUIUtility.keyboardControl;
    }

    void DrawAcSuggestionPopupVisuals()
    {
        if (!_acShowPopup || _acItems.Count == 0 || _acFieldRect.width < 2f)
        {
            FormulaSuggestionOverlaySession.ClearIfOwner(Property.Path, FormulaSuggestionOverlaySession.OverlayKind.Ac);
            return;
        }

        if (Event.current.type == EventType.Repaint)
        {
            _acScroll = FormulaSuggestionOverlaySession.Scroll;
            FormulaSuggestionDropdownGui.ClampScroll(ref _acScroll, _acItems.Count);
            FormulaSuggestionDropdownGui.PaintPopup(
                _acFieldRect, _acHeader, _acItems, _acScroll, out var popupRect, _acSel);
            FormulaSuggestionOverlaySession.UpdateGeometry(popupRect, _acScroll);
        }
        else if (Event.current.type == EventType.Layout)
        {
            if (!_acScrollResetThisLayout)
                _acScroll = FormulaSuggestionOverlaySession.Scroll;
            FormulaSuggestionDropdownGui.ClampScroll(ref _acScroll, _acItems.Count);
            FormulaSuggestionDropdownGui.Layout(_acFieldRect, _acItems.Count, out Rect popupRect, out _, out _);
            FormulaSuggestionOverlaySession.Publish(
                Property.Path, FormulaSuggestionOverlaySession.OverlayKind.Ac,
                popupRect, _acScroll, _acItems, _acSel, _acHeader);
            FormulaSuggestionOverlaySession.SetOwnerPick(Property.Path, OnOverlayPick);
            FormulaSuggestionOverlaySession.EnsureEndOfFrameBlocker(Property.Tree);
            _acScrollResetThisLayout = false;
        }
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

    // ── Inline Rendered preview ────────────────────────────────────────────────
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
            if (!string.IsNullOrEmpty(res.rendered)) lines.Add("Rendered:  " + res.rendered);
            if (res.warnings != null) foreach (var w in res.warnings) lines.Add("⚠ " + w);
            return string.Join("\n", lines);
        }
        catch (Exception e) { return "render error: " + e.Message; }
    }
}

/// <summary>
/// SuperPriority wrapper on every Odin property: when the formula suggestion popup is open, eat pointer
/// events over its rect before any inner drawer (Path, foldouts, fields) can see them.
/// </summary>
[DrawerPriority(DrawerPriorityLevel.SuperPriority)]
public class FormulaSuggestionOverlayBlockerDrawer : OdinDrawer
{
    public override bool CanDrawProperty(InspectorProperty property) => true;

    protected override void DrawPropertyLayout(GUIContent label)
    {
        if (FormulaSuggestionOverlaySession.Active)
            FormulaSuggestionOverlaySession.BlockPointerEvents();
        CallNextDrawer(label);
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
            if (!string.IsNullOrEmpty(res.rendered)) lines.Add("Rendered:  " + res.rendered);
            if (res.warnings != null) foreach (var w in res.warnings) lines.Add("⚠ " + w);
            return string.Join("\n", lines);
        }
        catch (Exception e) { return "render error: " + e.Message; }
    }
}
#endif

#if ODIN_INSPECTOR
/// <summary>
/// Tracks the one open formula suggestion popup. PropertyEffect drawers publish geometry;
/// <see cref="FormulaSuggestionOverlayBlockerDrawer"/> wraps every Odin property; an end-of-frame
/// blocker is drawn last via <see cref="PropertyTree.DelayAction"/> so it wins IMGUI hit-testing.
/// </summary>
static class FormulaSuggestionOverlaySession
{
    internal enum OverlayKind { None, Amp, Ac }

    static readonly int s_BlockHint = "FormulaSuggestionOverlayBlock".GetHashCode();
    static readonly List<(int start, int len, string text)> s_items = new();

    static int s_layoutFrame = -1;
    static int s_blockControlId;
    static int s_blockerQueuedFrame = -1;
    static int s_lastRenewFrame = -1; // last frame Publish() was called by the owning drawer; staleness == owner is gone

    internal static bool Active { get; private set; }
    internal static Rect BlockRect { get; private set; }
    internal static Vector2 Scroll { get; set; }
    internal static int SelectedIndex { get; private set; }
    internal static string OwnerPath { get; private set; }
    internal static OverlayKind Kind { get; private set; }
    internal static Action<int> OwnerPick { get; private set; }
    static string s_header = "";

    static FormulaSuggestionOverlaySession()
    {
        Editor.finishedDefaultHeaderGUI += _ => BlockPointerEvents();
    }

    internal static void SetOwnerPick(string ownerPath, Action<int> onPick)
    {
        if (Active && string.Equals(OwnerPath, ownerPath, StringComparison.Ordinal))
            OwnerPick = onPick;
    }

    internal static void UpdateGeometry(Rect blockRect, Vector2 scroll)
    {
        if (!Active || blockRect.width < 2f) return;
        BlockRect = blockRect;
        Scroll = scroll;
    }

    internal static void EnsureEndOfFrameBlocker(PropertyTree tree)
    {
        if (!Active || tree == null) return;
        if (s_blockerQueuedFrame == Time.frameCount) return;
        s_blockerQueuedFrame = Time.frameCount;
        tree.DelayAction(DrawEndOfFrameBlocker);
    }

    static void DrawEndOfFrameBlocker()
    {
        if (!Active || BlockRect.width < 2f) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        FormulaSuggestionDropdownGui.PaintInRect(BlockRect, s_header, s_items, Scroll, SelectedIndex);
    }

    internal static void Publish(
        string ownerPath,
        OverlayKind kind,
        Rect blockRect,
        Vector2 scroll,
        IReadOnlyList<(int start, int len, string text)> items,
        int selectedIndex,
        string header)
    {
        if (items == null || items.Count == 0 || blockRect.width < 2f)
        {
            ClearIfOwner(ownerPath, kind);
            return;
        }

        Active = true;
        OwnerPath = ownerPath;
        Kind = kind;
        BlockRect = blockRect;
        Scroll = scroll;
        SelectedIndex = selectedIndex;
        s_header = header ?? "";
        s_items.Clear();
        s_items.AddRange(items);
        s_lastRenewFrame = Time.frameCount;
    }

    internal static void ClearIfOwner(string ownerPath, OverlayKind kind)
    {
        if (!Active || Kind != kind || !string.Equals(OwnerPath, ownerPath, StringComparison.Ordinal))
            return;
        Clear();
    }

    internal static void Clear()
    {
        Active = false;
        BlockRect = default;
        Scroll = default;
        SelectedIndex = -1;
        OwnerPath = null;
        Kind = OverlayKind.None;
        OwnerPick = null;
        s_header = "";
        s_items.Clear();
    }

    internal static (int start, int len, string text) GetItem(int index) => s_items[index];

    internal static void SyncNavigation(Vector2 scroll, int selectedIndex)
    {
        if (!Active) return;
        Scroll = scroll;
        SelectedIndex = selectedIndex;
    }

    /// <summary>Eat pointer events over the open overlay. Click-to-insert is handled here on MouseDown (SuperPriority, before TextArea).</summary>
    internal static void BlockPointerEvents()
    {
        if (!Active) return;
        var e = Event.current;
        if (e == null || e.type == EventType.Used) return;
        // Don't intercept while another control owns the pointer (e.g. text selection drag in the formula field).
        if (GUIUtility.hotControl != 0) return;

        if (e.type == EventType.Layout)
        {
            if (s_layoutFrame != Time.frameCount)
            {
                s_layoutFrame = Time.frameCount;
                // The owning drawer renews the session via Publish() every Layout pass it's still
                // drawn in. If its Editor/PropertyTree was torn down (element switched, window
                // closed, ...) nobody calls Publish/ClearIfOwner again and the session would
                // otherwise stay Active forever with a stale OwnerPath — close it once nobody has
                // renewed it for more than a frame.
                if (Time.frameCount - s_lastRenewFrame > 1) { Clear(); return; }
                s_blockControlId = GUIUtility.GetControlID(s_BlockHint, FocusType.Passive, BlockRect);
            }
            return;
        }

        // Any stray MouseMove in this inspector — not just over the popup itself — has to be
        // eaten while the popup is open. Left unblocked, it reaches whatever's underneath (the
        // rest of the property tree) and can trigger background hover/focus-follow behaviour
        // there (e.g. an embedded scroll view jumping back to the top). Unlike click/scroll,
        // this one isn't scoped to BlockRect.Contains — the popup owns pointer motion everywhere
        // in the inspector until it closes.
        if (e.type == EventType.MouseMove)
        {
            e.Use();
            return;
        }

        if (!BlockRect.Contains(e.mousePosition)) return;

        switch (e.type)
        {
            case EventType.ScrollWheel:
            {
                var scroll = Scroll;
                FormulaSuggestionDropdownGui.ApplyScrollWheel(ref scroll, s_items.Count, e.delta.y);
                Scroll = scroll;
                e.Use();
                EditorWindow.focusedWindow?.Repaint();
                return;
            }
            case EventType.MouseDown:
                if (e.button == 0 && FormulaSuggestionDropdownGui.TryPick(BlockRect, s_items.Count, Scroll, e.mousePosition, out int idx))
                    OwnerPick?.Invoke(idx);
                e.Use();
                return;
            case EventType.MouseUp:
            case EventType.MouseDrag:
            case EventType.ContextClick:
                e.Use();
                return;
        }
    }
}
#endif

/// <summary>
/// Scrollable popup list styled like Unity's enum/AdvancedDropdown picker (header row + scrollbar, no search).
/// Used for formula-token autocomplete on both Amplitude's formula field and the backup autocomplete field.
/// </summary>
static class FormulaSuggestionDropdownGui
{
    public const float MinWidth = 200f;
    const float MaxListHeight = 200f;
    const float Pad = 1f;
    const float Gap = 1f;   // gap between popup and anchor field (popup opens above the field)

    static readonly Color BackDark = new(0.169f, 0.169f, 0.169f);
    static readonly Color BackLight = new(0.92f, 0.92f, 0.92f);
    static readonly Color HeaderDark = new(0.235f, 0.247f, 0.255f);
    static readonly Color HeaderLight = new(0.65f, 0.65f, 0.65f);
    static readonly Color SelectDark = new(0.294f, 0.431f, 0.686f);
    static readonly Color SelectLight = new(0.24f, 0.49f, 0.90f);
    static readonly Color Border = new(0f, 0f, 0f, 0.6f);

    static GUIStyle s_header, s_item;

    static float RowH => EditorGUIUtility.singleLineHeight;

    static GUIStyle HeaderStyle
    {
        get
        {
            if (s_header != null) return s_header;
            s_header = new GUIStyle(EditorStyles.label)
            {
                padding = new RectOffset(6, 4, 0, 0),
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Normal
            };
            s_header.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.733f, 0.733f, 0.733f) : new Color(0.15f, 0.15f, 0.15f);
            return s_header;
        }
    }

    static GUIStyle ItemStyle
    {
        get
        {
            if (s_item != null) return s_item;
            s_item = new GUIStyle(EditorStyles.label)
            {
                padding = new RectOffset(6, 4, 0, 0),
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };
            s_item.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.733f, 0.733f, 0.733f) : new Color(0.1f, 0.1f, 0.1f);
            return s_item;
        }
    }

    public static Vector2 CalcSize(int itemCount, float anchorWidth)
    {
        ListMetrics(itemCount, out _, out float viewH);
        return new Vector2(Mathf.Max(anchorWidth, MinWidth), Pad * 2f + RowH + viewH);
    }

    static void ListMetrics(int itemCount, out float contentH, out float viewH)
    {
        contentH = itemCount * RowH;
        viewH = Mathf.Min(contentH, MaxListHeight);
    }

    public static void Layout(Rect anchor, int itemCount, out Rect popupRect, out Rect scrollOuter, out float viewH)
    {
        ListMetrics(itemCount, out _, out viewH);
        var size = CalcSize(itemCount, anchor.width);
        popupRect = new Rect(anchor.x, anchor.y - size.y - Gap, size.x, size.y);
        scrollOuter = new Rect(popupRect.x + Pad, popupRect.y + Pad + RowH, popupRect.width - Pad * 2f, viewH);
    }

    public static bool TryPick(Rect popupRect, int itemCount, Vector2 scroll, Vector2 mouse, out int index)
    {
        index = -1;
        if (itemCount == 0 || popupRect.width < 2f || !popupRect.Contains(mouse)) return false;
        ListMetrics(itemCount, out _, out float viewH);
        var scrollOuter = new Rect(
            popupRect.x + Pad,
            popupRect.y + Pad + RowH,
            popupRect.width - Pad * 2f,
            viewH);
        if (!scrollOuter.Contains(mouse)) return false;
        index = Mathf.FloorToInt((mouse.y - scrollOuter.y + scroll.y) / RowH);
        return index >= 0 && index < itemCount;
    }

    public static void ScrollToIndex(ref Vector2 scroll, int index, int itemCount)
    {
        ListMetrics(itemCount, out float contentH, out float viewH);
        float rowTop = index * RowH;
        float rowBottom = rowTop + RowH;
        if (rowTop < scroll.y)
            scroll.y = rowTop;
        else if (rowBottom > scroll.y + viewH)
            scroll.y = rowBottom - viewH;
        scroll.y = Mathf.Clamp(scroll.y, 0f, Mathf.Max(0f, contentH - viewH));
    }

    public static void ClampScroll(ref Vector2 scroll, int itemCount)
    {
        ListMetrics(itemCount, out float contentH, out float viewH);
        scroll.y = Mathf.Clamp(scroll.y, 0f, Mathf.Max(0f, contentH - viewH));
    }

    public static void ApplyScrollWheel(ref Vector2 scroll, int itemCount, float deltaY)
    {
        ListMetrics(itemCount, out float contentH, out float viewH);
        float maxScroll = Mathf.Max(0f, contentH - viewH);
        scroll.y = Mathf.Clamp(scroll.y + deltaY * RowH, 0f, maxScroll);
    }

    /// <summary>Repaint-only styled popup anchored above <paramref name="anchor"/>.</summary>
    public static void PaintPopup(
        Rect anchor,
        string header,
        IReadOnlyList<(int start, int len, string text)> items,
        Vector2 scroll,
        out Rect popupRect,
        int selectedIndex = -1)
    {
        popupRect = Rect.zero;
        if (items == null || items.Count == 0 || Event.current.type != EventType.Repaint) return;

        Layout(anchor, items.Count, out popupRect, out _, out float viewH);
        ListMetrics(items.Count, out float contentH, out _);
        float headerH = RowH;
        var headerRect = new Rect(popupRect.x + Pad, popupRect.y + Pad, popupRect.width - Pad * 2f, headerH);
        var scrollOuter = new Rect(popupRect.x + Pad, headerRect.yMax, popupRect.width - Pad * 2f, viewH);
        bool scrollable = contentH > viewH;

        int hover = -1;
        if (scrollOuter.Contains(Event.current.mousePosition))
        {
            hover = Mathf.FloorToInt((Event.current.mousePosition.y - scrollOuter.y + scroll.y) / RowH);
            if (hover < 0 || hover >= items.Count) hover = -1;
        }
        int highlight = hover >= 0 ? hover : selectedIndex;

        Paint(popupRect, header, items, scroll, headerRect, scrollOuter, viewH, contentH, scrollable, highlight);
    }

    /// <summary>Repaint-only styled popup inside a precomputed <paramref name="rect"/>.</summary>
    public static void PaintInRect(
        Rect rect,
        string header,
        IReadOnlyList<(int start, int len, string text)> items,
        Vector2 scroll,
        int selectedIndex = -1)
    {
        if (items == null || items.Count == 0 || Event.current.type != EventType.Repaint) return;

        ListMetrics(items.Count, out float contentH, out float viewH);
        float headerH = RowH;
        var headerRect = new Rect(rect.x + Pad, rect.y + Pad, rect.width - Pad * 2f, headerH);
        var scrollOuter = new Rect(rect.x + Pad, headerRect.yMax, rect.width - Pad * 2f, viewH);
        bool scrollable = contentH > viewH;

        int hover = -1;
        if (scrollOuter.Contains(Event.current.mousePosition))
        {
            hover = Mathf.FloorToInt((Event.current.mousePosition.y - scrollOuter.y + scroll.y) / RowH);
            if (hover < 0 || hover >= items.Count) hover = -1;
        }
        int highlight = hover >= 0 ? hover : selectedIndex;

        Paint(rect, header, items, scroll, headerRect, scrollOuter, viewH, contentH, scrollable, highlight);
    }

    /// <summary>Blocker box + Repaint overlay. Returns picked row index on click, else -1.</summary>
    public static int Draw(
        Rect anchor,
        string header,
        IReadOnlyList<(int start, int len, string text)> items,
        ref Vector2 scroll,
        out Rect popupRect,
        int selectedIndex = -1)
    {
        popupRect = Rect.zero;
        if (items == null || items.Count == 0) return -1;
        Layout(anchor, items.Count, out popupRect, out _, out _);
        return HandleInputAndPaint(popupRect, header, items, ref scroll, selectedIndex);
    }

    static int HandleInputAndPaint(
        Rect rect,
        string header,
        IReadOnlyList<(int start, int len, string text)> items,
        ref Vector2 scroll,
        int selectedIndex)
    {
        if (items == null || items.Count == 0) return -1;

        ListMetrics(items.Count, out float contentH, out float viewH);
        float headerH = RowH;
        var headerRect = new Rect(rect.x + Pad, rect.y + Pad, rect.width - Pad * 2f, headerH);
        var scrollOuter = new Rect(rect.x + Pad, headerRect.yMax, rect.width - Pad * 2f, viewH);
        bool scrollable = contentH > viewH;
        float maxScroll = Mathf.Max(0f, contentH - viewH);
        scroll.y = Mathf.Clamp(scroll.y, 0f, maxScroll);

        var e = Event.current;

        // Layer 1 — IMGUI blocker: eats hits so inspector fields underneath don't receive them.
        GUI.Box(rect, GUIContent.none, GUIStyle.none);

        if (e.type == EventType.ScrollWheel && rect.Contains(e.mousePosition))
        {
            scroll.y = Mathf.Clamp(scroll.y + e.delta.y * RowH, 0f, maxScroll);
            e.Use();
        }

        int hover = -1;
        if (scrollOuter.Contains(e.mousePosition))
        {
            hover = Mathf.FloorToInt((e.mousePosition.y - scrollOuter.y + scroll.y) / RowH);
            if (hover < 0 || hover >= items.Count) hover = -1;
        }

        int highlight = hover >= 0 ? hover : selectedIndex;

        // Layer 2 — styled popup drawn on Repaint only (on top of the blocker).
        if (e.type == EventType.Repaint)
            Paint(rect, header, items, scroll, headerRect, scrollOuter, viewH, contentH, scrollable, highlight);
        else if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
        {
            e.Use();
            if (hover >= 0)
                return hover;
        }

        return -1;
    }

    static void Paint(
        Rect rect,
        string header,
        IReadOnlyList<(int start, int len, string text)> items,
        Vector2 scroll,
        Rect headerRect,
        Rect scrollOuter,
        float viewH,
        float contentH,
        bool scrollable,
        int highlight)
    {
        bool dark = EditorGUIUtility.isProSkin;
        var back = dark ? BackDark : BackLight;
        var headerBack = dark ? HeaderDark : HeaderLight;
        var select = dark ? SelectDark : SelectLight;

        EditorGUI.DrawRect(rect, back);
        DrawBorder(rect, Border);
        EditorGUI.DrawRect(headerRect, headerBack);
        GUI.Label(headerRect, string.IsNullOrEmpty(header) ? "Formula" : header, HeaderStyle);

        float contentW = scrollOuter.width - (scrollable ? 14f : 0f);
        EditorGUI.DrawRect(scrollOuter, back);
        GUI.BeginClip(scrollOuter);
        int first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / RowH));
        int last = Mathf.Min(items.Count - 1, Mathf.CeilToInt((scroll.y + viewH) / RowH));
        for (int i = first; i <= last; i++)
        {
            var row = new Rect(0f, i * RowH - scroll.y, contentW, RowH);
            if (i == highlight)
                EditorGUI.DrawRect(row, select);
            GUI.Label(row, items[i].text, ItemStyle);
        }
        GUI.EndClip();

        if (scrollable)
            DrawScrollbar(scrollOuter, scroll.y, contentH, viewH, dark);
    }

    /// <summary>Draw popup contents inside <paramref name="rect"/> (local 0,0 origin). Returns picked item index on click, else -1.</summary>
    public static int DrawInRect(
        Rect rect,
        string header,
        IReadOnlyList<(int start, int len, string text)> items,
        ref Vector2 scroll,
        out int picked,
        int selectedIndex = -1)
    {
        picked = -1;
        int idx = HandleInputAndPaint(rect, header, items, ref scroll, selectedIndex);
        if (idx >= 0) picked = idx;
        return idx;
    }

    static void DrawScrollbar(Rect area, float scrollY, float contentH, float viewH, bool dark)
    {
        const float barW = 14f;
        var track = new Rect(area.xMax - barW, area.y, barW, area.height);
        var trackColor = dark ? new Color(0.12f, 0.12f, 0.12f) : new Color(0.55f, 0.55f, 0.55f);
        var thumbColor = dark ? new Color(0.45f, 0.45f, 0.45f) : new Color(0.35f, 0.35f, 0.35f);
        EditorGUI.DrawRect(track, trackColor);
        float thumbH = Mathf.Max(RowH, viewH * (viewH / contentH));
        float travel = Mathf.Max(1f, track.height - thumbH);
        float thumbY = track.y + (scrollY / Mathf.Max(1f, contentH - viewH)) * travel;
        EditorGUI.DrawRect(new Rect(track.x + 3f, thumbY, track.width - 6f, thumbH), thumbColor);
    }

    static void DrawBorder(Rect r, Color c)
    {
        EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1f), c);
        EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), c);
        EditorGUI.DrawRect(new Rect(r.x, r.y, 1f, r.height), c);
        EditorGUI.DrawRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), c);
    }
}

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
