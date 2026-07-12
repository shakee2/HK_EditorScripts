using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Side-by-side inspector of one element across the mods that define it, plus (if present) the
    /// version already in Assets/Databases/Patch/. A searchable list on the left switches which element
    /// is compared. Each column stacks the element then its matching UIMapper/DescriptorMapper(s).
    /// Source columns are staged (read-only); the Patch column is the real asset and editable, saved on
    /// close. Scratch staging is deleted when switching elements and on close.
    /// </summary>
    public class CompatCompareWindow : EditorWindow
    {
        public class CompareItem
        {
            public string name;
            public string typeHint;
            public List<(string mod, HkMod modObj, HkElement el)> versions;
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
        }

        List<CompareItem> _items = new List<CompareItem>();
        readonly List<int> _filtered = new List<int>();   // indices into _items matching _search (materialized for virtualization)
        string _filterCache;                              // _search value _filtered was built for (null → rebuild)
        int _index = -1;
        string _search = "";
        Vector2 _listScroll;
        readonly List<Column> _cols = new List<Column>();
        float _listWidth = 300f;
        bool _draggingSplit;

        const float LIST_ROW_H = 18f;
        static readonly Color LIST_SEL = new Color(0.3f, 0.5f, 0.9f, 0.28f);
        static readonly Color LIST_ALT = new Color(1f, 1f, 1f, 0.03f);

        public static void Show(List<CompareItem> items, int index)
        {
            var w = GetWindow<CompatCompareWindow>(false, "Compare elements", true);
            w._items = items ?? new List<CompareItem>();
            w._filterCache = null;   // force filter rebuild against the new item set
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
                var col = new Column { header = mod };
                AddStaged(col, el.TypeHint, modObj, el);
                foreach (var m in MatchingMappers(modObj, el)) AddStaged(col, m.TypeHint, modObj, m);
                if (col.panels.Count > 0) _cols.Add(col);
            }
            var patchCol = new Column { header = "Patch (editable)" };
            foreach (var (path, obj) in FindPatchObjects(item.name))
                patchCol.panels.Add(new Panel { label = Path.GetFileNameWithoutExtension(path), obj = obj, editor = Editor.CreateEditor(obj), stagePath = null, editable = true });
            if (patchCol.panels.Count > 0) _cols.Add(patchCol);
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
                if (e.type == EventType.MouseDrag) { _listWidth = Mathf.Clamp(_listWidth + e.delta.x, 150f, position.width - 200f); Repaint(); }
                if (e.type == EventType.MouseUp) _draggingSplit = false;
            }
        }

        // Recompute the filtered index list; only when the search text actually changed.
        void RebuildFilter()
        {
            _filtered.Clear();
            string q = _search.Trim().ToLowerInvariant();
            for (int i = 0; i < _items.Count; i++)
                if (q.Length == 0 || (_items[i].name ?? "").ToLowerInvariant().Contains(q))
                    _filtered.Add(i);
            _filterCache = _search;
        }

        void DrawList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(_listWidth));
            EditorGUI.BeginChangeCheck();
            _search = EditorGUILayout.TextField(_search);
            if (EditorGUI.EndChangeCheck() || _filterCache == null) RebuildFilter();

            // Virtualized list: draw only the rows intersecting the viewport (like DescriptorPropertyIndex),
            // so cost is bounded by what's visible, not by the whole handed-over view.
            Rect area = GUILayoutUtility.GetRect(10, _listWidth, 10, 100000,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            Rect content = new Rect(0, 0, area.width - 16, _filtered.Count * LIST_ROW_H);
            _listScroll = GUI.BeginScrollView(area, _listScroll, content);

            int first = Mathf.Max(0, Mathf.FloorToInt(_listScroll.y / LIST_ROW_H));
            int last = Mathf.Min(_filtered.Count, Mathf.CeilToInt((_listScroll.y + area.height) / LIST_ROW_H) + 1);
            Vector2 mouse = Event.current.mousePosition;
            for (int vi = first; vi < last; vi++)
            {
                int i = _filtered[vi];
                Rect rr = new Rect(0, vi * LIST_ROW_H, content.width, LIST_ROW_H);
                if (i == _index) EditorGUI.DrawRect(rr, LIST_SEL);
                else if ((vi & 1) == 1) EditorGUI.DrawRect(rr, LIST_ALT);
                if (Event.current.type == EventType.MouseDown && rr.Contains(mouse)) { SelectItem(i); Event.current.Use(); }
                Rect label = new Rect(rr.x + 4, rr.y, rr.width - 4, rr.height);
                GUI.Label(label, new GUIContent(_items[i].name, _items[i].name),
                    i == _index ? EditorStyles.boldLabel : EditorStyles.label);
            }
            GUI.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        void DrawColumns()
        {
            EditorGUILayout.BeginVertical();
            if (_index < 0 || _index >= _items.Count) { EditorGUILayout.HelpBox("Select an element from the list.", MessageType.None); EditorGUILayout.EndVertical(); return; }
            var item = _items[_index];
            EditorGUILayout.LabelField($"{item.name}   ·   {item.typeHint}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Source columns are read-only (staged); the Patch column is editable and saved on close. Element then its UIMapper stacked.", EditorStyles.miniLabel);

            if (_cols.Count == 0) { EditorGUILayout.HelpBox("Nothing to compare (staging failed).", MessageType.Warning); EditorGUILayout.EndVertical(); return; }

            float w = (position.width - _listWidth - 24) / _cols.Count;
            float prevLabel = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Clamp(w * 0.38f, 110f, 200f);
            EditorGUILayout.BeginHorizontal();
            foreach (var c in _cols)
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(w));
                EditorGUILayout.LabelField(c.header, EditorStyles.boldLabel);
                c.scroll = EditorGUILayout.BeginScrollView(c.scroll);
                foreach (var p in c.panels)
                {
                    EditorGUILayout.LabelField(p.label + (p.editable ? "" : "  (read-only)"), EditorStyles.miniBoldLabel);
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    using (new EditorGUI.DisabledScope(!p.editable))
                    {
                        EditorGUILayout.BeginVertical(GUILayout.Width(w - 28));
                        try { if (p.editor != null) p.editor.OnInspectorGUI(); }
                        catch (Exception ex) { EditorGUILayout.HelpBox("Embedded inspector failed: " + ex.Message, MessageType.Warning); }
                        EditorGUILayout.EndVertical();
                    }
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
    }
}
