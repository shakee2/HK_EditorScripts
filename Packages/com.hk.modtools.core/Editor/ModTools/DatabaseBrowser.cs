using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HK.ModTools.Shared;
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
        public string folderPath; // directory of the asset / vanilla collection (grouping key)
        public bool isContent; // implements IDatatableElement — a real moddable data row, not build/plugin/config plumbing
        public bool isDup;     // My Mod content: same (type, name) on 2+ distinct project objects
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
    enum GroupMode { None, Type, Folder }
    static readonly string[] GROUP_LABELS = { "None", "Type", "Folder" };
    GroupMode _groupMode = GroupMode.None;
    HashSet<string> _collapsed = new();
    enum ScopeFilter { All, MyMod, Vanilla, Dupes }
    static readonly string[] SCOPE_LABELS = { "All", "My Mod", "Vanilla", "Dupes !" };
    ScopeFilter _scope = ScopeFilter.All;
    bool _contentOnly = true;   // hide build/plugin/config ScriptableObjects; show only actual data rows

    // prefs keys
    const string SearchKey     = "DatabaseBrowser.Search";
    const string TypeFilterKey = "DatabaseBrowser.TypeFilter";
    const string GroupKey      = "DatabaseBrowser.GroupByType"; // legacy bool
    const string GroupModeKey  = "DatabaseBrowser.GroupMode";
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

    // selection + embedded inspector (_selected = primary / inspector target; set = multi)
    UnityEngine.Object _selected;
    readonly HashSet<UnityEngine.Object> _selectedSet = new();
    int _anchorDisplayIndex = -1; // shift-range anchor in _display
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
        if (EditorPrefs.HasKey(GroupModeKey))
            _groupMode = (GroupMode)EditorPrefs.GetInt(GroupModeKey, 0);
        else if (EditorPrefs.GetBool(GroupKey, false))
            _groupMode = GroupMode.Type;
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
        EditorPrefs.SetInt(GroupModeKey, (int)_groupMode);
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
        if (obj == null) return;
        _selected = obj;
        _selectedSet.Clear();
        _selectedSet.Add(obj);
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
        {
            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                clipping = TextClipping.Clip,
                alignment = TextAnchor.MiddleLeft,
            };
        }
    }

    // ── Scan ──────────────────────────────────────────────────────────────────
    void Refresh(bool force = false)
    {
        if (force || s_cachedAll == null) s_cachedAll = ScanAll();
        _all = s_cachedAll.OrderBy(e => e.typeName).ThenBy(e => e.name).ToList();
        MarkModDupes(_all);
        // Drop destroyed / gone refs (e.g. after delete) so multi-select stays valid.
        _selectedSet.RemoveWhere(o => o == null);
        if (_selected == null || !_selectedSet.Contains(_selected))
            _selected = _selectedSet.Count > 0 ? _selectedSet.First() : null;
        ApplyFilters();
    }

    // Element identity at load is (type, name). Flag My Mod content rows that collide with
    // another distinct project object of the same type+name (vanilla never marked).
    static void MarkModDupes(List<Entry> all)
    {
        var identities = new Dictionary<(string type, string name), HashSet<int>>();
        foreach (var e in all)
        {
            e.isDup = false;
            if (e.scope != "Mod" || !e.isContent || e.obj == null) continue;
            var key = (e.typeName, e.name);
            if (!identities.TryGetValue(key, out var set)) identities[key] = set = new();
            set.Add(e.obj.GetInstanceID());
        }
        foreach (var e in all)
        {
            if (e.scope != "Mod" || !e.isContent || e.obj == null) continue;
            if (identities.TryGetValue((e.typeName, e.name), out var set) && set.Count >= 2)
                e.isDup = true;
        }
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
                    list.Add(new Entry
                    {
                        obj = obj, name = obj.name, typeName = obj.GetType().Name, scope = "Mod",
                        folderPath = FolderOfModAsset(path),
                        isContent = obj is Amplitude.Framework.IDatatableElement
                    });
                }
            }

            EditorUtility.DisplayProgressBar("Database Browser", "Loading vanilla databases…", 1f);
            foreach (var obj in VanillaDatabaseMount.LoadAllOfType(typeof(ScriptableObject)))
                list.Add(new Entry
                {
                    obj = obj, name = obj.name, typeName = obj.GetType().Name, scope = "Vanilla",
                    folderPath = FolderOfVanillaAsset(obj),
                    isContent = obj is Amplitude.Framework.IDatatableElement
                });
        }
        finally { EditorUtility.ClearProgressBar(); }
        return list;
    }

    static string FolderOfModAsset(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return "(unknown)";
        string dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
        return string.IsNullOrEmpty(dir) ? assetPath : dir;
    }

    static string FolderOfVanillaAsset(UnityEngine.Object obj)
    {
        if (VanillaDatabaseMount.TryGetOwnerDescriptor(obj, out var d) && !string.IsNullOrEmpty(d.FilePath))
        {
            string dir = Path.GetDirectoryName(d.FilePath)?.Replace('\\', '/');
            return string.IsNullOrEmpty(dir) ? d.FilePath.Replace('\\', '/') : dir;
        }
        return "(vanilla)";
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
             (_scope == ScopeFilter.Vanilla && e.scope == "Vanilla") ||
             (_scope == ScopeFilter.Dupes && e.isDup)) &&
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
        if (_groupMode == GroupMode.None)
        {
            foreach (var e in _view) _display.Add(new DI { entry = e });
            return;
        }

        Func<Entry, string> keyOf = _groupMode == GroupMode.Type
            ? e => e.typeName
            : e => string.IsNullOrEmpty(e.folderPath) ? "(unknown)" : e.folderPath;

        foreach (var g in _view.GroupBy(keyOf).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool collapsed = _collapsed.Contains(g.Key);
            _display.Add(new DI { isHeader = true, headerType = g.Key, count = g.Count(), collapsed = collapsed });
            if (!collapsed)
            {
                IEnumerable<Entry> rows = _groupMode == GroupMode.Folder
                    ? g.OrderBy(x => x.typeName).ThenBy(x => x.name)
                    : g.OrderBy(x => x.name);
                foreach (var e in rows)
                    _display.Add(new DI { entry = e });
            }
        }
    }

    // Truncate from the left so long folder headers keep the leaf path + count visible.
    static string FitTextKeepEnd(GUIStyle style, string text, float maxWidth)
    {
        if (maxWidth <= 0f || string.IsNullOrEmpty(text)) return text ?? "";
        if (style.CalcSize(new GUIContent(text)).x <= maxWidth) return text;
        const string ell = "…";
        float ellW = style.CalcSize(new GUIContent(ell)).x;
        if (ellW >= maxWidth) return ell;
        int lo = 1, hi = text.Length, best = 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            string candidate = ell + text.Substring(text.Length - mid);
            if (style.CalcSize(new GUIContent(candidate)).x <= maxWidth) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ell + text.Substring(text.Length - best);
    }

    // ── GUI ───────────────────────────────────────────────────────────────────
    void OnGUI()
    {
        if (WindowMinimize.DrawMinimizedChrome(this)) return;
        InitStyles();

        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        EditorGUI.BeginChangeCheck();
        _mode = (ViewMode)GUILayout.Toolbar((int)_mode, MODE_LABELS, EditorStyles.toolbarButton, GUILayout.Width(150));
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetInt(ModeKey, (int)_mode);
            if (_mode == ViewMode.ListOnly)
            {
                WindowMinimize.ForceRestore(this);
                if (_editor != null) { DestroyImmediate(_editor); _editor = null; }
            }
            ApplyWindowPlacement();
        }
        GUILayout.Space(8);
        if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70))) Refresh(true);
        if (GUILayout.Button("Save Assets", EditorStyles.toolbarButton, GUILayout.Width(90))) AssetDatabase.SaveAssets();
        GUILayout.FlexibleSpace();
        string counts = _selectedSet.Count > 1
            ? $"{_selectedSet.Count} selected · {_view.Count} / {_all.Count}"
            : $"{_view.Count} / {_all.Count}";
        GUILayout.Label(counts, EditorStyles.miniLabel);
        if (_mode == ViewMode.Window)
            WindowMinimize.DrawToolbarButton(this);
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

        // Searchable type dropdown + group mode
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Type", GUILayout.Width(40));
        if (GUILayout.Button(_typeFilter.Length == 0 ? "(any)" : _typeFilter, EditorStyles.popup))
        {
            var dd = new TypeDropdown(_typeDdState, _types, picked => { _typeFilter = picked; ApplyFilters(); });
            dd.Show(GUILayoutUtility.GetLastRect());
        }
        EditorGUILayout.LabelField("Group", GUILayout.Width(40));
        EditorGUI.BeginChangeCheck();
        var gm = (GroupMode)EditorGUILayout.Popup((int)_groupMode, GROUP_LABELS, GUILayout.Width(70));
        if (EditorGUI.EndChangeCheck() && gm != _groupMode)
        {
            _groupMode = gm;
            _collapsed.Clear();
            EditorPrefs.SetInt(GroupModeKey, (int)_groupMode);
            BuildDisplay();
        }
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
                // Foldout arrow + clipped label (paths are long; keep the right side so the
                // leaf folder + count stay visible; full text is in the tooltip).
                Rect foldRect = new Rect(row.x + 2, row.y, 14f, row.height);
                bool expanded = EditorGUI.Foldout(foldRect, !di.collapsed, GUIContent.none, true);
                Rect labelRect = new Rect(foldRect.xMax, row.y, row.xMax - foldRect.xMax - 4f, row.height);
                string full = $"{di.headerType}  ({di.count})";
                string shown = FitTextKeepEnd(_headerStyle, full, labelRect.width);
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0
                    && labelRect.Contains(mouse))
                {
                    expanded = !expanded;
                    Event.current.Use();
                }
                GUI.Label(labelRect, new GUIContent(shown, full), _headerStyle);
                if (expanded == di.collapsed) toggleType = di.headerType; // state flipped
                EditorGUI.DrawRect(new Rect(0, row.yMax - 1, contentW, 1), ROW_LINE);
                continue;
            }

            var e = di.entry;

            // MouseDown on the whole row (before Label) so Ctrl/Shift multi-select and
            // right-click context work on name + type columns alike.
            if (Event.current.type == EventType.MouseDown && row.Contains(mouse))
            {
                if (Event.current.button == 0)
                {
                    SelectClick(e, i);
                    Event.current.Use();
                }
                else if (Event.current.button == 1)
                {
                    Event.current.Use();
                    // Standard list behavior: right-click outside selection replaces it.
                    if (!_selectedSet.Contains(e.obj))
                        SelectOnly(e, i);
                    ShowRowContextMenu();
                }
            }

            bool selected = e.obj != null && _selectedSet.Contains(e.obj);
            bool hover = row.Contains(mouse);
            if (hover) hoveredThisPass = i;
            if (selected)          EditorGUI.DrawRect(row, ROW_SEL);
            else if (hover)        EditorGUI.DrawRect(row, ROW_HOVER);
            else if ((i & 1) == 1) EditorGUI.DrawRect(row, ROW_ALT);

            float indent = _groupMode != GroupMode.None ? 14f : 0f;
            float typeW = _groupMode == GroupMode.Type ? 0f : TYPE_COL_W;   // type column when not grouping by type

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
            string label = (e.scope == "Mod" ? "● " : "  ") + (e.isDup ? "!" : "") + e.name;
            string tip = $"{e.typeName} ({e.scope})"
                + (string.IsNullOrEmpty(e.folderPath) ? "" : "\n" + e.folderPath)
                + (e.isDup ? "\n! Duplicate (type, name) in My Mod — load-order collision" : "")
                + (sev == DiagSeverity.Crash ? "\n⛔ Will crash load — see the inspector Diagnostics panel"
                   : sev == DiagSeverity.Warn ? "\n⚠ Has warnings — see the inspector Diagnostics panel" : "");
            GUI.Label(nameRect, new GUIContent(label, tip), EditorStyles.label);

            if (_groupMode != GroupMode.Type)
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

    void ShowRowContextMenu()
    {
        var selected = GetSelectedEntries();
        var vanilla = selected.Where(x => x.scope == "Vanilla").ToList();
        var mod = selected.Where(x => x.scope == "Mod").ToList();
        var menu = new GenericMenu();

        if (vanilla.Count > 0)
        {
            string label = vanilla.Count == 1
                ? "Import (Override from Archives)"
                : $"Import {vanilla.Count} Selected (Override from Archives)";
            menu.AddItem(new GUIContent(label), false, () => ImportVanillaMany(vanilla));
        }

        if (mod.Count > 0)
        {
            bool anyDeletable = mod.Any(CanDeleteModEntry);
            string label = mod.Count == 1 ? "Delete" : $"Delete {mod.Count} Selected";
            if (anyDeletable)
                menu.AddItem(new GUIContent(label), false, () => DeleteModEntries(mod));
            else
                menu.AddDisabledItem(new GUIContent(label));
        }

        if (menu.GetItemCount() > 0) menu.ShowAsContext();
    }

    List<Entry> GetSelectedEntries()
    {
        if (_selectedSet.Count == 0) return new List<Entry>();
        // Preserve display order so mass ops feel predictable.
        var list = new List<Entry>(_selectedSet.Count);
        foreach (var di in _display)
        {
            if (di.isHeader || di.entry?.obj == null) continue;
            if (_selectedSet.Contains(di.entry.obj)) list.Add(di.entry);
        }
        // Anything selected but filtered out of _display still counts.
        if (list.Count < _selectedSet.Count)
        {
            foreach (var e in _all)
            {
                if (e.obj != null && _selectedSet.Contains(e.obj) && list.All(x => x.obj != e.obj))
                    list.Add(e);
            }
        }
        return list;
    }

    void ImportVanillaMany(List<Entry> entries)
    {
        if (entries == null || entries.Count == 0) return;
        UnityEngine.Object last = null;
        int ok = 0, fail = 0;
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e?.obj == null) { fail++; continue; }
                if (entries.Count > 1 && EditorUtility.DisplayCancelableProgressBar(
                        "Import from Archives",
                        $"Importing {e.name} ({i + 1}/{entries.Count})…",
                        (i + 1) / (float)entries.Count))
                    break;
                var imported = VanillaDatabaseMount.OverrideVanillaElement(e.obj);
                if (imported != null) { ok++; last = imported; }
                else fail++;
            }
        }
        finally { EditorUtility.ClearProgressBar(); }

        Refresh(true);
        if (last != null)
        {
            var entry = FindEntry(last);
            if (entry != null) SelectOnly(entry, -1);
            else SelectOnly(new Entry { obj = last, name = last.name, scope = "Mod" }, -1);
        }
        if (fail > 0)
            Debug.LogWarning($"[DatabaseBrowser] Import finished: {ok} ok, {fail} failed.");
        else if (ok > 1)
            Debug.Log($"[DatabaseBrowser] Imported {ok} element(s) from archives.");
    }

    static bool CanDeleteModEntry(Entry e)
    {
        if (e?.obj == null || e.scope != "Mod") return false;
        string path = AssetDatabase.GetAssetPath(e.obj);
        return !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
    }

    // Deletes My Mod project asset(s). Collection rows are usually sub-assets inside a
    // DatatableElementCollection .asset — remove that object only. If the row is the main
    // asset, delete the whole file (and warn when siblings would go with it).
    void DeleteModEntries(List<Entry> entries)
    {
        var deletable = entries?.Where(CanDeleteModEntry).ToList();
        if (deletable == null || deletable.Count == 0)
        {
            EditorUtility.DisplayDialog("Database Browser",
                "None of the selected items are under Assets/ and can be deleted from here.", "OK");
            return;
        }

        string msg = deletable.Count == 1
            ? BuildDeleteConfirmMessage(deletable[0])
            : $"Delete {deletable.Count} selected project assets?\n\n"
              + string.Join("\n", deletable.Take(12).Select(e => "• " + e.name))
              + (deletable.Count > 12 ? $"\n… and {deletable.Count - 12} more" : "");
        if (!EditorUtility.DisplayDialog("Delete", msg, "Delete", "Cancel")) return;

        bool clearedPrimary = false;
        try
        {
            for (int i = 0; i < deletable.Count; i++)
            {
                var e = deletable[i];
                if (deletable.Count > 1 && EditorUtility.DisplayCancelableProgressBar(
                        "Delete", $"Deleting {e.name} ({i + 1}/{deletable.Count})…",
                        (i + 1) / (float)deletable.Count))
                    break;

                string path = AssetDatabase.GetAssetPath(e.obj);
                var main = AssetDatabase.LoadMainAssetAtPath(path);
                bool isMain = main == e.obj;
                if (_selectedSet.Contains(e.obj)) _selectedSet.Remove(e.obj);
                if (_selected == e.obj) clearedPrimary = true;

                if (isMain)
                {
                    if (!AssetDatabase.DeleteAsset(path))
                        Debug.LogError($"[DatabaseBrowser] Failed to delete: {path}");
                }
                else
                {
                    AssetDatabase.RemoveObjectFromAsset(e.obj);
                    DestroyImmediate(e.obj, true);
                }
            }
            AssetDatabase.SaveAssets();
        }
        finally { EditorUtility.ClearProgressBar(); }

        if (clearedPrimary || _selected == null || !_selectedSet.Contains(_selected))
        {
            _selected = _selectedSet.Count > 0 ? _selectedSet.First() : null;
            if (_editor != null) { DestroyImmediate(_editor); _editor = null; }
            if (_mode == ViewMode.ListOnly)
                Selection.objects = _selectedSet.Count > 0 ? _selectedSet.ToArray() : Array.Empty<UnityEngine.Object>();
        }
        Refresh(true);
    }

    static string BuildDeleteConfirmMessage(Entry e)
    {
        string path = AssetDatabase.GetAssetPath(e.obj);
        var main = AssetDatabase.LoadMainAssetAtPath(path);
        bool isMain = main == e.obj;
        var siblings = AssetDatabase.LoadAllAssetsAtPath(path);
        int otherCount = siblings == null ? 0 : siblings.Count(o => o != null && o != e.obj);
        return isMain && otherCount > 0
            ? $"Delete '{e.name}' and {otherCount} other object(s) in:\n{path}?"
            : $"Delete '{e.name}'?\n{path}";
    }

    Entry FindEntry(UnityEngine.Object obj)
    {
        if (obj == null) return null;
        return _all.FirstOrDefault(e => e.obj == obj);
    }

    void SelectClick(Entry e, int displayIndex)
    {
        if (e?.obj == null) return;
        bool ctrl = Event.current.control || Event.current.command;
        bool shift = Event.current.shift;

        if (shift && _anchorDisplayIndex >= 0 && _anchorDisplayIndex < _display.Count)
        {
            _selectedSet.Clear();
            int a = Mathf.Min(_anchorDisplayIndex, displayIndex);
            int b = Mathf.Max(_anchorDisplayIndex, displayIndex);
            for (int i = a; i <= b; i++)
            {
                if (_display[i].isHeader || _display[i].entry?.obj == null) continue;
                _selectedSet.Add(_display[i].entry.obj);
            }
            SetPrimary(e.obj);
        }
        else if (ctrl)
        {
            if (_selectedSet.Contains(e.obj))
            {
                _selectedSet.Remove(e.obj);
                SetPrimary(_selectedSet.Count > 0 ? (_selectedSet.Contains(_selected) ? _selected : _selectedSet.First()) : null);
            }
            else
            {
                _selectedSet.Add(e.obj);
                SetPrimary(e.obj);
            }
            _anchorDisplayIndex = displayIndex;
        }
        else
        {
            SelectOnly(e, displayIndex);
            return;
        }
        SyncUnitySelection();
        Repaint();
    }

    void SelectOnly(Entry e, int displayIndex)
    {
        _selectedSet.Clear();
        if (e?.obj != null) _selectedSet.Add(e.obj);
        SetPrimary(e?.obj);
        if (displayIndex >= 0) _anchorDisplayIndex = displayIndex;
        SyncUnitySelection();
        Repaint();
    }

    void SetPrimary(UnityEngine.Object obj)
    {
        bool changed = _selected != obj;
        _selected = obj;
        if (_mode == ViewMode.Window && changed)
        {
            if (_editor != null) { DestroyImmediate(_editor); _editor = null; }
            _inspScroll = Vector2.zero;
        }
    }

    void SyncUnitySelection()
    {
        if (_mode != ViewMode.ListOnly) return;
        if (_selectedSet.Count == 0)
        {
            Selection.activeObject = null;
            return;
        }
        Selection.objects = _selectedSet.ToArray();
        if (_selected != null) EditorGUIUtility.PingObject(_selected);
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
        string title = _selectedSet.Count > 1 ? $"{_selected.name}  (+{_selectedSet.Count - 1} more)" : _selected.name;
        GUILayout.Label(title, EditorStyles.boldLabel);
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