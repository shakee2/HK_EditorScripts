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
    /// stacks the element then its matching UIMapper/DescriptorMapper(s). Source columns use staged text
    /// imports for folder/zip/unitypackage, or the mounted live object for assetbundle sources (no scratch
    /// file). The Patch column is the real asset and editable, saved on close. Scratch staging is deleted
    /// when switching elements and on close.
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
        }

        class Panel
        {
            public string label;
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
        readonly List<Column> _cols = new List<Column>();
        float _listWidth = 320f;
        bool _draggingSplit;
        GUIStyle _headerStyle;

        const float LIST_ROW_H = 18f;
        const float TYPE_COL_W = 110f;
        const string PrefTypeFilter = "CompatCompare.TypeFilter";
        const string PrefGroupByType = "CompatCompare.GroupByType";
        static readonly Color LIST_SEL = new Color(0.3f, 0.5f, 0.9f, 0.28f);
        static readonly Color LIST_ALT = new Color(1f, 1f, 1f, 0.03f);
        static readonly Color HEADER_BG = new Color(0f, 0f, 0f, 0.18f);
        static readonly Color ROW_LINE = new Color(0f, 0f, 0f, 0.12f);

        public static void Show(List<CompareItem> items, int index)
        {
            var w = GetWindow<CompatCompareWindow>(typeof(CompatPatcherWindow));
            w.titleContent = new GUIContent("Compare elements");
            w._items = items ?? new List<CompareItem>();
            w._typeFilter = EditorPrefs.GetString(PrefTypeFilter, "");
            w._groupByType = EditorPrefs.GetBool(PrefGroupByType, false);
            w._viewDirty = true;
            w.RebuildView();
            w.SelectItem(Mathf.Clamp(index, 0, w._items.Count - 1));
            w.Show();
        }

        void SelectItem(int i)
        {
            ClearColumns();
            _index = i;
            if (i < 0 || i >= _items.Count) return;
            var item = _items[i];

            foreach (var (mod, modObj, el) in item.versions)
            {
                var col = new Column { header = mod, mod = modObj, el = el };
                AddStaged(col, el.TypeHint, modObj, el);
                foreach (var m in MatchingMappers(modObj, el)) AddStaged(col, m.TypeHint, modObj, m);
                if (col.panels.Count > 0) _cols.Add(col);
            }
            var patchCol = new Column { header = "Patch (editable)" };
            foreach (var (path, obj) in FindPatchObjects(item.name))
                patchCol.panels.Add(new Panel { label = Path.GetFileNameWithoutExtension(path), obj = obj, editor = Editor.CreateEditor(obj), stagePath = null, editable = true });
            if (patchCol.panels.Count > 0) _cols.Add(patchCol);
            RecomputeDisplayDiffs(item);
            Repaint();
        }

        static IEnumerable<HkElement> MatchingMappers(HkMod mod, HkElement el) =>
            mod.Elements.Values.Where(e => e.Name == el.Name && e.Type != el.Type && !e.IsRoot);

        void AddStaged(Column col, string label, HkMod mod, HkElement el)
        {
            var (obj, _, stage) = PatchBuilder.StageElement(mod, el);
            if (obj == null) { PatchBuilder.CleanupStage(stage); return; }
            col.panels.Add(new Panel { label = label, obj = obj, editor = Editor.CreateEditor(obj), stagePath = stage, editable = false });
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
            if (GUILayout.Button(_typeFilter.Length == 0 ? "(any)" : _typeFilter, EditorStyles.popup))
            {
                var rect = GUILayoutUtility.GetLastRect();
                new TypeDropdown(_typeDdState, _types, picked =>
                {
                    _typeFilter = picked;
                    EditorPrefs.SetString(PrefTypeFilter, _typeFilter);
                    _viewDirty = true;
                    Repaint();
                }).Show(rect);
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
                string rowLabel = (_items[i].inPatch ? "● " : "  ") + _items[i].name;
                if (_items[i].inPatch) tip = "In patch\n" + tip;
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
            EditorGUILayout.LabelField($"{item.name}   ·   {ItemType(item)}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Source columns are read-only (staged); the Patch column is editable and saved on close. Element then its UIMapper stacked.", EditorStyles.miniLabel);

            DrawDiffSection(item);

            if (_cols.Count == 0) { EditorGUILayout.HelpBox("Nothing to compare (staging failed).", MessageType.Warning); EditorGUILayout.EndVertical(); return; }

            float w = (position.width - _listWidth - 24) / _cols.Count;
            float prevLabel = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Clamp(w * 0.38f, 110f, 200f);
            EditorGUILayout.BeginHorizontal();
            foreach (var c in _cols)
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(w));
                EditorGUILayout.LabelField(c.header, EditorStyles.boldLabel);
                if (c.mod != null && c.el != null)
                {
                    using (new EditorGUI.DisabledScope(item.inPatch))
                    {
                        if (GUILayout.Button("Import this version into Patch/", EditorStyles.miniButton, GUILayout.Width(w - 28)))
                            ImportThisVersion(c);
                    }
                }
                c.scroll = EditorGUILayout.BeginScrollView(c.scroll);
                foreach (var p in c.panels)
                {
                    EditorGUILayout.LabelField(p.label + (p.editable ? "" : "  (read-only)"), EditorStyles.miniBoldLabel);
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
                    EditorGUILayout.Space(4);
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUIUtility.labelWidth = prevLabel;
            EditorGUILayout.EndVertical();
        }

        void DrawDiffSection(CompareItem item)
        {
            EditorGUILayout.Space(2);
            float h = Mathf.Clamp(position.height * 0.28f, 70f, 260f);
            _diffScroll = EditorGUILayout.BeginScrollView(_diffScroll, GUILayout.Height(h));
            DiffGui.DrawTable(_displayDiffs, _displayDiffWinner, _displayDiffOdin);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space(2);
        }

        static List<HkElement> FindPatchHkElements(string elementName)
        {
            var result = new List<HkElement>();
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
                _displayDiffs = item.diffs ?? new List<Diff>();
                _displayDiffWinner = item.winner;
                _displayDiffOdin = item.odin;
                return;
            }

            var winner = patchEls.FirstOrDefault(e => e.TypeHint == item.typeHint) ?? patchEls[0];
            var losers = item.versions.ToDictionary(v => v.mod, v => v.el);
            _displayDiffs = ConflictAnalyzer.ComputeDiffs(winner, losers);
            _displayDiffWinner = "Patch";
            _displayDiffOdin = winner.Odin;
        }

        static IEnumerable<HkElement> MappersFor(HkMod mod, HkElement el) =>
            mod.Elements.Values.Where(e => e.Name == el.Name && e.Type != el.Type && !e.IsRoot);

        // Mirrors CompatPatcherWindow.ImportChosen but for the compare window's per-mod columns:
        // imports this column's primary element plus its UIMapper/DescriptorMapper(s) into the patch,
        // then refreshes the Patch column on the spot so the user can see the new editable copy.
        void ImportThisVersion(Column c)
        {
            var set = new Dictionary<string, (HkMod mod, HkElement el)>();
            set[c.el.Key] = (c.mod, c.el);
            foreach (var m in MappersFor(c.mod, c.el))
                if (!set.ContainsKey(m.Key)) set[m.Key] = (c.mod, m);
            int n = PatchBuilder.ImportElements(set.Values);
            Debug.Log($"[CompatPatcher] Compare import: {c.el.Name} from {c.mod.Name} (+{n - 1} mapper(s)) → {PatchBuilder.PatchDir}.");
            RefreshPatchColumn();
        }

        // Rebuild only the Patch column in place so the imported element shows up as an editable panel
        // without nuking the (still-relevant) staged source columns and resetting their tab state.
        void RefreshPatchColumn()
        {
            for (int i = _cols.Count - 1; i >= 0; i--)
            {
                var c = _cols[i];
                if (c.mod != null) continue;
                foreach (var p in c.panels)
                {
                    if (p.editor != null) DestroyImmediate(p.editor);
                }
                c.panels.Clear();
                _cols.RemoveAt(i);
            }
            var item = _items[_index];
            var patchCol = new Column { header = "Patch (editable)" };
            foreach (var (path, obj) in FindPatchObjects(item.name))
                patchCol.panels.Add(new Panel { label = Path.GetFileNameWithoutExtension(path), obj = obj, editor = Editor.CreateEditor(obj), stagePath = null, editable = true });
            if (patchCol.panels.Count > 0) _cols.Add(patchCol);
            item.inPatch = true;
        }

        void OnDisable() => ClearColumns();

        void ClearColumns()
        {
            bool hadEditable = false;
            foreach (var c in _cols)
                foreach (var p in c.panels)
                {
                    if (p.editable && p.obj != null) { EditorUtility.SetDirty(p.obj); hadEditable = true; }
                    if (p.editor != null) DestroyImmediate(p.editor);
                    PatchBuilder.CleanupStage(p.stagePath);
                }
            if (hadEditable) AssetDatabase.SaveAssets();
            _cols.Clear();
        }

        class TypeDropdown : AdvancedDropdown
        {
            readonly List<string> _types;
            readonly Action<string> _onPick;
            public TypeDropdown(AdvancedDropdownState state, List<string> types, Action<string> onPick) : base(state)
            {
                _types = types; _onPick = onPick;
                minimumSize = new Vector2(260, 340);
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
