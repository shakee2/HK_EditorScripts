using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

/// <summary>
/// Tech-tree viewer/editor. Edit mode adds position drag (snap to grid) and prereq
/// removal, staged in an in-memory overlay and committed on Save. Save materializes
/// each change: edit-in-place when the asset is under Databases/, copy-on-write into
/// New Additions when it's reference-only (TechTreeData.EnsureWritable). The +prereq
/// picker (step 4) and new-tech creation (step 5) are the remaining pieces.
/// </summary>
public class TechTreeWindow : EditorWindow
{
    // ── Pending overlay: staged, not-yet-saved edits per tech name ────────────
    class Pending
    {
        public int? X, Y;                // staged position (null = unchanged)
        public List<string> Prereqs;     // staged prereq list (null = unchanged)
        public string TitleText, DescriptionText;  // staged localization text (null = unchanged)
        public bool IsEmpty => X == null && Y == null && Prereqs == null && TitleText == null && DescriptionText == null;
    }
    readonly Dictionary<string, Pending> _pending = new();

    // Cache of "is this loc key imported into a project override row?" per key. The
    // underlying ArchiveTranslations.HasOverride walks every LocalizedStringTranslationCollection
    // in the project on every call, so calling it per repaint (twice per selected node) was
    // what made the window slow after the editable-loc feature landed. The override state
    // only changes on import / save / revert, so a memo keyed by loc key is enough; invalidate
    // it (InvalidateImportedCache) on those three events.
    readonly Dictionary<string, bool> _importedCache = new();
    void InvalidateImportedCache() => _importedCache.Clear();
    bool IsImported(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (!_importedCache.TryGetValue(key, out var v))
            _importedCache[key] = v = ArchiveTranslations.HasOverride(key);
        return v;
    }

    List<TechTreeData.Node> _nodes;
    Dictionary<string, TechTreeData.Node> _byName;
    TechTreeData.Node _selected;

    // mod path (editable, persisted)
    string _modPath = TechTreeData.DefaultModPath;
    string ModPathKey => "TechTree.ModPath";

    bool _editMode;

    // position-drag state (edit mode)
    TechTreeData.Node _dragNode;
    Vector2 _dragStartMouse;
    int _dragStartX, _dragStartY;

    // view transform
    Vector2 _pan;
    float   _zoom = 1f;
    bool    _framed, _panning;

    const float CELL = 26f;
    const float NODE_W = 150f, NODE_H = 34f;
    const float LABEL_MIN_ZOOM = 0.45f;

    // sidebar width — draggable via the splitter handle, persisted across sessions
    const float SIDE_W_MIN = 220f, SIDE_W_MAX = 600f;
    float _sideW = 300f;
    bool _resizingSide;
    string SideWKey => "TechTree.SideW";

    static readonly Color EDIT_DOT = new Color(1f, 0.65f, 0.1f);     // amber: unsaved
    static readonly Color MOD_BADGE = new Color(0.4f, 0.7f, 1f);     // blue: modded on disk

    [MenuItem("Tools/Tech Tree Viewer", false, 4)]
    static void Open()
    {
        var w = GetWindow<TechTreeWindow>("Tech Tree");
        w.minSize = new Vector2(500, 300);
        w.maxSize = new Vector2(4000, 4000);   // large max so it stays freely resizable
        w.Show();
        w.Focus();
    }

    void OnEnable()
    {
        _modPath = EditorPrefs.GetString(ModPathKey, TechTreeData.DefaultModPath);
        _sideW = Mathf.Clamp(EditorPrefs.GetFloat(SideWKey, _sideW), SIDE_W_MIN, SIDE_W_MAX);
        Undo.undoRedoPerformed += OnUndoRedo;
        try { Reload(); }
        catch (Exception e) { Debug.LogError($"[TechTree] Build failed on open: {e}"); _nodes = new(); _byName = new(); }

        // After a script recompile, the Mod Editor's translations bundle may still be
        // loaded at the native level while Amplitude's provider registry hasn't
        // re-registered it yet — so the first Reload() above may have produced an empty
        // translation dict (labels fallen back to keys). Give ModTools a frame to finish
        // repopulating, then retry once. Subsequent calls won't re-trigger this because
        // BuildVanillaCache only retries when its cache is empty.
        if (!ArchiveTranslations.IsMounted)
            EditorApplication.update += DelayedRetryMount;
    }

