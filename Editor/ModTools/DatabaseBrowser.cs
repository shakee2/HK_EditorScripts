using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class DatabaseBrowser : EditorWindow
{
    class Entry
    {
        public UnityEngine.Object obj;
        public string name;
        public string typeName;
        public string scope;   // "Vanilla" | "Mod"
        public bool isContent; // implements IDatatableElement — a real moddable data row, not build/plugin/config plumbing
    }

    // Flattened display item: a type header (group mode) or an entry row
    class DI
    {
        public bool isHeader;
        public string headerType;
        public int count;
        public bool collapsed;
        public Entry entry;
    }

    // ── State ─────────────────────────────────────────────────────────────────
    static List<Entry> s_cachedAll;   // survives across the close+reopen we use to dock/undock, so toggling modes doesn't rescan
    static bool s_suppressPlacement;  // guard: while we deliberately reopen, don't re-trigger placement in OnEnable
    List<Entry> _all = new();
    List<Entry> _view = new();
    List<DI>    _display = new();

    // filters
    string _search = "";
    List<string> _types = new();
    string _typeFilter = "";          // "" = any
    AdvancedDropdownState _typeDdState = new();
    bool _groupByType;
    HashSet<string> _collapsed = new();
    enum ScopeFilter { All, MyMod, Vanilla }
    static readonly string[] SCOPE_LABELS = { "All", "My Mod", "Vanilla" };
    ScopeFilter _scope = ScopeFilter.All;
    bool _contentOnly = true;   // hide build/plugin/config ScriptableObjects; show only actual data rows

    // prefs keys
    const string SearchKey     = "DatabaseBrowser.Search";
    const string TypeFilterKey = "DatabaseBrowser.TypeFilter";
    const string GroupKey      = "DatabaseBrowser.GroupByType";
    const string ScopeKey      = "DatabaseBrowser.Scope";
    const string IssueKey      = "DatabaseBrowser.Issue";
    const string ContentOnlyKey = "DatabaseBrowser.ContentOnly";
    const string SelectedGuidKey = "DatabaseBrowser.SelectedGuid";

    // Diagnostics filter — narrows to MY mod's files with issues (vanilla is never analyzed).
    enum IssueFilter { All, Warnings, HardStops }
    static readonly string[] ISSUE_LABELS = { "All", "Warnings", "Hard Stops" };
    IssueFilter _issue = IssueFilter.All;

    enum ViewMode { Window, ListOnly }
    static readonly string[] MODE_LABELS = { "Window", "Only List" };
    ViewMode _mode = ViewMode.Window;
    const string ModeKey = "DatabaseBrowser.Mode";

    // selection + embedded inspector
    UnityEngine.Object _selected;
    Editor _editor;

    // layout
    float _leftWidth = 340f;
    bool  _draggingSplit;
    Vector2 _listScroll, _inspScroll;
    int _hoverIndex = -1; // last row that had the mouse hover highlight; only repaint when this changes
    const float ROW_H = 18f;
    const float SPLIT_W = 5f;
    const float TYPE_COL_W = 160f;

    static readonly Color ROW_ALT    = new Color(1f, 1f, 1f, 0.03f);
    static readonly Color ROW_HOVER  = new Color(0.3f, 0.5f, 0.9f, 0.18f);
    static readonly Color ROW_SEL    = new Color(0.3f, 0.5f, 0.9f, 0.35f);
    static readonly Color ROW_LINE   = new Color(0f, 0f, 0f, 0.20f);
    static readonly Color HEADER_BG  = new Color(1f, 1f, 1f, 0.07f);

    GUIStyle _typeColStyle, _headerStyle;

    [MenuItem("Tools/shakee's Tools/Database Browser", false, 2)]
    static void Open() => GetWindow<DatabaseBrowser>("Database Browser");

    void OnEnable()
    {
        wantsMouseMove = true;
        LoadPrefs();
        _mode = (ViewMode)EditorPrefs.GetInt(ModeKey, 0);
        _contentOnly = EditorPrefs.GetBool(ContentOnlyKey, true);
        Refresh();
        RestoreSelection();
        if (!s_suppressPlacement) ApplyWindowPlacement();
    }

    void LoadPrefs()
    {
        _search     = EditorPrefs.GetString(SearchKey, "");
        _typeFilter = EditorPrefs.GetString(TypeFilterKey, "");
        _groupByType = EditorPrefs.GetBool(GroupKey, false);
        _scope      = (ScopeFilter)EditorPrefs.GetInt(ScopeKey, 0);
        _issue      = (IssueFilter)EditorPrefs.GetInt(IssueKey, 0);
    }

    void OnDisable()
    {
        SavePrefs();
        if (_editor != null) DestroyImmediate(_editor);
    }

    void SavePrefs()
    {
        EditorPrefs.SetString(SearchKey, _search);
        EditorPrefs.SetString(TypeFilterKey, _typeFilter);
        EditorPrefs.SetBool(GroupKey, _groupByType);
        EditorPrefs.SetInt(ScopeKey, (int)_scope);
        EditorPrefs.SetInt(IssueKey, (int)_issue);
        // Persist selected element by asset GUID (only mod/asset selections survive; vanilla refs can't)
        if (_selected != null && AssetDatabase.Contains(_selected))
            EditorPrefs.SetString(SelectedGuidKey, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_selected)));
        else
            EditorPrefs.DeleteKey(SelectedGuidKey);
    }

    void RestoreSelection()
    {
        string guid = EditorPrefs.GetString(SelectedGuidKey, "");
        if (guid.Length == 0) return;
        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(AssetDatabase.GUIDToAssetPath(guid));
        if (obj != null) _selected = obj;
    }

    void InitStyles()
    {
        if (_typeColStyle == null)
        {
            _typeColStyle = new GUIStyle(EditorStyles.miniLabel)
            { alignment = TextAnchor.MiddleRight, normal = { textColor = EditorGUIUtility.isProSkin
                ? new Color(0.75f, 0.75f, 0.75f) : new Color(0.3f, 0.3f, 0.3f) } };
        }
        if (_headerStyle == null)
            _headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
    }

    // ── Scan ──────────────────────────────────────────────────────────────────
    void Refresh(bool force = false)
    {
        if (force || s_cachedAll == null) s_cachedAll = ScanAll();
        _all = s_cachedAll.OrderBy(e => e.typeName).ThenBy(e => e.name).ToList();
        ApplyFilters();
    }

    static List<Entry> ScanAll()
    {
        var list = new List<Entry>();
        try
        {
            var guids = AssetDatabase.FindAssets("t:ScriptableObject");
            int n = guids.Length, i = 0;
            foreach (var guid in guids)
            {
                if (++i % 64 == 0)
                    EditorUtility.DisplayProgressBar("Database Browser", "Loading project assets…", i / (float)n);
                var path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (obj == null || obj is not ScriptableObject) continue;
                    list.Add(new Entry { obj = obj, name = obj.name, typeName = obj.GetType().Name, scope = "Mod",
                        isContent = obj is Amplitude.Framework.IDatatableElement });
                }
            }

            EditorUtility.DisplayProgressBar("Database Browser", "Loading vanilla databases…", 1f);
            foreach (var obj in VanillaDatabaseMount.LoadAllOfType(typeof(ScriptableObject)))
                list.Add(new Entry { obj = obj, name = obj.name, typeName = obj.GetType().Name, scope = "Vanilla",
                    isContent = obj is Amplitude.Framework.IDatatableElement });
        }
        finally { EditorUtility.ClearProgressBar(); }
        return list;
    }

    void ApplyFilters()
    {
        _types = _all.Where(e => !_contentOnly || e.isContent)
            .Select(e => e.typeName).Distinct().OrderBy(s => s).ToList();
        if (_typeFilter.Length > 0 && !_types.Contains(_typeFilter)) _typeFilter = "";

        string s = _search.Trim().ToLowerInvariant();
        _view = _all.Where(e =>
            (!_contentOnly || e.isContent) &&
            (_typeFilter.Length == 0 || e.typeName == _typeFilter) &&
            (_scope == ScopeFilter.All ||
             (_scope == ScopeFilter.MyMod && e.scope == "Mod") ||
             (_scope == ScopeFilter.Vanilla && e.scope == "Vanilla")) &&
            (s.Length == 0 || e.name.ToLowerInvariant().Contains(s)) &&
            MatchesIssue(e)
        ).ToList();
        BuildDisplay();
        Repaint();
    }

    // Issue filter: only my mod's content files, with a warning / hard-stop finding. Vanilla is never
    // analyzed (keeps it fast). Uses the shared, cached InspectorDiagnostics engine.
    bool MatchesIssue(Entry e)
    {
        if (_issue == IssueFilter.All) return true;
        if (e.scope != "Mod" || !e.isContent || e.obj == null) return false;
        var findings = InspectorDiagnostics.Analyze(e.obj);
        var want = _issue == IssueFilter.HardStops ? DiagSeverity.Crash : DiagSeverity.Warn;
        for (int i = 0; i < findings.Count; i++) if (findings[i].severity == want) return true;
        return false;
    }

    void BuildDisplay()
    {
        _display = new List<DI>(_view.Count + 32);
        if (!_groupByType)
        {
            foreach (var e in _view) _display.Add(new DI { entry = e });
        }
        else
        {
            foreach (var g in _view.GroupBy(e => e.typeName).OrderBy(g => g.Key))
            {
                bool collapsed = _collapsed.Contains(g.Key);
                _display.Add(new DI { isHeader = true, headerType = g.Key, count = g.Count(), collapsed = collapsed });
                if (!collapsed)
                    foreach (var e in g.OrderBy(x => x.name))
                        _display.Add(new DI { entry = e });
            }
        }
    }

    // ── GUI ───────────────────────────────────────────────────────────────────
    void OnGUI()
    {
        InitStyles();

        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        EditorGUI.BeginChangeCheck();
        _mode = (ViewMode)GUILayout.Toolbar((int)_mode, MODE_LABELS, EditorStyles.toolbarButton, GUILayout.Width(150));
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetInt(ModeKey, (int)_mode);
            if (_mode == ViewMode.ListOnly && _editor != null) { DestroyImmediate(_editor); _editor = null; }
            ApplyWindowPlacement();
        }
        GUILayout.Space(8);
        if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70))) Refresh(true);
        if (GUILayout.Button("Save Assets", EditorStyles.toolbarButton, GUILayout.Width(90))) AssetDatabase.SaveAssets();
        GUILayout.FlexibleSpace();
        GUILayout.Label($"{_view.Count} / {_all.Count}", EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();

        float toolbarH = EditorStyles.toolbar.fixedHeight > 0f ? EditorStyles.toolbar.fixedHeight : 21f;
        float top = toolbarH;
        float h = position.height - top;

        if (_mode == ViewMode.Window)
        {
            DrawLeft(new Rect(0, top, _leftWidth, h));
            DrawSplitter(new Rect(_leftWidth, top, SPLIT_W, h));
            DrawRight(new Rect(_leftWidth + SPLIT_W, top, position.width - _leftWidth - SPLIT_W, h));
        }
        else // ListOnly — full-width list; clicks drive the docked Inspector
        {
            DrawLeft(new Rect(0, top, position.width, h));
        }
    }

    // ── Left: filters + list ──────────────────────────────────────────────────
    void DrawLeft(Rect area)
    {
        GUILayout.BeginArea(area);

        EditorGUILayout.BeginHorizontal();
        GUI.SetNextControlName("dbSearch");
        EditorGUI.BeginChangeCheck();
        _search = EditorGUILayout.TextField("Search", _search);
        if (EditorGUI.EndChangeCheck()) ApplyFilters();
        if (GUILayout.Button("x", EditorStyles.miniButton, GUILayout.Width(18)) && _search.Length > 0)
        { _search = ""; GUI.FocusControl(null); ApplyFilters(); }
        EditorGUILayout.EndHorizontal();

        // Searchable type dropdown
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Type", GUILayout.Width(40));
        if (GUILayout.Button(_typeFilter.Length == 0 ? "(any)" : _typeFilter, EditorStyles.popup))
        {
            var dd = new TypeDropdown(_typeDdState, _types, picked => { _typeFilter = picked; ApplyFilters(); });
            dd.Show(GUILayoutUtility.GetLastRect());
        }
        bool g = GUILayout.Toggle(_groupByType, "Group", EditorStyles.miniButton, GUILayout.Width(54));
        if (g != _groupByType) { _groupByType = g; BuildDisplay(); }
        EditorGUILayout.EndHorizontal();

        EditorGUI.BeginChangeCheck();
        _contentOnly = GUILayout.Toggle(_contentOnly, "Content Only", EditorStyles.miniButton);
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetBool(ContentOnlyKey, _contentOnly);
            ApplyFilters();
        }

        EditorGUI.BeginChangeCheck();
        _scope = (ScopeFilter)GUILayout.Toolbar((int)_scope, SCOPE_LABELS);
        if (EditorGUI.EndChangeCheck()) ApplyFilters();

        EditorGUI.BeginChangeCheck();
        _issue = (IssueFilter)GUILayout.Toolbar((int)_issue, ISSUE_LABELS);
        if (EditorGUI.EndChangeCheck()) ApplyFilters();

        EditorGUILayout.Space(2);

        // Virtualized list over _display
        Rect listArea = GUILayoutUtility.GetRect(10, 10000, 10, 100000,
            GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        float contentW = listArea.width - 16;
        Rect content = new Rect(0, 0, contentW, _display.Count * ROW_H);

        _listScroll = GUI.BeginScrollView(listArea, _listScroll, content);
        int first = Mathf.Max(0, Mathf.FloorToInt(_listScroll.y / ROW_H));
        int last  = Mathf.Min(_display.Count, Mathf.CeilToInt((_listScroll.y + listArea.height) / ROW_H) + 1);
        Vector2 mouse = Event.current.mousePosition;
        int hoveredThisPass = -1;

        string toggleType = null;
        for (int i = first; i < last; i++)
        {
            var di = _display[i];
            Rect row = new Rect(0, i * ROW_H, contentW, ROW_H);

            if (di.isHeader)
            {
                EditorGUI.DrawRect(row, HEADER_BG);
                bool expanded = EditorGUI.Foldout(new Rect(row.x + 2, row.y, row.width - 4, row.height),
                    !di.collapsed, $"{di.headerType}  ({di.count})", true, _headerStyle);
                if (expanded == di.collapsed) toggleType = di.headerType; // state flipped
                EditorGUI.DrawRect(new Rect(0, row.yMax - 1, contentW, 1), ROW_LINE);
                continue;
            }

            var e = di.entry;

            // Handled before the GUI.Button below so a right-click anywhere on the row
            // (name or type column) reaches us — GUI.Button swallows MouseDown over its
            // rect regardless of button, which would otherwise eat right-clicks on the name.
            if (e.scope == "Vanilla" && Event.current.type == EventType.MouseDown
                && Event.current.button == 1 && row.Contains(mouse))
            {
                Event.current.Use();
                var entry = e;
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Import (Override from Archives)"), false, () => ImportVanilla(entry));
                menu.ShowAsContext();
            }

            bool selected = e.obj == _selected;
            bool hover = row.Contains(mouse);
            if (hover) hoveredThisPass = i;
            if (selected)          EditorGUI.DrawRect(row, ROW_SEL);
            else if (hover)        EditorGUI.DrawRect(row, ROW_HOVER);
            else if ((i & 1) == 1) EditorGUI.DrawRect(row, ROW_ALT);

            float indent = _groupByType ? 14f : 0f;
            float typeW = _groupByType ? 0f : TYPE_COL_W;   // type column only in flat mode

            // Diagnostics badge — a colored dot for content rows with load-time issues, so the
            // problem files are findable at a glance (worst severity, cached by InspectorDiagnostics).
            DiagSeverity sev = e.isContent ? InspectorDiagnostics.WorstSeverity(e.obj) : DiagSeverity.None;
            float badgeW = sev != DiagSeverity.None ? 12f : 0f;
            if (sev != DiagSeverity.None)
            {
                Color c = sev == DiagSeverity.Crash ? new Color(0.90f, 0.27f, 0.22f) : new Color(0.95f, 0.75f, 0.15f);
                EditorGUI.DrawRect(new Rect(row.x + 3 + indent, row.y + (ROW_H - 7f) * 0.5f, 7f, 7f), c);
            }

            Rect nameRect = new Rect(row.x + 4 + indent + badgeW, row.y, row.width - 8 - indent - badgeW - typeW, row.height);
            string label = (e.scope == "Mod" ? "● " : "  ") + e.name;
            string tip = $"{e.typeName} ({e.scope})" + (sev == DiagSeverity.Crash ? "\n⛔ Will crash load — see the inspector Diagnostics panel"
                                                        : sev == DiagSeverity.Warn ? "\n⚠ Has warnings — see the inspector Diagnostics panel" : "");
            if (GUI.Button(nameRect, new GUIContent(label, tip), EditorStyles.label))
                Select(e.obj);

            if (!_groupByType)
            {
                Rect typeRect = new Rect(row.xMax - TYPE_COL_W - 4, row.y, TYPE_COL_W, row.height);
                GUI.Label(typeRect, new GUIContent(e.typeName, e.typeName), _typeColStyle);
            }

            EditorGUI.DrawRect(new Rect(0, row.yMax - 1, contentW, 1), ROW_LINE);
        }
        GUI.EndScrollView();

        // Only force a repaint when the hovered row actually changes (crossing a row boundary,
        // or entering/leaving the list) — not on every single MouseMove pixel, which is what
        // wantsMouseMove-driven Repaint() used to do for the whole window.
        if (Event.current.type == EventType.MouseMove && hoveredThisPass != _hoverIndex) Repaint();
        _hoverIndex = hoveredThisPass;

        if (toggleType != null)
        {
            if (_collapsed.Contains(toggleType)) _collapsed.Remove(toggleType);
            else _collapsed.Add(toggleType);
            BuildDisplay();
            Repaint();
        }

        GUILayout.EndArea();
    }

    void ImportVanilla(Entry e)
    {
        var imported = VanillaDatabaseMount.OverrideVanillaElement(e.obj);
        if (imported == null) return;
        Refresh();
        Select(imported);
    }

    void Select(UnityEngine.Object obj)
    {
        bool changed = _selected != obj;
        _selected = obj;
        if (_mode == ViewMode.ListOnly)
        {
            // Drive the user's docked Inspector; re-ping even on repeat click
            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
        }
        else if (changed)
        {
            if (_editor != null) { DestroyImmediate(_editor); _editor = null; }
            _inspScroll = Vector2.zero;
        }
        Repaint();
    }

    // ── Splitter ──────────────────────────────────────────────────────────────
    void DrawSplitter(Rect r)
    {
        EditorGUI.DrawRect(r, new Color(0, 0, 0, 0.3f));
        EditorGUIUtility.AddCursorRect(r, MouseCursor.ResizeHorizontal);
        var e = Event.current;
        if (e.type == EventType.MouseDown && r.Contains(e.mousePosition)) { _draggingSplit = true; e.Use(); }
        if (_draggingSplit && e.type == EventType.MouseDrag)
        {
            _leftWidth = Mathf.Clamp(e.mousePosition.x, 220f, position.width - 280f);
            Repaint(); e.Use();
        }
        if (e.type == EventType.MouseUp) _draggingSplit = false;
    }

    // ── Right: embedded inspector ─────────────────────────────────────────────
    void DrawRight(Rect area)
    {
        GUILayout.BeginArea(area);

        if (_selected == null)
        {
            EditorGUILayout.HelpBox("Select an element on the left to edit it.", MessageType.Info);
            GUILayout.EndArea();
            return;
        }

        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label(_selected.name, EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Ping", EditorStyles.toolbarButton, GUILayout.Width(50)))
        { EditorGUIUtility.PingObject(_selected); Selection.activeObject = _selected; }
        EditorGUILayout.EndHorizontal();

        if (_editor == null || _editor.target != _selected)
        {
            if (_editor != null) DestroyImmediate(_editor);
            _editor = Editor.CreateEditor(_selected);
        }

        // Constrain the embedded inspector to the pane width. Editors that size
        // controls to currentViewWidth (the window) overflow into a horizontal
        // scrollbar contained within this pane rather than the whole window.
        float prevLabel = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = Mathf.Clamp(area.width * 0.38f, 110f, 200f);

        _inspScroll = EditorGUILayout.BeginScrollView(_inspScroll);
        EditorGUILayout.BeginVertical(GUILayout.Width(area.width - 24));
        try { _editor.OnInspectorGUI(); }
        catch (Exception ex)
        {
            EditorGUILayout.HelpBox(
                "This element's custom inspector failed to render embedded (often an " +
                "Odin editor expecting the real Inspector). Use Ping to edit it in the " +
                "docked Inspector.\n\n" + ex.Message, MessageType.Warning);
        }
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndScrollView();

        EditorGUIUtility.labelWidth = prevLabel;
        GUILayout.EndArea();
    }

    // ── Window placement ──────────────────────────────────────────────────────
    // Unity exposes no public dock/undock API, so we move the window by closing
    // and reopening it with the desired docking hint. This uses only public API,
    // is stable across Unity updates, and the static scan cache means reopening
    // is instant (no rescan, no progress bar).
    void ApplyWindowPlacement()
    {
        EditorApplication.delayCall -= DoApplyWindowPlacement;
        EditorApplication.delayCall += DoApplyWindowPlacement;
    }

    void DoApplyWindowPlacement()
    {
        s_suppressPlacement = true;
        try
        {
            bool wantDocked = _mode == ViewMode.ListOnly;
            if (wantDocked == docked) return; // already in the right placement
            if (wantDocked)
            {
                Type hostType = FindDockedNeighborType();
                Close();
                // desiredDockNextTo makes GetWindow dock as a tab next to that window.
                GetWindow<DatabaseBrowser>("Database Browser", true, hostType);
            }
            else // Window — reopen as a free-floating, movable window
            {
                Close();
                GetWindow<DatabaseBrowser>("Database Browser", true);
            }
        }
        catch (Exception e) { Debug.LogError($"[DatabaseBrowser] placement failed: {e}"); }
        finally { s_suppressPlacement = false; }
    }

    static Type FindDockedNeighborType()
    {
        // Prefer docking next to Project / Hierarchy / Console so the List tab
        // lands in the same dock column as those, matching the standard layout.
        foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
        {
            if (w == null || !w.docked) continue;
            string n = w.GetType().Name;
            if (n == "ProjectBrowser" || n == "SceneHierarchyWindow" || n == "ConsoleWindow")
                return w.GetType();
        }
        return null;
    }

    // ── Searchable type dropdown ──────────────────────────────────────────────
    class TypeDropdown : AdvancedDropdown
    {
        readonly List<string> _types;
        readonly Action<string> _onPick;
        public TypeDropdown(AdvancedDropdownState state, List<string> types, Action<string> onPick) : base(state)
        {
            _types = types; _onPick = onPick;
            minimumSize = new Vector2(240, 320);
        }
        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem("Type");
            root.AddChild(new AdvancedDropdownItem("(any)"));
            foreach (var t in _types) root.AddChild(new AdvancedDropdownItem(t));
            return root;
        }
        protected override void ItemSelected(AdvancedDropdownItem item)
            => _onPick(item.name == "(any)" ? "" : item.name);
    }
}