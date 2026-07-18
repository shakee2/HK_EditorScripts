using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Side-by-side inspector of one element across the mods that define it, plus (if present) the
    /// version already in Assets/Databases/Patch/. A searchable list on the left switches which element
    /// is compared (type filter + optional group-by-type, same pattern as Database Browser). Each column
    /// stacks the element then its matching UIMapper/DescriptorMapper(s), each headed with concrete type
    /// + element name. Source columns use a session repository: each (mod, element) is staged once and
    /// kept until the window closes — mid-session DeleteAsset/ImportAsset/SaveAssets would bump Amplitude
    /// DatatableElementCache.CacheRevisionIndex and force BuildCacheBuilderEntry (~0.4s) on every
    /// switch. Assetbundle sources use the mounted live object (no scratch file). The Patch column is
    /// the real project asset (also repo-cached); rebuilt only after Import. Compare Import brings only
    /// the primary element (not mappers).
    /// </summary>
    public class CompatCompareWindow : EditorWindow
    {
        public class CompareItem
        {
            public string name;
            public string typeHint;
            /// <summary>Friendly type for filter/group (resolved class name when known, else typeHint).</summary>
            public string typeName;
            public List<(string mod, HkMod modObj, HkElement el)> versions;
            public string winner;        // load-order winner mod name (last in versions)
            public bool odin;            // true if Odin element
            public List<Diff> diffs;     // pre-computed diffs (empty when there are no differences)
            public bool inPatch;         // true if an element with this name is already in Assets/Databases/Patch/
            public bool resolved;        // true if sidecar / Mark resolved accepted the winner (no import)
            /// <summary>Element Type key (guid:fileID or FullName) for matching the main-window row.</summary>
            public string typeKey;
        }

        class Panel
        {
            /// <summary>Concrete CLR type name (e.g. TechnologyDefinition), not the collection stem.</summary>
            public string typeName;
            public string elementName;
            public UnityEngine.Object obj;
            public Editor editor;
            public string stagePath;   // non-null → scratch (delete on close)
            public bool editable;
        }
        class Column
        {
            public string header;
            public readonly List<Panel> panels = new List<Panel>();
            public Vector2 scroll;
            public HkMod mod;       // non-null for source columns; null for the Patch column
            public HkElement el;    // the primary element this column represents (used by the Import button)
        }

        // Flat or grouped virtualized list rows (headers + element rows).
        class DisplayRow
        {
            public bool isHeader;
            public string headerType;
            public int count;
            public bool collapsed;
            public int itemIndex; // into _items when !isHeader
        }

        Action<CompareItem> _onResolveAsWinner;
        List<CompareItem> _items = new List<CompareItem>();
        readonly List<int> _filtered = new List<int>();   // indices into _items matching search+type
        readonly List<DisplayRow> _display = new List<DisplayRow>();
        List<string> _types = new List<string>();
        string _typeFilter = "";
        bool _groupByType;
        readonly HashSet<string> _collapsed = new HashSet<string>();
        AdvancedDropdownState _typeDdState = new AdvancedDropdownState();
        bool _viewDirty = true;
        int _index = -1;
        string _search = "";
        Vector2 _listScroll;
        Vector2 _diffScroll;
        List<Diff> _displayDiffs;
        string _displayDiffWinner;
        bool _displayDiffOdin;
        bool _showWinnerOnly; // ExtraInWinner — noise when Patch imported the load-order winner
        bool _showResolvedDiffs; // carried per-diff Resolve rows
        readonly List<Column> _cols = new List<Column>();
        // Session repository: source + Patch objects/Editors kept until the window closes.
        // Mid-session ImportAsset/DeleteAsset/SaveAssets bumps Amplitude DatatableElementCache.CacheRevisionIndex
        // and forces BuildCacheBuilderEntry (~0.4s) on every inspector draw.
        readonly Dictionary<string, RepoEntry> _sourceRepo = new Dictionary<string, RepoEntry>();
        readonly Dictionary<string, RepoEntry> _patchRepo = new Dictionary<string, RepoEntry>();
        /// <summary>Parked columns (sources + Patch) keyed by <see cref="ItemKey"/>.</summary>
        readonly Dictionary<string, List<Column>> _columnsByItem = new Dictionary<string, List<Column>>();
        float _listWidth = 320f;
        bool _draggingSplit;
        GUIStyle _headerStyle;
        GUIStyle _panelTypeStyle;
        GUIStyle _panelNameStyle;

        class RepoEntry
        {
            public UnityEngine.Object Obj;
            public string StagePath; // null for assetbundle LiveObject / Patch project assets
            public Editor Editor;
        }

        const float LIST_ROW_H = 18f;
        const float TYPE_COL_W = 110f;
        const string PrefTypeFilter = "CompatCompare.TypeFilter";
        const string PrefGroupByType = "CompatCompare.GroupByType";
        const string PrefShowWinnerOnly = "CompatCompare.ShowWinnerOnly";
        const string PrefShowResolvedDiffs = "CompatCompare.ShowResolvedDiffs";
        static readonly Color LIST_SEL = new Color(0.3f, 0.5f, 0.9f, 0.28f);
        static readonly Color LIST_ALT = new Color(1f, 1f, 1f, 0.03f);
        static readonly Color HEADER_BG = new Color(0f, 0f, 0f, 0.18f);
        static readonly Color ROW_LINE = new Color(0f, 0f, 0f, 0.12f);

        public static void Show(List<CompareItem> items, int index, Action<CompareItem> onResolveAsWinner = null)
        {
            var w = GetWindow<CompatCompareWindow>(typeof(CompatPatcherWindow));
            w.titleContent = new GUIContent("Compare elements");
            // Keep the session repository across Show() — clearing would DeleteAsset and invalidate
            // Amplitude's DatatableElementCache for the rest of the session.
            w._items = items ?? new List<CompareItem>();
            w._onResolveAsWinner = onResolveAsWinner;
            w._typeFilter = EditorPrefs.GetString(PrefTypeFilter, "");
            w._groupByType = EditorPrefs.GetBool(PrefGroupByType, false);
            w._showWinnerOnly = EditorPrefs.GetBool(PrefShowWinnerOnly, false);
            w._showResolvedDiffs = EditorPrefs.GetBool(PrefShowResolvedDiffs, false);
            w._viewDirty = true;
            w.RebuildView();
            w.SelectItem(Mathf.Clamp(index, 0, w._items.Count - 1));
            w.Show();
        }

        void SelectItem(int i)
        {
            ParkCurrentColumns();
            _index = i;
            if (i < 0 || i >= _items.Count) return;
            var item = _items[i];
            string key = ItemKey(item);

            if (!string.IsNullOrEmpty(key) && _columnsByItem.TryGetValue(key, out var parked)
                && parked != null && parked.Count > 0)
            {
                _cols.AddRange(parked);
                parked.Clear();
                _columnsByItem.Remove(key);
            }
            else
            {
                BuildSourceColumns(item, _cols);
                AppendPatchColumn(item);
            }

            RecomputeDisplayDiffs(item);
            Repaint();
        }

        void BuildSourceColumns(CompareItem item, List<Column> into)
        {
            foreach (var (mod, modObj, el) in item.versions)
            {
                var col = new Column { header = mod, mod = modObj, el = el };
                AddFromRepo(col, modObj, el);
                foreach (var m in MatchingMappers(modObj, el)) AddFromRepo(col, modObj, m);
                if (col.panels.Count > 0) into.Add(col);
            }
        }

        void AppendPatchColumn(CompareItem item)
        {
            var patchCol = new Column { header = "Patch (editable)" };
            foreach (var (_, obj) in FindPatchObjects(item.name))
                AddPatchFromRepo(patchCol, obj);
            if (patchCol.panels.Count > 0) _cols.Add(patchCol);
        }

        static string ItemKey(CompareItem item) =>
            (item.typeKey ?? item.typeHint ?? "") + "|" + (item.name ?? "");

        static string RepoKey(HkMod mod, HkElement el) =>
            (mod?.Name ?? "") + "|" + (el?.TypeHint ?? "") + "|" + (el?.Name ?? "");

        static string PatchRepoKey(UnityEngine.Object obj) =>
            "patch|" + (obj != null ? obj.GetInstanceID().ToString() : "0");

        /// <summary>
        /// Detach all visible columns (sources + Patch) for reuse. Never destroys Editors or SaveAssets —
        /// that was bumping Amplitude DatatableElementCache.CacheRevisionIndex on every switch.
        /// </summary>
        void ParkCurrentColumns()
        {
            if (_cols.Count == 0) return;
            if (_index >= 0 && _index < _items.Count)
            {
                string key = ItemKey(_items[_index]);
                if (!string.IsNullOrEmpty(key))
                    _columnsByItem[key] = new List<Column>(_cols);
            }
            _cols.Clear();
        }

        static IEnumerable<HkElement> MatchingMappers(HkMod mod, HkElement el) =>
            mod.Elements.Values.Where(e => e.Name == el.Name && e.Type != el.Type && !e.IsRoot);

        void AddFromRepo(Column col, HkMod mod, HkElement el)
        {
            var entry = GetOrCreateRepoEntry(mod, el);
            if (entry?.Obj == null) return;
            col.panels.Add(new Panel
            {
                typeName = entry.Obj.GetType().Name,
                elementName = entry.Obj.name,
                obj = entry.Obj,
                editor = entry.Editor,
                stagePath = null,
                editable = false,
            });
        }

        void AddPatchFromRepo(Column col, UnityEngine.Object obj)
        {
            var entry = GetOrCreatePatchRepoEntry(obj);
            if (entry?.Obj == null) return;
            col.panels.Add(new Panel
            {
                typeName = entry.Obj.GetType().Name,
                elementName = entry.Obj.name,
                obj = entry.Obj,
                editor = entry.Editor,
                stagePath = null,
                editable = true,
            });
        }

        RepoEntry GetOrCreateRepoEntry(HkMod mod, HkElement el)
        {
            string key = RepoKey(mod, el);
            if (_sourceRepo.TryGetValue(key, out var existing))
            {
                if (existing.Obj == null) { _sourceRepo.Remove(key); }
                else
                {
                    if (existing.Editor == null)
                        existing.Editor = Editor.CreateEditor(existing.Obj);
                    return existing;
                }
            }

            var (obj, _, stage) = PatchBuilder.StageElement(mod, el);
            if (obj == null)
            {
                PatchBuilder.CleanupStage(stage);
                return null;
            }
            PatchBuilder.PinStage(stage);
            var entry = new RepoEntry
            {
                Obj = obj,
                StagePath = stage,
                Editor = Editor.CreateEditor(obj),
            };
            _sourceRepo[key] = entry;
            return entry;
        }

        RepoEntry GetOrCreatePatchRepoEntry(UnityEngine.Object obj)
        {
            if (obj == null) return null;
            string key = PatchRepoKey(obj);
            if (_patchRepo.TryGetValue(key, out var existing))
            {
                if (existing.Obj == null) { _patchRepo.Remove(key); }
                else
                {
                    if (existing.Editor == null)
                        existing.Editor = Editor.CreateEditor(existing.Obj);
                    return existing;
                }
            }
            var entry = new RepoEntry
            {
                Obj = obj,
                StagePath = null,
                Editor = Editor.CreateEditor(obj),
            };
            _patchRepo[key] = entry;
            return entry;
        }

        /// <summary>Drop Patch Editors for this element name so the next Append picks up post-Import objects.</summary>
        void InvalidatePatchRepo(string elementName)
        {
            if (string.IsNullOrEmpty(elementName)) return;
            var doomed = new List<string>();
            foreach (var kv in _patchRepo)
            {
                if (kv.Value?.Obj != null && kv.Value.Obj.name == elementName)
                    doomed.Add(kv.Key);
            }
            foreach (var key in doomed)
            {
                var e = _patchRepo[key];
                if (e?.Editor != null) DestroyImmediate(e.Editor);
                _patchRepo.Remove(key);
            }

            // Parked columns may still hold the old Patch panels — strip them.
            foreach (var parked in _columnsByItem.Values)
            {
                if (parked == null) continue;
                for (int i = parked.Count - 1; i >= 0; i--)
                {
                    if (parked[i].mod == null) parked.RemoveAt(i);
                }
            }
        }

        static IEnumerable<(string path, UnityEngine.Object obj)> FindPatchObjects(string elementName)
        {
            if (!Directory.Exists(PatchBuilder.PatchDir)) yield break;
            foreach (var f in Directory.EnumerateFiles(PatchBuilder.PatchDir, "*.asset", SearchOption.AllDirectories))
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(f))
                    if (o != null && o.name == elementName) yield return (f, o);
        }

        void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            DrawList();
            DrawSplitter();
            DrawColumns();
            EditorGUILayout.EndHorizontal();
        }

        void DrawSplitter()
        {
            var rect = GUILayoutUtility.GetRect(5, 5, GUILayout.ExpandHeight(true), GUILayout.Width(5));
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.25f));
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);
            var e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition)) _draggingSplit = true;
            if (_draggingSplit)
            {
                if (e.type == EventType.MouseDrag) { _listWidth = Mathf.Clamp(_listWidth + e.delta.x, 180f, position.width - 200f); Repaint(); }
                if (e.type == EventType.MouseUp) _draggingSplit = false;
            }
        }

        static string ItemType(CompareItem it) =>
            !string.IsNullOrEmpty(it.typeName) ? it.typeName
            : !string.IsNullOrEmpty(it.typeHint) ? it.typeHint
            : "?";

        void RebuildView()
        {
            _types = _items.Select(ItemType).Where(t => !string.IsNullOrEmpty(t) && t != "?")
                .Distinct().OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            if (_typeFilter.Length > 0 && !_types.Contains(_typeFilter)) _typeFilter = "";

            _filtered.Clear();
            string q = _search.Trim().ToLowerInvariant();
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (_typeFilter.Length > 0 && ItemType(it) != _typeFilter) continue;
                if (q.Length > 0 && !(it.name ?? "").ToLowerInvariant().Contains(q)) continue;
                _filtered.Add(i);
            }

            _display.Clear();
            if (!_groupByType)
            {
                foreach (int i in _filtered)
                    _display.Add(new DisplayRow { itemIndex = i });
            }
            else
            {
                foreach (var g in _filtered.GroupBy(i => ItemType(_items[i]))
                                           .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    bool collapsed = _collapsed.Contains(g.Key);
                    _display.Add(new DisplayRow
                    {
                        isHeader = true,
                        headerType = g.Key,
                        count = g.Count(),
                        collapsed = collapsed,
                    });
                    if (!collapsed)
                        foreach (int i in g.OrderBy(i => _items[i].name, StringComparer.OrdinalIgnoreCase))
                            _display.Add(new DisplayRow { itemIndex = i });
                }
            }
            _viewDirty = false;
        }

        void DrawList()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(EditorStyles.foldout)
                {
                    fontStyle = FontStyle.Bold,
                    clipping = TextClipping.Clip,
                };
            }

            EditorGUILayout.BeginVertical(GUILayout.Width(_listWidth));

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _search = EditorGUILayout.TextField(_search);
            if (EditorGUI.EndChangeCheck()) _viewDirty = true;
            if (GUILayout.Button("x", EditorStyles.miniButton, GUILayout.Width(18)) && _search.Length > 0)
            { _search = ""; GUI.FocusControl(null); _viewDirty = true; }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Type", GUILayout.Width(32));
            Rect typeBtn = GUILayoutUtility.GetRect(10, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
            string typeLabel = _typeFilter.Length == 0 ? "(any)" : _typeFilter;
            if (EditorGUI.DropdownButton(typeBtn, new GUIContent(typeLabel), FocusType.Keyboard, EditorStyles.popup))
            {
                var dd = new TypeDropdown(_typeDdState, _types, picked =>
                {
                    _typeFilter = picked;
                    EditorPrefs.SetString(PrefTypeFilter, _typeFilter);
                    _viewDirty = true;
                    Repaint();
                });
                AdvancedDropdownHeight.ShowCapped(dd, typeBtn);
            }
            bool g = GUILayout.Toggle(_groupByType, "Group", EditorStyles.miniButton, GUILayout.Width(54));
            if (g != _groupByType)
            {
                _groupByType = g;
                EditorPrefs.SetBool(PrefGroupByType, _groupByType);
                _viewDirty = true;
            }
            EditorGUILayout.EndHorizontal();

            if (_viewDirty) RebuildView();
            EditorGUILayout.LabelField($"{_filtered.Count} / {_items.Count}", EditorStyles.miniLabel);

            Rect area = GUILayoutUtility.GetRect(10, _listWidth, 10, 100000,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            Rect content = new Rect(0, 0, area.width - 16, _display.Count * LIST_ROW_H);
            _listScroll = GUI.BeginScrollView(area, _listScroll, content);

            int first = Mathf.Max(0, Mathf.FloorToInt(_listScroll.y / LIST_ROW_H));
            int last = Mathf.Min(_display.Count, Mathf.CeilToInt((_listScroll.y + area.height) / LIST_ROW_H) + 1);
            Vector2 mouse = Event.current.mousePosition;
            string toggleType = null;

            for (int vi = first; vi < last; vi++)
            {
                var di = _display[vi];
                Rect rr = new Rect(0, vi * LIST_ROW_H, content.width, LIST_ROW_H);

                if (di.isHeader)
                {
                    EditorGUI.DrawRect(rr, HEADER_BG);
                    bool expanded = EditorGUI.Foldout(
                        new Rect(rr.x + 2, rr.y, rr.width - 4, rr.height),
                        !di.collapsed, $"{di.headerType}  ({di.count})", true, _headerStyle);
                    if (expanded == di.collapsed) toggleType = di.headerType;
                    EditorGUI.DrawRect(new Rect(0, rr.yMax - 1, content.width, 1), ROW_LINE);
                    continue;
                }

                int i = di.itemIndex;
                if (i == _index) EditorGUI.DrawRect(rr, LIST_SEL);
                else if ((vi & 1) == 1) EditorGUI.DrawRect(rr, LIST_ALT);
                if (Event.current.type == EventType.MouseDown && rr.Contains(mouse))
                { SelectItem(i); Event.current.Use(); }

                float indent = _groupByType ? 14f : 0f;
                float typeW = _groupByType ? 0f : TYPE_COL_W;
                Rect label = new Rect(rr.x + 4 + indent, rr.y, rr.width - 4 - indent - typeW, rr.height);
                string tip = ItemType(_items[i]);
                string mark = _items[i].inPatch ? "● " : _items[i].resolved ? "○ " : "  ";
                string rowLabel = mark + _items[i].name;
                if (_items[i].inPatch) tip = "In patch\n" + tip;
                else if (_items[i].resolved) tip = "Resolved (winner OK)\n" + tip;
                GUI.Label(label, new GUIContent(rowLabel, tip),
                    i == _index ? EditorStyles.boldLabel : EditorStyles.label);
                if (!_groupByType)
                {
                    Rect typeRect = new Rect(rr.xMax - TYPE_COL_W, rr.y, TYPE_COL_W - 2, rr.height);
                    GUI.Label(typeRect, new GUIContent(tip, tip), EditorStyles.miniLabel);
                }
                EditorGUI.DrawRect(new Rect(0, rr.yMax - 1, content.width, 1), ROW_LINE);
            }
            GUI.EndScrollView();

            if (toggleType != null)
            {
                if (!_collapsed.Add(toggleType)) _collapsed.Remove(toggleType);
                _viewDirty = true;
                Repaint();
            }

            EditorGUILayout.EndVertical();
        }

        void DrawColumns()
        {
            EditorGUILayout.BeginVertical();
            if (_index < 0 || _index >= _items.Count) { EditorGUILayout.HelpBox("Select an element from the list.", MessageType.None); EditorGUILayout.EndVertical(); return; }
            var item = _items[_index];
            EnsurePanelStyles();
            EditorGUILayout.LabelField($"{item.name}   ·   {ItemType(item)}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Source columns are read-only; Patch is editable and saved on close. "
                + "Import brings only the primary element — not attached mappers.",
                EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(item.inPatch || item.resolved || _onResolveAsWinner == null))
            {
                if (GUILayout.Button($"Resolve as winner ({item.winner})", GUILayout.Width(280)))
                {
                    _onResolveAsWinner(item);
                    item.resolved = true;
                    Repaint();
                }
            }
            if (item.inPatch)
                EditorGUILayout.LabelField("● In patch", EditorStyles.miniLabel);
            else if (item.resolved)
                EditorGUILayout.LabelField("○ Resolved — winner accepted (sidecar)", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            DrawDiffSection(item);

            if (_cols.Count == 0) { EditorGUILayout.HelpBox("Nothing to compare (staging failed).", MessageType.Warning); EditorGUILayout.EndVertical(); return; }

            float w = (position.width - _listWidth - 24) / _cols.Count;
            float prevLabel = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Clamp(w * 0.38f, 110f, 200f);
            EditorGUILayout.BeginHorizontal();
            // Import refreshes _cols — must not mutate during this loop.
            Column pendingImport = null;
            for (int ci = 0; ci < _cols.Count; ci++)
            {
                var c = _cols[ci];
                EditorGUILayout.BeginVertical(GUILayout.Width(w));
                EditorGUILayout.LabelField(c.header, EditorStyles.boldLabel);
                if (c.mod != null && c.el != null)
                {
                    using (new EditorGUI.DisabledScope(item.inPatch))
                    {
                        if (GUILayout.Button("Import this version into Patch/", EditorStyles.miniButton, GUILayout.Width(w - 28)))
                            pendingImport = c;
                    }
                }
                c.scroll = EditorGUILayout.BeginScrollView(c.scroll);
                for (int pi = 0; pi < c.panels.Count; pi++)
                {
                    var p = c.panels[pi];
                    if (pi > 0) EditorGUILayout.Space(12);
                    DrawPanelHeader(p);
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    // DisabledScope is intentionally not used here: it would also disable the
                    // embedded inspector's tabs, but we need to be able to switch tabs even for
                    // read-only source columns. Staged source assets are scratch-only and never
                    // saved, so the read-only status is enforced by lifecycle, not by GUI locking.
                    EditorGUILayout.BeginVertical(GUILayout.Width(w - 28));
                    try { if (p.editor != null) p.editor.OnInspectorGUI(); }
                    catch (Exception ex) { EditorGUILayout.HelpBox("Embedded inspector failed: " + ex.Message, MessageType.Warning); }
                    EditorGUILayout.EndVertical();
                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUIUtility.labelWidth = prevLabel;
            EditorGUILayout.EndVertical();

            if (pendingImport != null)
                ImportThisVersion(pendingImport);
        }

        void EnsurePanelStyles()
        {
            if (_panelTypeStyle != null) return;
            _panelTypeStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14, wordWrap = true };
            _panelNameStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, wordWrap = true };
        }

        void DrawPanelHeader(Panel p)
        {
            EditorGUILayout.LabelField(p.typeName ?? "?", _panelTypeStyle);
            string name = p.elementName ?? "?";
            if (!p.editable) name += "  (read-only)";
            EditorGUILayout.LabelField(name, _panelNameStyle);
        }

        void DrawDiffSection(CompareItem item)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            bool showWo = GUILayout.Toggle(_showWinnerOnly, "Show winner-only", EditorStyles.miniButton, GUILayout.Width(120));
            if (showWo != _showWinnerOnly)
            {
                _showWinnerOnly = showWo;
                EditorPrefs.SetBool(PrefShowWinnerOnly, _showWinnerOnly);
            }
            int resolvedN = _displayDiffs?.Count(d => d.Status == "carried") ?? 0;
            if (resolvedN > 0)
            {
                bool showRes = GUILayout.Toggle(_showResolvedDiffs,
                    $"Show resolved ({resolvedN})", EditorStyles.miniButton, GUILayout.Width(130));
                if (showRes != _showResolvedDiffs)
                {
                    _showResolvedDiffs = showRes;
                    EditorPrefs.SetBool(PrefShowResolvedDiffs, _showResolvedDiffs);
                }
            }
            if (string.Equals(_displayDiffWinner, "Patch", StringComparison.Ordinal))
                EditorGUILayout.LabelField("Patch as winner — winner-only usually means already imported.", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
            float h = Mathf.Clamp(position.height * 0.28f, 70f, 260f);
            _diffScroll = EditorGUILayout.BeginScrollView(_diffScroll, GUILayout.Height(h));
            DiffGui.DrawTable(_displayDiffs, _displayDiffWinner, _displayDiffOdin,
                hideExtraInWinner: !_showWinnerOnly,
                onResolveDiff: d => ResolveDiff(item, d),
                hideResolved: !_showResolvedDiffs);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space(2);
        }

        void ResolveDiff(CompareItem item, Diff d)
        {
            if (item == null || d == null) return;
            if (string.IsNullOrEmpty(d.Sig) || string.IsNullOrEmpty(d.Fp))
            {
                ConflictAnalyzer.FinalizeDiffs(new[] { d },
                    item.typeKey ?? item.typeHint ?? "", item.name ?? "",
                    Sidecar.LoadIndex(Sidecar.DefaultPath));
            }
            Sidecar.UpsertDecision(Sidecar.DefaultPath, new Sidecar.Decision
            {
                sig = d.Sig,
                fp = d.Fp,
                choice = _displayDiffWinner ?? item.winner ?? "accepted",
                kind = "diff",
                element = item.name,
            });
            d.Status = "carried";
            d.Choice = _displayDiffWinner ?? item.winner;
            string assetPath = Sidecar.DefaultPath.Replace('\\', '/');
            if (assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            Repaint();
        }

        static List<HkElement> FindPatchHkElements(string elementName)
        {
            var result = new List<HkElement>();
            // Prefer live SerializedObject Flatten (same as assetbundle sources). YAML ParseElements
            // marks hybrid Odin+Unity assets as Odin and forced RefDiff ("MISSING refs[X]" for a
            // single ProductionCost swap).
            foreach (var (path, obj) in FindPatchObjects(elementName))
            {
                string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                var el = LiveElementBuilder.Build(obj, path, stem);
                if (el != null && !el.IsRoot) result.Add(el);
            }
            if (result.Count > 0) return result;

            if (!Directory.Exists(PatchBuilder.PatchDir)) return result;
            foreach (var f in Directory.EnumerateFiles(PatchBuilder.PatchDir, "*.asset", SearchOption.AllDirectories))
            {
                string text;
                try { text = File.ReadAllText(f); }
                catch { continue; }
                foreach (var el in ModReader.ParseElements(text, f))
                    if (el.Name == elementName && !el.IsRoot)
                        result.Add(el);
            }
            return result;
        }

        void RecomputeDisplayDiffs(CompareItem item)
        {
            var patchEls = FindPatchHkElements(item.name);
            if (patchEls.Count == 0)
            {
                _displayDiffs = item.diffs != null ? new List<Diff>(item.diffs) : new List<Diff>();
                ConflictAnalyzer.FinalizeDiffs(_displayDiffs,
                    item.typeKey ?? item.typeHint ?? "",
                    item.name ?? "",
                    Sidecar.LoadIndex(Sidecar.DefaultPath));
                _displayDiffWinner = item.winner;
                _displayDiffOdin = item.odin;
                return;
            }

            var winner = patchEls.FirstOrDefault(e => e.TypeHint == item.typeHint
                || string.Equals(e.TypeHint, item.typeName, StringComparison.OrdinalIgnoreCase))
                ?? patchEls[0];
            // Rebuild losers from live objects so Flatten matches Patch. Stale analyze-time
            // HkElements often lack Unity fields → false "ONLY in Patch" after Import from winner.
            var losers = new Dictionary<string, HkElement>();
            foreach (var (mod, modObj, el) in item.versions)
                losers[mod] = LiveHkElement(modObj, el) ?? el;

            _displayDiffs = ConflictAnalyzer.ComputeDiffs(winner, losers);
            ConflictAnalyzer.FinalizeDiffs(_displayDiffs,
                item.typeKey ?? item.typeHint ?? winner.Type ?? "",
                item.name ?? winner.Name ?? "",
                Sidecar.LoadIndex(Sidecar.DefaultPath));
            _displayDiffWinner = "Patch";
            _displayDiffOdin = ConflictAnalyzer.UsesNameSetDiff(winner)
                               || losers.Values.Any(ConflictAnalyzer.UsesNameSetDiff);
        }

        /// <summary>Live Flatten for Compare diffs (bundle LiveObject or session-staged repo object).</summary>
        HkElement LiveHkElement(HkMod mod, HkElement el)
        {
            if (el == null) return null;
            UnityEngine.Object live = el.LiveObject;
            if (live == null && mod != null)
                live = GetOrCreateRepoEntry(mod, el)?.Obj;
            if (live == null) return null;
            return LiveElementBuilder.Build(live, el.SourcePath, el.TypeHint ?? live.GetType().Name);
        }

        // Imports only this column's primary element (not attached mappers — import those from their
        // own conflict rows if needed), then refreshes the Patch column in place.
        // Uses the repository live object so we do not Stage+CleanupStage (DeleteAsset would invalidate
        // Amplitude's DatatableElementCache while Compare is still open).
        void ImportThisVersion(Column c)
        {
            if (c?.mod == null || c.el == null) return;
            UnityEngine.Object live = c.panels.Count > 0 ? c.panels[0].obj : null;
            if (live == null)
                live = GetOrCreateRepoEntry(c.mod, c.el)?.Obj;
            if (live == null)
            {
                Debug.LogWarning("[CompatPatcher] Compare Import: no live object for " + c.el.Name);
                return;
            }
            if (PatchBuilder.ImportLive(live, c.el.TypeHint) == null) return;
            Debug.Log($"[CompatPatcher] Compare import: {c.el.Name} ({live.GetType().Name}) from {c.mod.Name} → {PatchBuilder.PatchDir}.");
            RefreshPatchColumn();
            if (_index >= 0 && _index < _items.Count)
                _items[_index].resolved = true;
        }

        // Rebuild only the Patch column in place so the imported element shows up as an editable panel
        // without nuking the (still-relevant) source columns and resetting their tab state.
        void RefreshPatchColumn()
        {
            var item = _items[_index];
            InvalidatePatchRepo(item.name);
            for (int i = _cols.Count - 1; i >= 0; i--)
            {
                if (_cols[i].mod == null) _cols.RemoveAt(i);
            }
            AppendPatchColumn(item);
            item.inPatch = true;
            RecomputeDisplayDiffs(item);
        }

        void OnDisable()
        {
            DisposeSession();
        }

        void DisposeSession()
        {
            FlushDirtyPatchAssets();
            _cols.Clear();
            _columnsByItem.Clear();

            foreach (var e in _sourceRepo.Values)
            {
                if (e?.Editor != null) DestroyImmediate(e.Editor);
                if (e == null) continue;
                PatchBuilder.UnpinStage(e.StagePath);
                PatchBuilder.CleanupStage(e.StagePath);
            }
            _sourceRepo.Clear();

            foreach (var e in _patchRepo.Values)
            {
                if (e?.Editor != null) DestroyImmediate(e.Editor);
            }
            _patchRepo.Clear();
            _displayDiffs = null;
        }

        void FlushDirtyPatchAssets()
        {
            bool any = false;
            foreach (var e in _patchRepo.Values)
            {
                if (e?.Obj == null) continue;
                if (!EditorUtility.IsDirty(e.Obj)) continue;
                any = true;
            }
            // Also check currently visible patch panels (same objects, but be safe).
            foreach (var c in _cols)
            {
                if (c.mod != null) continue;
                foreach (var p in c.panels)
                {
                    if (p.obj != null && EditorUtility.IsDirty(p.obj)) any = true;
                }
            }
            if (any) AssetDatabase.SaveAssets();
        }

        class TypeDropdown : AdvancedDropdown
        {
            readonly List<string> _types;
            readonly Action<string> _onPick;
            public TypeDropdown(AdvancedDropdownState state, List<string> types, Action<string> onPick) : base(state)
            {
                _types = types ?? new List<string>();
                _onPick = onPick;
                int rows = Math.Min(_types.Count + 1, 22);
                float h = 44f + rows * 18f;
                minimumSize = new Vector2(220, Mathf.Clamp(h, 100f, AdvancedDropdownHeight.DefaultMaxHeight));
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
}