    static void DelayedRetryMount()
    {
        // One-shot: unsubscribe first so this only runs once per subscription.
        EditorApplication.update -= DelayedRetryMount;
        // Re-mount attempt + reload every open Tech Tree window so labels recover without
        // a manual Reload click after a recompile.
        ArchiveTranslations.TryMount(out _);
        foreach (var w in Resources.FindObjectsOfTypeAll<TechTreeWindow>())
            w.Reload(keepView: true);
    }

    void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedo;
    }

    void OnUndoRedo()
    {
        // An undo/redo changed an asset on disk; rebuild from disk but keep the view.
        Reload(keepView: true);
        Repaint();
    }

    void Reload(bool keepView = false)
    {
        _nodes = TechTreeData.Build(_modPath);
        _byName = _nodes.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.First());
        // keep pending across reload only for names that still exist
        foreach (var k in _pending.Keys.Where(k => !_byName.ContainsKey(k)).ToList()) _pending.Remove(k);
        // Re-check imported state: override rows may have been added or removed externally
        // (e.g. via the Mod Editor's Localization Window) since the last reload, so the
        // _importedCache memo is stale. Drop it so IsImported re-queries the collection.
        InvalidateImportedCache();
        if (!keepView) _framed = false;   // re-frame only on first load / explicit Frame All
        Repaint();
    }

    // ── Effective (pending-over-base) accessors ───────────────────────────────
    int EffX(TechTreeData.Node n) => _pending.TryGetValue(n.Name, out var p) && p.X.HasValue ? p.X.Value : n.BaseX;
    int EffY(TechTreeData.Node n) => _pending.TryGetValue(n.Name, out var p) && p.Y.HasValue ? p.Y.Value : n.BaseY;
    IReadOnlyList<string> EffPrereqs(TechTreeData.Node n) =>
        _pending.TryGetValue(n.Name, out var p) && p.Prereqs != null ? p.Prereqs : (IReadOnlyList<string>)n.BasePrereqs;
    string EffTitleText(TechTreeData.Node n) =>
        _pending.TryGetValue(n.Name, out var p) && p.TitleText != null ? p.TitleText : n.TitleText;
    string EffDescriptionText(TechTreeData.Node n) =>
        _pending.TryGetValue(n.Name, out var p) && p.DescriptionText != null ? p.DescriptionText : n.DescriptionText;

    bool IsEdited(TechTreeData.Node n) => _pending.ContainsKey(n.Name);
    bool Dirty => _pending.Count > 0;

    Pending Stage(TechTreeData.Node n)
    {
        if (!_pending.TryGetValue(n.Name, out var p)) _pending[n.Name] = p = new Pending();
        return p;
    }
    void DropIfClean(TechTreeData.Node n)
    {
        if (_pending.TryGetValue(n.Name, out var p) && p.IsEmpty) _pending.Remove(n.Name);
    }

    Vector2 W2S(float cx, float cy) => new Vector2(cx * CELL * _zoom + _pan.x, cy * CELL * _zoom + _pan.y);

    void FrameAll(Rect canvas)
    {
        if (_nodes == null || _nodes.Count == 0) return;
        float maxX = _nodes.Max(EffX) + 2;
        float maxY = _nodes.Max(EffY) + 2;
        float zx = canvas.width / (maxX * CELL), zy = canvas.height / (maxY * CELL);
        _zoom = Mathf.Clamp(Mathf.Min(zx, zy), 0.05f, 2f);
        _pan = new Vector2(canvas.x + (canvas.width - maxX * CELL * _zoom) * 0.5f,
                           canvas.y + (canvas.height - maxY * CELL * _zoom) * 0.5f);
        _framed = true;
    }

    void OnGUI()
    {
        if (Event.current.type == EventType.MouseMove) Repaint();

        // ── Toolbar ──
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        _editMode = GUILayout.Toggle(_editMode, _editMode ? "● Edit" : "View", EditorStyles.toolbarButton, GUILayout.Width(70));
        if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(60))) Reload(keepView: true);
        if (GUILayout.Button("Frame All", EditorStyles.toolbarButton, GUILayout.Width(70))) _framed = false;

        using (new EditorGUI.DisabledScope(!Dirty))
        {
            GUI.backgroundColor = Dirty ? EDIT_DOT : Color.white;
            if (GUILayout.Button($"Save{(Dirty ? $" ({_pending.Count})" : "")}", EditorStyles.toolbarButton, GUILayout.Width(80)))
                CommitChanges();
            GUI.backgroundColor = Color.white;
            if (GUILayout.Button("Revert", EditorStyles.toolbarButton, GUILayout.Width(60))) RevertChanges();
        }

        GUILayout.FlexibleSpace();

        // vanilla bundle mount status (mounted automatically from the Mod Editor's Humankind folder)
        bool vanillaMounted = VanillaDatabaseMount.IsMounted || VanillaDatabaseMount.TryMount(out _);
        EditorGUILayout.LabelField("Vanilla", GUILayout.Width(46));
        GUI.color = vanillaMounted ? Color.white : new Color(1f, 0.6f, 0.6f);
        GUILayout.Label(vanillaMounted ? "mounted" : "not mounted", EditorStyles.toolbarButton, GUILayout.Width(80));
        GUI.color = Color.white;
        if (GUILayout.Button("Remount", EditorStyles.toolbarButton, GUILayout.Width(64)))
        {
            VanillaDatabaseMount.Invalidate();
            if (!VanillaDatabaseMount.TryMount(out var error)) Debug.LogWarning($"[TechTree] {error}");
            Reload(keepView: true);
        }

        EditorGUILayout.LabelField("Mod path", GUILayout.Width(55));
        string newPath = EditorGUILayout.TextField(_modPath, GUILayout.Width(200));
        if (newPath != _modPath) { _modPath = newPath; EditorPrefs.SetString(ModPathKey, _modPath); }
        if (GUILayout.Button("Apply", EditorStyles.toolbarButton, GUILayout.Width(50))) Reload(keepView: true);
        EditorGUILayout.EndHorizontal();

        if (!vanillaMounted)
            EditorGUILayout.HelpBox($"Vanilla databases bundle not mounted ({VanillaDatabaseMount.LastError}) — showing Databases content only (no vanilla fallback).", MessageType.Info);

        float top = EditorStyles.toolbar.fixedHeight > 0 ? EditorStyles.toolbar.fixedHeight : 21f;
        _sideW = Mathf.Clamp(_sideW, SIDE_W_MIN, Mathf.Max(SIDE_W_MIN, position.width - 200f));
        Rect canvas   = new Rect(0, top, position.width - _sideW, position.height - top);
        Rect side     = new Rect(canvas.xMax, top, _sideW, position.height - top);
        Rect splitter = new Rect(canvas.xMax - 2f, top, 4f, position.height - top);

        HandleSplitter(splitter);
        if (!_framed) FrameAll(canvas);
        HandleInput(canvas);
        DrawCanvas(canvas);
        DrawSide(side);
    }

    void HandleSplitter(Rect splitter)
    {
        EditorGUIUtility.AddCursorRect(splitter, MouseCursor.ResizeHorizontal);
        var e = Event.current;
        if (e.type == EventType.MouseDown && splitter.Contains(e.mousePosition))
        { _resizingSide = true; e.Use(); }
        else if (e.type == EventType.MouseDrag && _resizingSide)
        {
            _sideW = Mathf.Clamp(_sideW - e.delta.x, SIDE_W_MIN, Mathf.Max(SIDE_W_MIN, position.width - 200f));
            EditorPrefs.SetFloat(SideWKey, _sideW);
            e.Use(); Repaint();
        }
        else if (e.type == EventType.MouseUp && _resizingSide)
        { _resizingSide = false; e.Use(); }
    }

    // ── Input ─────────────────────────────────────────────────────────────────
    void HandleInput(Rect canvas)
    {
        var e = Event.current;
        if (!canvas.Contains(e.mousePosition) && !_panning) return;

        if (e.type == EventType.ScrollWheel)
        {
            float old = _zoom;
            _zoom = Mathf.Clamp(_zoom * (1f - e.delta.y * 0.05f), 0.05f, 3f);
            Vector2 m = e.mousePosition;
            _pan = m - (m - _pan) * (_zoom / old);
            e.Use(); Repaint();
        }
        else if (e.type == EventType.MouseDown && (e.button == 2 || (e.button == 0 && e.alt)))
        { _panning = true; e.Use(); }
        else if (e.type == EventType.MouseDrag && _panning)
        { _pan += e.delta; e.Use(); Repaint(); }
        else if (e.type == EventType.MouseUp && _panning)
        { _panning = false; e.Use(); }
        else if (e.type == EventType.MouseDown && e.button == 0)
        {
            var hit = NodeAt(e.mousePosition);
            if (hit != null)
            {
                _selected = hit;
                EditorGUIUtility.PingObject(hit.Asset);
                Selection.activeObject = hit.Asset;
                if (_editMode)   // begin a potential position drag
                {
                    _dragNode = hit;
                    _dragStartMouse = e.mousePosition;
                    _dragStartX = EffX(hit);
                    _dragStartY = EffY(hit);
                }
                e.Use(); Repaint();
            }
        }
        else if (e.type == EventType.MouseDrag && _dragNode != null)
        {
            float inv = 1f / (CELL * _zoom);
            int dx = Mathf.RoundToInt((e.mousePosition.x - _dragStartMouse.x) * inv);
            int dy = Mathf.RoundToInt((e.mousePosition.y - _dragStartMouse.y) * inv);
            int nx = Mathf.Max(0, _dragStartX + dx);
            int ny = Mathf.Max(0, _dragStartY + dy);
            var p = Stage(_dragNode);
            p.X = nx != _dragNode.BaseX ? (int?)nx : null;   // snap; equal to base = no change
            p.Y = ny != _dragNode.BaseY ? (int?)ny : null;
            DropIfClean(_dragNode);
            e.Use(); Repaint();
        }
        else if (e.type == EventType.MouseUp && _dragNode != null)
        {
            _dragNode = null;
            e.Use(); Repaint();
        }
    }

    TechTreeData.Node NodeAt(Vector2 screen)
    {
        foreach (var n in _nodes)
        {
            Vector2 p = W2S(EffX(n), EffY(n));
            if (new Rect(p.x, p.y, NODE_W * _zoom, NODE_H * _zoom).Contains(screen)) return n;
        }
        return null;
    }

    // ── Canvas ────────────────────────────────────────────────────────────────
    void DrawCanvas(Rect canvas)
    {
        EditorGUI.DrawRect(canvas, new Color(0.16f, 0.16f, 0.17f));
        GUI.BeginClip(canvas);
        Rect local = new Rect(0, 0, canvas.width, canvas.height);
        Vector2 panSave = _pan; _pan -= new Vector2(canvas.x, canvas.y);

        bool showLabels = _zoom >= LABEL_MIN_ZOOM;

        // edges (from effective prereqs)
        foreach (var n in _nodes)
        {
            Vector2 to = W2S(EffX(n), EffY(n)) + new Vector2(0, NODE_H * _zoom * 0.5f);
            foreach (var pr in EffPrereqs(n))
            {
                if (!_byName.TryGetValue(pr, out var src)) continue;
                Vector2 from = W2S(EffX(src), EffY(src)) + new Vector2(NODE_W * _zoom, NODE_H * _zoom * 0.5f);
                if (!local.Contains(from) && !local.Contains(to) && !LineCrossesRect(from, to, local)) continue;
                Handles.DrawBezier(from, to, from + Vector2.right * 40f * _zoom, to - Vector2.right * 40f * _zoom,
                    new Color(0.5f, 0.6f, 0.7f, 0.7f), null, 2f);
            }
        }

        // nodes
        foreach (var n in _nodes)
        {
            Vector2 p = W2S(EffX(n), EffY(n));
            var r = new Rect(p.x, p.y, NODE_W * _zoom, NODE_H * _zoom);
            if (!r.Overlaps(local)) continue;

            Color tint = EraColor(n.Era);
            if (n == _selected) tint = Color.Lerp(tint, Color.white, 0.4f);
            EditorGUI.DrawRect(r, tint);
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), new Color(0, 0, 0, 0.4f));

            // markers: modded badge (left stripe, scales with zoom) + edited dot (top-right)
            if (n.AnyModded)
            {
                float bw = Mathf.Max(3f, 4f * _zoom);
                EditorGUI.DrawRect(new Rect(r.x, r.y, bw, r.height), MOD_BADGE);
            }
            if (IsEdited(n))
            {
                float d = Mathf.Max(5f, 7f * _zoom);
                EditorGUI.DrawRect(new Rect(r.xMax - d - 2, r.y + 2, d, d), EDIT_DOT);
            }

            if (showLabels)
            {
                int fs = Mathf.Clamp(Mathf.RoundToInt(11f * Mathf.Min(_zoom, 1.3f)), 7, 14);
                var style = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = fs,
                    fontStyle = FontStyle.Bold,
                    wordWrap = true,
                    normal = { textColor = Color.white }
                };
                GUI.Label(r, n.Label, style);

                if (_zoom >= LABEL_MIN_ZOOM)
                {
                    int cfs = Mathf.Clamp(Mathf.RoundToInt(9f * Mathf.Min(_zoom, 1.3f)), 7, 12);
                    var corner = new GUIStyle(EditorStyles.miniLabel)
                    { fontSize = cfs, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1, 1, 1, 0.75f) } };
                    float ch = cfs + 4f;
                    corner.alignment = TextAnchor.UpperLeft;
                    GUI.Label(new Rect(r.x + 5, r.y + 1, r.width - 8, ch), ShortEra(n.Era), corner);
                    corner.alignment = TextAnchor.UpperRight;
                    GUI.Label(new Rect(r.x + 2, r.y + 1, r.width - 12, ch), ShortTier(n.Tier), corner);
                }
            }
        }

        _pan = panSave;
        GUI.EndClip();
    }

    // ── Side strip ────────────────────────────────────────────────────────────
    Vector2 _sideScroll;
    float _sideContentW;   // width of the pinned vertical group inside the side scroll view
    void DrawSide(Rect side)
    {
        GUILayout.BeginArea(side, EditorStyles.helpBox);
        // Pin the scroll view's vertical content to the visible width so multi-line
        // controls (notably the loc TextArea in DrawLocField) can't grow the content
        // wider than the sidebar to fit their text on one line — which was preventing
        // word-wrap and pushing a horizontal scrollbar in. We subtract a small margin
        // for the scrollbar + box padding.
        const float SIDE_PADDING = 18f;
        float contentW = Mathf.Max(40f, side.width - SIDE_PADDING);
        _sideScroll = EditorGUILayout.BeginScrollView(_sideScroll, GUILayout.Width(side.width), GUILayout.ExpandHeight(true));
        EditorGUILayout.BeginVertical(GUILayout.Width(contentW));
        _sideContentW = contentW;

        if (_selected == null)
            EditorGUILayout.LabelField("Click a node to inspect.", EditorStyles.wordWrappedMiniLabel);
        else
        {
            var n = _selected;
            EditorGUILayout.LabelField(n.Label, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(n.Name, EditorStyles.miniLabel);

            // state line
            string state = n.AnyModded ? "Modded" : "Vanilla";
            if (IsEdited(n)) state += " · edited *";
            string detail = $"def:{(n.DefModded ? "mod" : "vanilla")}  map:{(n.MapperModded ? "mod" : "vanilla")}";
            EditorGUILayout.LabelField($"{state}   ({detail})", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"{n.Era} · {n.Tier} · ({EffX(n)},{EffY(n)})", EditorStyles.miniLabel);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Localization", EditorStyles.boldLabel);
            DrawLocField(n, "Title", n.TitleKey, EffTitleText(n), isDescription: false);
            DrawLocField(n, "Description", n.DescriptionKey, EffDescriptionText(n), isDescription: true);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Prerequisites (OR):", EditorStyles.boldLabel);
            var prereqs = EffPrereqs(n);
            if (prereqs.Count == 0)
                EditorGUILayout.LabelField("  (none — root)", EditorStyles.miniLabel);
            else
                foreach (var pr in prereqs.ToList())
                {
                    EditorGUILayout.BeginHorizontal();
                    string label = _byName.TryGetValue(pr, out var s) ? $"{s.Label}" : pr;
                    if (GUILayout.Button(label, EditorStyles.miniButton) && _byName.TryGetValue(pr, out var sn))
                    { _selected = sn; EditorGUIUtility.PingObject(sn.Asset); Selection.activeObject = sn.Asset; }
                    using (new EditorGUI.DisabledScope(!_editMode))
                        if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(22)))
                            RemovePrereq(n, pr);   // step 3 fills the body; staging works now
                    EditorGUILayout.EndHorizontal();
                }

            using (new EditorGUI.DisabledScope(!_editMode))
                if (GUILayout.Button("+ add prerequisite"))
                    AddPrereqPrompt(n, GUILayoutUtility.GetLastRect());

            EditorGUILayout.Space(8);
            if (GUILayout.Button("Select asset"))
            { Selection.activeObject = n.Asset; EditorGUIUtility.PingObject(n.Asset); }
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    // %key + copy button, plus the resolved text — editable once imported into a project
    // override row (ArchiveTranslations.EnsureOverride); locked (with an Import button)
    // until then, since editing text that's still only in the vanilla archive would have
    // nowhere to be saved.
    void DrawLocField(TechTreeData.Node n, string label, string key, string currentText, bool isDescription)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(70));
        EditorGUILayout.SelectableLabel(string.IsNullOrEmpty(key) ? "(none)" : key,
            EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(key)))
            if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(44)))
                EditorGUIUtility.systemCopyBuffer = key;
        EditorGUILayout.EndHorizontal();

        bool imported = IsImported(key);
        using (new EditorGUI.DisabledScope(!imported))
        {
            // Word-wrap fix: the textarea is inside a vertical group whose width is pinned
            // to the visible sidebar width (see DrawSide), so the style's wordWrap actually
            // engages now. Compute the wrapped height from that available width so the field
            // hugs its content instead of being a MinHeight the layout can inflate, and pass
            // an explicit Width so it can't grow wider than the content column.
            var taStyle = EditorStyles.textArea;   // wordWrap == true by default
            float availW = Mathf.Max(40f, _sideContentW);
            float h = taStyle.CalcHeight(new GUIContent(currentText ?? ""), availW);
            h = Mathf.Max(h, isDescription ? 50f : 18f);
            EditorGUI.BeginChangeCheck();
            string edited = EditorGUILayout.TextArea(currentText, taStyle,
                GUILayout.Width(availW), GUILayout.Height(h), GUILayout.ExpandWidth(false));
            if (imported && EditorGUI.EndChangeCheck())
                StageLocText(n, isDescription, edited);
        }
        if (!imported)
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(key)))
                if (GUILayout.Button("Import for editing", EditorStyles.miniButton))
                {
                    if (ArchiveTranslations.EnsureOverride(key, currentText) != null)
                    { InvalidateImportedCache(); Repaint(); }
                }
        EditorGUILayout.Space(4);
    }

    void StageLocText(TechTreeData.Node n, bool isDescription, string text)
    {
        var p = Stage(n);
        string baseText = isDescription ? n.DescriptionText : n.TitleText;
        if (isDescription) p.DescriptionText = text != baseText ? text : null;
        else                p.TitleText      = text != baseText ? text : null;
        DropIfClean(n);
        Repaint();
    }

    // ── Edit operations (staging into the overlay; disk write is at Save) ──────
    void RemovePrereq(TechTreeData.Node n, string prereq)
    {
        var p = Stage(n);
        p.Prereqs ??= new List<string>(EffPrereqs(n));
        p.Prereqs.Remove(prereq);
        if (p.Prereqs.SequenceEqual(n.BasePrereqs)) p.Prereqs = null;  // back to base = no change
        DropIfClean(n);
        Repaint();
    }

    void AddPrereqPrompt(TechTreeData.Node n, Rect anchor)
    {
        var current = new HashSet<string>(EffPrereqs(n));
        var candidates = _nodes
            .Where(c => c.Name != n.Name)           // not self
            .Where(c => !current.Contains(c.Name))  // not already a prereq
            .Where(c => !WouldCycle(n, c.Name))     // no cycle (uses effective graph)
            .ToList();

        if (candidates.Count == 0)
        { Debug.Log($"[TechTree] No valid prerequisites to add to {n.Name} (all would self/dup/cycle)."); return; }

        var dd = new TechPickerDropdown(new AdvancedDropdownState(), candidates, picked => AddPrereq(n, picked));
        dd.Show(anchor);
    }

    void AddPrereq(TechTreeData.Node n, string prereq)
    {
        var p = Stage(n);
        p.Prereqs ??= new List<string>(EffPrereqs(n));
        if (!p.Prereqs.Contains(prereq)) p.Prereqs.Add(prereq);
        if (p.Prereqs.SequenceEqual(n.BasePrereqs)) p.Prereqs = null;
        DropIfClean(n);
        Repaint();
    }

    // Adding "target requires candidate" cycles iff candidate already (transitively)
    // depends on target. Walk candidate's prerequisite ancestors via the EFFECTIVE
    // graph (staged edits included); if we reach target, it would cycle.
    bool WouldCycle(TechTreeData.Node target, string candidateName)
    {
        var visited = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(candidateName);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == target.Name) return true;
            if (!visited.Add(cur)) continue;
            if (_byName.TryGetValue(cur, out var node))
                foreach (var pr in EffPrereqs(node)) stack.Push(pr);
        }
        return false;
    }

    // ── Save / Revert ─────────────────────────────────────────────────────────
    void CommitChanges()
    {
        if (_pending.Count == 0) return;
        int pos = 0, pre = 0, loc = 0, skipped = 0;

        // Pass 1 — mapper/def edits (plain ScriptableObjects under Databases/). These are safe
        // to persist via a global SaveAssets() because they aren't Amplitude datatable
        // collections, so Amplitude's save handler won't prompt for them.
        foreach (var kv in _pending)
        {
            if (!_byName.TryGetValue(kv.Key, out var node)) continue;
            var p = kv.Value;

            if (p.X.HasValue || p.Y.HasValue)
            {
                var mapper = TechTreeData.EnsureWritable(node, true, _modPath);
                if (mapper != null)
                {
                    Undo.RecordObject(mapper, "Tech position");
                    TechTreeData.WritePosition(mapper, p.X ?? node.BaseX, p.Y ?? node.BaseY);
                    EditorUtility.SetDirty(mapper);
                    pos++;
                }
                else skipped++;
            }

            if (p.Prereqs != null)
            {
                var def = TechTreeData.EnsureWritable(node, false, _modPath);
                if (def != null)
                {
                    Undo.RecordObject(def, "Tech prerequisites");
                    if (TechTreeData.WritePrereqs(def, p.Prereqs.ToArray())) pre++;
                    EditorUtility.SetDirty(def);
                }
                else skipped++;
            }
        }
        // Save ONLY the mapper/def ScriptableObjects now. This must happen BEFORE the loc
        // edits below, because AssetDatabase.SaveAssets() is global: if the Translations.asset
        // datatable collection were dirty at this point, Amplitude's save handler would pop a
        // "Couldn't create asset file!" dialog for it. The mapper/def are plain SOs, so this
        // save is prompt-free.
        if (pos > 0 || pre > 0)
            AssetDatabase.SaveAssets();

        // Pass 2 — loc edits. SetOverrideText mutates the row in memory and marks the
        // collection dirty. We deliberately do NOT call SaveAssets() after this: the
        // Translations.asset is a DatatableElementCollection, and forcing its save via
        // SaveAssets() triggers Amplitude's prompt. Mirror VanillaDatabaseMount.OverrideVanillaElement,
        // which marks dirty and leaves persistence to Unity's normal save flow (the
        // in-memory mutation is what Reload reads back, so the edit shows immediately).
        foreach (var kv in _pending)
        {
            if (!_byName.TryGetValue(kv.Key, out var node)) continue;
            var p = kv.Value;
            if (p.TitleText != null) { ArchiveTranslations.SetOverrideText(node.TitleKey, p.TitleText); loc++; }
            if (p.DescriptionText != null) { ArchiveTranslations.SetOverrideText(node.DescriptionKey, p.DescriptionText); loc++; }
        }

        Debug.Log($"[TechTree] Saved: {pos} position, {pre} prerequisite, {loc} localization change(s)"
                + (skipped > 0 ? $" Â· {skipped} skipped (no writable target â€” see step 5)" : "") + ".");
        _pending.Clear();
        InvalidateImportedCache();   // commits may have written new override rows
        Reload(keepView: true);
    }

    void RevertChanges()
    {
        if (_pending.Count == 0) return;
        if (EditorUtility.DisplayDialog("Revert", $"Discard {_pending.Count} unsaved change(s)?", "Discard", "Cancel"))
        { _pending.Clear(); InvalidateImportedCache(); Reload(keepView: true); }
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    static Color EraColor(string era) => era switch
    {
        "Era1" => new Color(0.35f, 0.45f, 0.55f),
        "Era2" => new Color(0.35f, 0.55f, 0.45f),
        "Era3" => new Color(0.55f, 0.50f, 0.35f),
        "Era4" => new Color(0.55f, 0.40f, 0.40f),
        "Era5" => new Color(0.45f, 0.40f, 0.55f),
        "Era6" => new Color(0.50f, 0.45f, 0.50f),
        _      => new Color(0.40f, 0.40f, 0.42f),
    };
    static string ShortEra(string era)  => string.IsNullOrEmpty(era) ? "" : era.Replace("Era", "E");
    static string ShortTier(string tier) => string.IsNullOrEmpty(tier) ? "" : tier.Replace("Tier", "T");

    static bool LineCrossesRect(Vector2 a, Vector2 b, Rect r)
    {
        float minX = Mathf.Min(a.x, b.x), maxX = Mathf.Max(a.x, b.x);
        float minY = Mathf.Min(a.y, b.y), maxY = Mathf.Max(a.y, b.y);
        return !(maxX < r.xMin || minX > r.xMax || maxY < r.yMin || minY > r.yMax);
    }
}

/// <summary>
/// Searchable tech picker for adding a prerequisite. Groups candidates by era for
/// navigability; AdvancedDropdown's built-in search filters across all of them.
/// Maps the picked item back to a tech name by item reference (no id juggling).
/// </summary>
class TechPickerDropdown : AdvancedDropdown
{
    readonly List<TechTreeData.Node> _candidates;
    readonly Action<string> _onPick;
    readonly Dictionary<AdvancedDropdownItem, string> _map = new();

    public TechPickerDropdown(AdvancedDropdownState state, List<TechTreeData.Node> candidates, Action<string> onPick)
        : base(state)
    {
        _candidates = candidates;
        _onPick = onPick;
        minimumSize = new Vector2(320, 420);
    }

    protected override AdvancedDropdownItem BuildRoot()
    {
        var root = new AdvancedDropdownItem("Add prerequisite");
        foreach (var era in _candidates.Select(c => c.Era).Distinct()
                                       .OrderBy(e => string.IsNullOrEmpty(e) ? "zzz" : e))
        {
            var eraItem = new AdvancedDropdownItem(string.IsNullOrEmpty(era) ? "(no era)" : era);
            foreach (var c in _candidates.Where(c => c.Era == era).OrderBy(c => c.Label))
            {
                var it = new AdvancedDropdownItem($"{c.Label}   ({c.Name})");
                _map[it] = c.Name;
                eraItem.AddChild(it);
            }
            root.AddChild(eraItem);
        }
        return root;
    }

    protected override void ItemSelected(AdvancedDropdownItem item)
    {
        if (_map.TryGetValue(item, out var name)) _onPick(name);
    }
}