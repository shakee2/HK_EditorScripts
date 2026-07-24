using System;
using System.Collections.Generic;
using System.Linq;
using HK.ModTools.Shared;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

/// <summary>
/// Unit Family Lines viewer/editor: pan/zoom DAG of UnitFamilyDefinitions
/// linked by SerializableNextFamilyName, with unit list (reassign family/level)
/// and JSON import/export for assignments + next links.
/// </summary>
public class UnitFamilyLinesWindow : EditorWindow
{
    UnitFamilyLinesData.Graph _graph;
    UnitFamilyLinesData.FamilyNode _selected;
    HashSet<string> _highlight = new(StringComparer.OrdinalIgnoreCase);
    // Precomputed on selection change so per-repaint edge/node drawing is O(1) lookups
    // instead of re-walking ForwardChain / LINQ per edge and per node.
    readonly HashSet<string> _prevSet = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _preEdges = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _afterEdges = new(StringComparer.OrdinalIgnoreCase);

    // Reused GUIStyles — rebuilding these per node/band every repaint churns the GC.
    GUIStyle _domainLabelStyle, _noPathStyle, _nodeLabelStyle;

    Vector2 _pan;
    float _zoom = 1f;
    bool _framed, _panning;
    string _search = "";
    Vector2 _unitScroll;
    AdvancedDropdownState _familyDdState = new();

    const float CELL = 52f;
    const float NODE_W = 156f, NODE_H = 44f;
    const float LABEL_MIN_ZOOM = 0.4f;
    const float BOTTOM_H_MIN = 120f, BOTTOM_H_MAX = 420f;
    float _bottomH = 180f;
    bool _resizingBottom;

    static readonly Color ColBg = new(0.16f, 0.16f, 0.17f);
    static readonly Color ColNode = new(0.28f, 0.34f, 0.42f);
    static readonly Color ColSelected = new(0.45f, 0.55f, 0.35f);
    static readonly Color ColPre = new(0.40f, 0.48f, 0.62f);
    static readonly Color ColAfter = new(0.38f, 0.50f, 0.48f);
    static readonly Color ColDim = new(0.22f, 0.22f, 0.24f);
    static readonly Color ColObsolete = new(0.45f, 0.32f, 0.28f);
    static readonly Color ColStub = new(0.55f, 0.22f, 0.22f);
    static readonly Color ColEdge = new(0.45f, 0.55f, 0.65f, 0.55f);
    static readonly Color ColEdgeHot = new(0.85f, 0.78f, 0.35f, 0.95f);
    static readonly Color ColEdgePre = new(0.55f, 0.70f, 0.95f, 0.9f);
    static readonly Color ColMod = new(0.4f, 0.7f, 1f);

    string BottomHKey => "UnitFamilyLines.BottomH";

    [MenuItem("Tools/shakee's Tools/Unit Family Lines", false, 5)]
    static void Open()
    {
        var w = GetWindow<UnitFamilyLinesWindow>("Unit Family Lines");
        w.minSize = new Vector2(520, 360);
        w.maxSize = new Vector2(4000, 4000);
        w.Show();
        w.Focus();
    }

    void OnEnable()
    {
        _bottomH = Mathf.Clamp(EditorPrefs.GetFloat(BottomHKey, _bottomH), BOTTOM_H_MIN, BOTTOM_H_MAX);
        EditorApplication.projectChanged += OnProjectChanged;
        try { Reload(); }
        catch (Exception e)
        {
            Debug.LogError($"[UnitFamilyLines] Build failed on open: {e}");
            _graph = new UnitFamilyLinesData.Graph();
        }
    }

    void OnDisable()
    {
        EditorApplication.projectChanged -= OnProjectChanged;
    }

    void OnProjectChanged() => Reload(keepView: true);

    void Reload(bool keepView = false, bool forceReferenceRefresh = false)
    {
        string keep = _selected?.Name;
        _graph = UnitFamilyLinesData.Build(forceReferenceRefresh);
        _selected = null;
        _highlight.Clear();
        if (!string.IsNullOrEmpty(keep) && _graph.ByName.TryGetValue(keep, out var n))
            Select(n, frame: false, ping: false);
        if (!keepView) _framed = false;
        Repaint();
    }

    void Select(UnitFamilyLinesData.FamilyNode n, bool frame, bool ping)
    {
        _selected = n;
        RebuildHighlight();
        if (n != null && ping && n.Asset != null)
        {
            EditorGUIUtility.PingObject(n.Asset);
            Selection.activeObject = n.Asset;
        }
        if (frame && n != null) FrameOn(n);
        Repaint();
    }

    void RebuildHighlight()
    {
        _highlight.Clear();
        _prevSet.Clear();
        _preEdges.Clear();
        _afterEdges.Clear();
        if (_selected == null || _graph == null) return;

        _highlight.Add(_selected.Name);
        foreach (var p in _selected.PreviousNames)
        {
            _highlight.Add(p);
            _prevSet.Add(p);
            _preEdges.Add(EdgeKey(p, _selected.Name)); // parent → selected
        }

        // Forward upgrade chain from the selection: highlight nodes + mark consecutive edges.
        string prev = null;
        foreach (var name in UnitFamilyLinesData.ForwardChain(_graph, _selected.Name))
        {
            _highlight.Add(name);
            if (prev != null) _afterEdges.Add(EdgeKey(prev, name));
            prev = name;
        }
    }

    static string EdgeKey(string from, string to) => (from ?? "") + "\u0001" + (to ?? "");

    Vector2 W2S(float lx, float ly) =>
        new(lx * CELL * _zoom + _pan.x, ly * CELL * _zoom + _pan.y);

    void FrameAll(Rect canvas)
    {
        if (_graph == null || _graph.Families.Count == 0) return;
        float maxX = _graph.Families.Max(f => f.LayoutX) + 2.2f;
        float minY = _graph.Families.Min(f => f.LayoutY) - 1.5f;
        float maxY = _graph.Families.Max(f => f.LayoutY) + 2.2f;
        float zx = canvas.width / (maxX * CELL);
        float zy = canvas.height / ((maxY - minY) * CELL);
        _zoom = Mathf.Clamp(Mathf.Min(zx, zy) * 0.92f, 0.15f, 2f);
        _pan = new Vector2(
            canvas.x + (canvas.width - maxX * CELL * _zoom) * 0.5f,
            canvas.y + (canvas.height - (maxY - minY) * CELL * _zoom) * 0.5f - minY * CELL * _zoom);
        _framed = true;
    }

    void FrameOn(UnitFamilyLinesData.FamilyNode n)
    {
        if (n == null) return;
        Vector2 center = W2S(n.LayoutX, n.LayoutY) +
                         new Vector2(NODE_W * _zoom * 0.5f, NODE_H * _zoom * 0.5f);
        // Will be adjusted after canvas known; approximate using position
        var canvasGuess = new Rect(0, 22, position.width, position.height - _bottomH - 22);
        _pan += new Vector2(canvasGuess.center.x, canvasGuess.center.y) - center;
    }

    void OnGUI()
    {
        if (WindowMinimize.DrawMinimizedChrome(this)) return;

        DrawToolbar();

        bool vanillaMounted = VanillaDatabaseMount.IsMounted || VanillaDatabaseMount.TryMount(out _);
        if (!vanillaMounted)
            EditorGUILayout.HelpBox(
                $"Vanilla databases not mounted ({VanillaDatabaseMount.LastError}) — showing Databases content only.",
                MessageType.Info);
        if (_graph != null && !string.IsNullOrEmpty(_graph.Error))
            EditorGUILayout.HelpBox(_graph.Error, MessageType.Error);

        float top = EditorStyles.toolbar.fixedHeight > 0 ? EditorStyles.toolbar.fixedHeight : 21f;
        if (!vanillaMounted || (_graph != null && !string.IsNullOrEmpty(_graph.Error)))
            top += 40f;

        _bottomH = Mathf.Clamp(_bottomH, BOTTOM_H_MIN, Mathf.Max(BOTTOM_H_MIN, position.height - top - 80f));
        Rect canvas = new(0, top, position.width, position.height - top - _bottomH);
        Rect splitter = new(0, canvas.yMax - 2f, position.width, 4f);
        Rect bottom = new(0, canvas.yMax, position.width, _bottomH);

        HandleBottomSplitter(splitter);
        if (!_framed) FrameAll(canvas);
        HandleInput(canvas);
        DrawCanvas(canvas);
        DrawBottom(bottom);
    }

    void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(60)))
            Reload(keepView: true, forceReferenceRefresh: true);
        if (GUILayout.Button("Fit", EditorStyles.toolbarButton, GUILayout.Width(40)))
            _framed = false;

        GUILayout.Space(8);
        GUILayout.Label("Search", GUILayout.Width(44));
        string newSearch = GUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120), GUILayout.MaxWidth(260));
        if (newSearch != _search)
        {
            _search = newSearch;
            TrySearchJump();
        }
        if (GUILayout.Button("Go", EditorStyles.toolbarButton, GUILayout.Width(32)))
            TrySearchJump();
        if (GUILayout.Button("Export JSON", EditorStyles.toolbarButton, GUILayout.Width(90)))
            ExportJson();
        if (GUILayout.Button("Import JSON", EditorStyles.toolbarButton, GUILayout.Width(90)))
            ImportJson();

        GUILayout.FlexibleSpace();

        bool mounted = VanillaDatabaseMount.IsMounted;
        GUI.color = mounted ? Color.white : new Color(1f, 0.6f, 0.6f);
        GUILayout.Label(mounted ? "vanilla mounted" : "vanilla missing", EditorStyles.toolbarButton, GUILayout.Width(100));
        GUI.color = Color.white;
        if (GUILayout.Button("Remount", EditorStyles.toolbarButton, GUILayout.Width(64)))
        {
            VanillaDatabaseMount.Invalidate();
            if (!VanillaDatabaseMount.TryMount(out var err))
                Debug.LogWarning($"[UnitFamilyLines] {err}");
            Reload(keepView: true, forceReferenceRefresh: true);
        }

        if (_graph != null)
            GUILayout.Label($"{_graph.Families.Count} families", EditorStyles.toolbarButton, GUILayout.Width(90));

        WindowMinimize.DrawToolbarButton(this);
        EditorGUILayout.EndHorizontal();
    }

    void ImportJson()
    {
        if (_graph == null)
        {
            EditorUtility.DisplayDialog("Unit Family Lines", "Nothing loaded — reload first.", "OK");
            return;
        }
        string path = EditorUtility.OpenFilePanel("Import Unit Family Lines", Application.dataPath, "json");
        if (string.IsNullOrEmpty(path)) return;
        string json;
        try { json = System.IO.File.ReadAllText(path); }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Unit Family Lines", "Read failed:\n" + e.Message, "OK");
            return;
        }
        if (!UnitFamilyLinesData.TryParseExportJson(json, out var families, out var parseErr))
        {
            EditorUtility.DisplayDialog("Unit Family Lines", "Parse failed:\n" + parseErr, "OK");
            return;
        }

        var diff = UnitFamilyLinesData.DiffImport(_graph, families);
        if (diff.MissingUnits.Count > 0)
            Debug.LogWarning($"[UnitFamilyLines] Import: {diff.MissingUnits.Count} unit(s) not found:\n  " +
                             string.Join("\n  ", diff.MissingUnits.Take(40)));

        int creates = diff.FamiliesToCreate.Count;
        int famWrites = diff.FamilyWrites.Count;
        int unitWrites = diff.UnitWrites.Count;
        if (creates == 0 && famWrites == 0 && unitWrites == 0)
        {
            string extra = diff.MissingUnits.Count > 0
                ? $"\n({diff.MissingUnits.Count} unit name(s) in JSON were not found — see Console.)"
                : "";
            EditorUtility.DisplayDialog("Unit Family Lines", "No changes vs live graph." + extra, "OK");
            return;
        }

        var plan = new System.Text.StringBuilder();
        plan.AppendLine($"Create families ({creates}):");
        foreach (var n in diff.FamiliesToCreate) plan.AppendLine("  + " + n);
        plan.AppendLine($"Family next/obsolete writes ({famWrites}):");
        foreach (var (n, next, obs) in diff.FamilyWrites)
            plan.AppendLine($"  {n} → next={(string.IsNullOrEmpty(next) ? "(terminal)" : next)}" +
                            (obs.HasValue ? $" obsolete={obs.Value}" : ""));
        plan.AppendLine($"Unit family/level writes ({unitWrites}):");
        foreach (var (row, fam, lvl) in diff.UnitWrites)
            plan.AppendLine($"  {row.Name}: {row.FamilyName}/L{row.Level} → {fam}/L{lvl}");
        Debug.Log("[UnitFamilyLines] Import plan:\n" + plan);

        string msg =
            $"Create {creates} families, update {famWrites} family link(s), update {unitWrites} unit(s)\n" +
            $"({diff.NeedLiftFamilies} family import(s), {diff.NeedLiftUnits} unit import(s) needed).\n\n" +
            (diff.MissingUnits.Count > 0 ? $"{diff.MissingUnits.Count} missing unit name(s) will be skipped (see Console).\n\n" : "") +
            "Continue?";
        if (!EditorUtility.DisplayDialog("Import Unit Family Lines", msg, "Apply", "Cancel"))
            return;

        int undo = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Import Unit Family Lines JSON");
        if (!UnitFamilyLinesData.ApplyImport(_graph, diff, out var report))
        {
            Undo.RevertAllDownToGroup(undo);
            EditorUtility.DisplayDialog("Unit Family Lines", "Import aborted:\n" + report, "OK");
            return;
        }
        Undo.CollapseUndoOperations(undo);
        Debug.Log("[UnitFamilyLines] Import applied:\n" + report);
        Reload(keepView: true);
    }

    void ExportJson()
    {
        if (_graph == null || _graph.Families.Count == 0)
        {
            EditorUtility.DisplayDialog("Unit Family Lines", "Nothing to export — reload first.", "OK");
            return;
        }
        string path = EditorUtility.SaveFilePanel(
            "Export Unit Family Lines", Application.dataPath, "UnitFamilyLines", "json");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            System.IO.File.WriteAllText(path, UnitFamilyLinesData.ExportJson(_graph));
            EditorUtility.RevealInFinder(path);
            Debug.Log($"[UnitFamilyLines] Exported {_graph.Families.Count(f => !f.IsBrokenStub)} families → {path}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[UnitFamilyLines] Export failed: {e.Message}");
            EditorUtility.DisplayDialog("Unit Family Lines", "Export failed:\n" + e.Message, "OK");
        }
    }

    void TrySearchJump()
    {
        if (_graph == null || string.IsNullOrWhiteSpace(_search)) return;
        string q = _search.Trim();
        var hit = _graph.Families.FirstOrDefault(f =>
            f.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
            f.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
        if (hit == null)
        {
            hit = _graph.Families.FirstOrDefault(f =>
                f.Units.Any(u => u.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
        }
        if (hit != null) Select(hit, frame: true, ping: true);
    }

    void HandleBottomSplitter(Rect splitter)
    {
        EditorGUIUtility.AddCursorRect(splitter, MouseCursor.ResizeVertical);
        var e = Event.current;
        if (e.type == EventType.MouseDown && splitter.Contains(e.mousePosition))
        { _resizingBottom = true; e.Use(); }
        else if (e.type == EventType.MouseDrag && _resizingBottom)
        {
            _bottomH = Mathf.Clamp(_bottomH - e.delta.y, BOTTOM_H_MIN,
                Mathf.Max(BOTTOM_H_MIN, position.height - 120f));
            EditorPrefs.SetFloat(BottomHKey, _bottomH);
            e.Use(); Repaint();
        }
        else if (e.type == EventType.MouseUp && _resizingBottom)
        { _resizingBottom = false; e.Use(); }
    }

    void HandleInput(Rect canvas)
    {
        var e = Event.current;
        if (!canvas.Contains(e.mousePosition) && !_panning) return;

        if (e.type == EventType.ScrollWheel)
        {
            float old = _zoom;
            _zoom = Mathf.Clamp(_zoom * (1f - e.delta.y * 0.05f), 0.12f, 3f);
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
                bool dbl = e.clickCount >= 2;
                Select(hit, frame: false, ping: true);
                if (dbl && hit.Asset != null)
                {
                    EditorGUIUtility.PingObject(hit.Asset);
                    Selection.activeObject = hit.Asset;
                }
                e.Use();
            }
        }
    }

    UnitFamilyLinesData.FamilyNode NodeAt(Vector2 screen)
    {
        if (_graph == null) return null;
        foreach (var n in _graph.Families)
        {
            Vector2 p = W2S(n.LayoutX, n.LayoutY);
            if (new Rect(p.x, p.y, NODE_W * _zoom, NODE_H * _zoom).Contains(screen))
                return n;
        }
        return null;
    }

    bool IsPreEdge(string from, string to) => _preEdges.Contains(EdgeKey(from, to));

    bool IsAfterEdge(string from, string to) => _afterEdges.Contains(EdgeKey(from, to));

    void DrawCanvas(Rect canvas)
    {
        EditorGUI.DrawRect(canvas, ColBg);
        if (_graph == null || _graph.Families.Count == 0)
        {
            GUI.Label(canvas, "No UnitFamilyDefinition data loaded.", EditorStyles.centeredGreyMiniLabel);
            return;
        }

        GUI.BeginClip(canvas);
        Rect local = new(0, 0, canvas.width, canvas.height);
        Vector2 panSave = _pan;
        _pan -= new Vector2(canvas.x, canvas.y);

        bool hasSel = _selected != null;
        bool showLabels = _zoom >= LABEL_MIN_ZOOM;

        // Domain band labels + soft separators (above singleton strip when present).
        // Y extents are precomputed in the graph; styles are reused (see EnsureStyles).
        if (_graph.DomainBands != null && _graph.DomainBands.Count > 0)
        {
            EnsureStyles();
            _domainLabelStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(13f * Mathf.Min(_zoom, 1.2f)), 10, 16);
            _noPathStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(9f * Mathf.Min(_zoom, 1.2f)), 8, 11);

            foreach (var (domain, x0, x1, bandMinY, bandMaxY) in _graph.DomainBands)
            {
                float minY = bandMinY - 1.0f;
                Vector2 a = W2S(x0, minY);
                Vector2 b = W2S(x1, minY);
                var labelRect = new Rect(a.x, a.y, Mathf.Max(40f, b.x - a.x), 18f * Mathf.Max(_zoom, 0.5f));
                if (labelRect.Overlaps(local))
                {
                    _domainLabelStyle.normal.textColor = DomainLabelColor(domain);
                    GUI.Label(labelRect, domain.ToString().ToUpperInvariant(), _domainLabelStyle);
                }
                Vector2 top = W2S(x0 - 0.35f, minY);
                Vector2 bot = W2S(x0 - 0.35f, bandMaxY + 1.5f);
                Handles.color = new Color(1f, 1f, 1f, 0.08f);
                Handles.DrawLine(top, bot);

                // Hairline in the gap between singleton strip and linked DAG (not through nodes).
                bool hasSingleton = bandMinY <= -0.001f;   // singletons sit at negative Y
                bool hasLinked = bandMaxY >= -0.001f;       // linked DAG starts at Y >= 0
                if (hasSingleton && hasLinked)
                {
                    // Singletons sit at Y <= -singletonSep (~-2.4); linked at Y >= 0.
                    // Draw midpoint of that gap, inset from band edges so it doesn't look like overflow.
                    const float sepY = -1.15f;
                    float inset = 0.25f;
                    Vector2 s0 = W2S(x0 + inset, sepY);
                    Vector2 s1 = W2S(x1 - inset - 0.5f, sepY);
                    Handles.color = new Color(1f, 1f, 1f, 0.22f);
                    Handles.DrawLine(s0, s1);
                    GUI.Label(new Rect(s0.x + 4f, s0.y - 14f * _zoom, 140f, 14f), "no upgrade path ↑", _noPathStyle);
                }
            }
        }

        // Edges
        foreach (var n in _graph.Families)
        {
            if (string.IsNullOrEmpty(n.NextName)) continue;
            if (!_graph.ByName.TryGetValue(n.NextName, out var dst)) continue;

            Vector2 from = W2S(n.LayoutX, n.LayoutY) + new Vector2(NODE_W * _zoom * 0.5f, NODE_H * _zoom);
            Vector2 to = W2S(dst.LayoutX, dst.LayoutY) + new Vector2(NODE_W * _zoom * 0.5f, 0);
            if (!local.Overlaps(RectFrom(from, to))) continue;

            Color c = ColEdge;
            float w = 2f;
            if (hasSel)
            {
                if (IsPreEdge(n.Name, dst.Name)) { c = ColEdgePre; w = 3.5f; }
                else if (IsAfterEdge(n.Name, dst.Name)) { c = ColEdgeHot; w = 3.5f; }
                else c = new Color(ColEdge.r, ColEdge.g, ColEdge.b, 0.18f);
            }
            if (n.NextMissing || dst.IsBrokenStub)
                c = new Color(1f, 0.35f, 0.3f, hasSel && !IsPreEdge(n.Name, dst.Name) && !IsAfterEdge(n.Name, dst.Name) ? 0.25f : 0.85f);

            float bend = 40f * _zoom;
            Handles.DrawBezier(from, to,
                from + Vector2.up * bend,
                to - Vector2.up * bend,
                c, null, w);
        }

        // Nodes
        if (showLabels)
        {
            EnsureStyles();
            _nodeLabelStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(11f * Mathf.Min(_zoom, 1.25f)), 7, 13);
        }
        foreach (var n in _graph.Families)
        {
            Vector2 p = W2S(n.LayoutX, n.LayoutY);
            var r = new Rect(p.x, p.y, NODE_W * _zoom, NODE_H * _zoom);
            if (!r.Overlaps(local)) continue;

            Color tint = DomainNodeColor(n.Domain);
            if (n.IsBrokenStub) tint = ColStub;
            else if (n.IsObsolete) tint = Color.Lerp(tint, ColObsolete, 0.55f);
            if (n.Layer < 0) // singleton strip
                tint = Color.Lerp(tint, ColDim, 0.25f);

            if (hasSel)
            {
                if (n == _selected) tint = ColSelected;
                else if (_prevSet.Contains(n.Name))
                    tint = ColPre;
                else if (_highlight.Contains(n.Name) && n != _selected)
                    tint = ColAfter;
                else
                    tint = Color.Lerp(tint, ColDim, 0.65f);
            }

            EditorGUI.DrawRect(r, tint);
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), new Color(0, 0, 0, 0.4f));

            if (n.IsModded)
            {
                float bw = Mathf.Max(3f, 4f * _zoom);
                EditorGUI.DrawRect(new Rect(r.x, r.y, bw, r.height), ColMod);
            }

            if (showLabels)
            {
                string label = n.Label;
                if (n.Units.Count > 0) label += $"\n({n.Units.Count})";
                if (n.IsObsolete) label = "† " + label;
                GUI.Label(r, label, _nodeLabelStyle);
            }
        }

        _pan = panSave;
        GUI.EndClip();
    }

    void EnsureStyles()
    {
        _domainLabelStyle ??= new GUIStyle(EditorStyles.boldLabel);
        _noPathStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = new Color(1f, 1f, 1f, 0.45f) }
        };
        _nodeLabelStyle ??= new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold,
            wordWrap = true,
            clipping = TextClipping.Clip,
            normal = { textColor = Color.white }
        };
    }

    static Rect RectFrom(Vector2 a, Vector2 b)
    {
        float x = Mathf.Min(a.x, b.x);
        float y = Mathf.Min(a.y, b.y);
        return new Rect(x, y, Mathf.Abs(a.x - b.x) + 1f, Mathf.Abs(a.y - b.y) + 1f);
    }

    void DrawBottom(Rect bottom)
    {
        EditorGUI.DrawRect(bottom, new Color(0.19f, 0.19f, 0.20f));
        GUILayout.BeginArea(bottom, EditorStyles.helpBox);
        if (_selected == null)
        {
            EditorGUILayout.LabelField("Click a family node to list units. Edit Next, Level, or Family (auto-imports vanilla/mounted). Alt/MMB drag to pan, scroll to zoom. Flow is top→bottom.",
                EditorStyles.wordWrappedMiniLabel);
            GUILayout.EndArea();
            return;
        }

        var n = _selected;
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField($"Units — {n.Name}  [{n.Domain}]", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (n.PreviousNames.Length > 0)
            EditorGUILayout.LabelField($"Pre: {string.Join(", ", n.PreviousNames.Select(DisplayShort))}",
                EditorStyles.miniLabel, GUILayout.MaxWidth(bottom.width * 0.35f));

        GUILayout.Label("Next:", EditorStyles.miniLabel, GUILayout.Width(36));
        string nextLabel = string.IsNullOrEmpty(n.NextName) ? "(terminal)" :
            (n.NextMissing ? n.NextName + " MISSING" : DisplayShort(n.NextName));
        if (GUILayout.Button(nextLabel, EditorStyles.miniButton, GUILayout.Width(140)))
        {
            var rect = GUILayoutUtility.GetLastRect();
            var candidates = _graph.Families
                .Where(f => !f.IsBrokenStub &&
                            !string.Equals(f.Name, n.Name, StringComparison.OrdinalIgnoreCase) &&
                            !WouldCreateNextCycle(n, f.Name))
                .ToList();
            var dd = new FamilyPickerDropdown(_familyDdState, candidates,
                picked => ApplyFamilyNext(n, picked),
                title: "Set next family",
                clearLabel: "(terminal — clear next)",
                allowClear: true);
            dd.Show(rect);
        }
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(n.NextName)))
            if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22)))
                ApplyFamilyNext(n, "");

        if (n.Asset != null && GUILayout.Button("Select family", EditorStyles.miniButton, GUILayout.Width(90)))
        {
            EditorGUIUtility.PingObject(n.Asset);
            Selection.activeObject = n.Asset;
        }
        EditorGUILayout.EndHorizontal();

        if (n.IsBrokenStub)
        {
            EditorGUILayout.HelpBox("Broken Next target — no UnitFamilyDefinition with this name.", MessageType.Warning);
            GUILayout.EndArea();
            return;
        }

        _unitScroll = EditorGUILayout.BeginScrollView(_unitScroll);
        if (n.Units.Count == 0)
            EditorGUILayout.LabelField("(no UnitDefinitions use this SerializableFamily)", EditorStyles.miniLabel);
        else
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Scope", EditorStyles.miniBoldLabel, GUILayout.Width(58));
            GUILayout.Label("Level", EditorStyles.miniBoldLabel, GUILayout.Width(44));
            GUILayout.Label("Name", EditorStyles.miniBoldLabel, GUILayout.ExpandWidth(true));
            GUILayout.Label("Family", EditorStyles.miniBoldLabel, GUILayout.Width(160));
            EditorGUILayout.EndHorizontal();

            foreach (var u in n.Units.ToList())
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(UnitFamilyLinesData.ScopeLabel(u.Source), EditorStyles.miniLabel, GUILayout.Width(58));

                EditorGUI.BeginChangeCheck();
                int newLevel = EditorGUILayout.IntField(u.Level, GUILayout.Width(44));
                if (EditorGUI.EndChangeCheck() && newLevel != u.Level)
                    ApplyUnitLevel(u, newLevel);

                if (GUILayout.Button(u.Name, EditorStyles.linkLabel, GUILayout.ExpandWidth(true)))
                {
                    if (u.Asset != null)
                    {
                        EditorGUIUtility.PingObject(u.Asset);
                        Selection.activeObject = u.Asset;
                    }
                }

                string famLabel = DisplayShort(u.FamilyName ?? n.Name);
                if (GUILayout.Button(famLabel, EditorStyles.miniButton, GUILayout.Width(160)))
                {
                    var rect = GUILayoutUtility.GetLastRect();
                    var candidates = _graph.Families
                        .Where(f => !f.IsBrokenStub &&
                                    !string.Equals(f.Name, u.FamilyName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var dd = new FamilyPickerDropdown(_familyDdState, candidates,
                        picked => ApplyUnitFamily(u, picked));
                    dd.Show(rect);
                }
                EditorGUILayout.EndHorizontal();
            }
        }
        EditorGUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    void ApplyUnitLevel(UnitFamilyLinesData.UnitRow u, int level)
    {
        var writable = UnitFamilyLinesData.EnsureWritableUnit(u);
        if (writable == null)
        {
            EditorUtility.DisplayDialog("Unit Family Lines",
                $"Could not import '{u.Name}' for editing.", "OK");
            return;
        }
        Undo.RecordObject(writable, "Unit level");
        UnitFamilyLinesData.WriteFamilyAndLevel(writable, null, level);
        UnityEditor.AssetDatabase.SaveAssets();
        // Incremental: level only re-sorts the family; no rescan / reflow needed.
        UnitFamilyLinesData.UpdateUnitLevel(_graph, u, level);
        Repaint();
    }

    void ApplyUnitFamily(UnitFamilyLinesData.UnitRow u, string familyName)
    {
        if (string.IsNullOrEmpty(familyName)) return;
        var writable = UnitFamilyLinesData.EnsureWritableUnit(u);
        if (writable == null)
        {
            EditorUtility.DisplayDialog("Unit Family Lines",
                $"Could not import '{u.Name}' for editing.", "OK");
            return;
        }
        Undo.RecordObject(writable, "Unit family");
        UnitFamilyLinesData.WriteFamilyAndLevel(writable, familyName, null);
        UnityEditor.AssetDatabase.SaveAssets();
        // Incremental in-memory move + reflow, avoiding a full project/bundle rescan.
        var target = UnitFamilyLinesData.MoveUnitToFamily(_graph, u, familyName);
        if (target != null)
            Select(target, frame: true, ping: false);
        else
            Reload(keepView: true); // fell out of sync — rebuild from disk
    }

    void ApplyFamilyNext(UnitFamilyLinesData.FamilyNode node, string nextName)
    {
        if (node == null || node.IsBrokenStub) return;
        nextName ??= "";
        if (!string.IsNullOrEmpty(nextName) && WouldCreateNextCycle(node, nextName))
        {
            EditorUtility.DisplayDialog("Unit Family Lines",
                $"Setting Next to '{nextName}' would create a cycle.", "OK");
            return;
        }
        var writable = UnitFamilyLinesData.EnsureWritableFamily(node);
        if (writable == null)
        {
            EditorUtility.DisplayDialog("Unit Family Lines",
                $"Could not import '{node.Name}' for editing.", "OK");
            return;
        }
        Undo.RecordObject(writable, "Family next");
        UnitFamilyLinesData.WriteNext(writable, nextName);
        UnityEditor.AssetDatabase.SaveAssets();
        // Incremental link reflow (stubs/prev/domains/layout) — no project/bundle rescan.
        UnitFamilyLinesData.SetFamilyNext(_graph, node, nextName);
        Select(node, frame: false, ping: false);
    }

    /// <summary>
    /// True if setting <paramref name="from"/>'s next to <paramref name="candidate"/>
    /// would make candidate's forward chain reach <paramref name="from"/> (cycle).
    /// </summary>
    bool WouldCreateNextCycle(UnitFamilyLinesData.FamilyNode from, string candidate)
    {
        if (_graph == null || from == null || string.IsNullOrEmpty(candidate)) return false;
        if (string.Equals(from.Name, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var name in UnitFamilyLinesData.ForwardChain(_graph, candidate))
        {
            if (string.Equals(name, from.Name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static Color DomainNodeColor(UnitFamilyLinesData.UnitDomain d) => d switch
    {
        UnitFamilyLinesData.UnitDomain.Land => new Color(0.30f, 0.38f, 0.32f),
        UnitFamilyLinesData.UnitDomain.Naval => new Color(0.26f, 0.34f, 0.48f),
        UnitFamilyLinesData.UnitDomain.Air => new Color(0.34f, 0.32f, 0.44f),
        _ => ColNode,
    };

    static Color DomainLabelColor(UnitFamilyLinesData.UnitDomain d) => d switch
    {
        UnitFamilyLinesData.UnitDomain.Land => new Color(0.55f, 0.85f, 0.55f),
        UnitFamilyLinesData.UnitDomain.Naval => new Color(0.55f, 0.75f, 1f),
        UnitFamilyLinesData.UnitDomain.Air => new Color(0.75f, 0.65f, 1f),
        _ => new Color(0.75f, 0.75f, 0.75f),
    };

    static string DisplayShort(string name)
    {
        const string p = "UnitFamily_";
        return !string.IsNullOrEmpty(name) && name.StartsWith(p, StringComparison.OrdinalIgnoreCase)
            ? name.Substring(p.Length) : name;
    }
}

/// <summary>
/// Searchable family picker (DatabaseBrowser TypeDropdown / TechTree picker style).
/// Groups by domain; AdvancedDropdown built-in search filters across groups.
/// </summary>
class FamilyPickerDropdown : AdvancedDropdown
{
    readonly List<UnitFamilyLinesData.FamilyNode> _candidates;
    readonly Action<string> _onPick;
    readonly Dictionary<AdvancedDropdownItem, string> _map = new();
    readonly string _title;
    readonly string _clearLabel;
    readonly bool _allowClear;

    public FamilyPickerDropdown(
        AdvancedDropdownState state,
        List<UnitFamilyLinesData.FamilyNode> candidates,
        Action<string> onPick,
        string title = "Reassign family",
        string clearLabel = null,
        bool allowClear = false) : base(state)
    {
        _candidates = candidates;
        _onPick = onPick;
        _title = title;
        _clearLabel = clearLabel ?? "(clear)";
        _allowClear = allowClear;
        minimumSize = new Vector2(280, 360);
    }

    protected override AdvancedDropdownItem BuildRoot()
    {
        var root = new AdvancedDropdownItem(_title);
        if (_allowClear)
        {
            var clear = new AdvancedDropdownItem(_clearLabel);
            _map[clear] = "";
            root.AddChild(clear);
        }
        foreach (UnitFamilyLinesData.UnitDomain domain in new[]
                 {
                     UnitFamilyLinesData.UnitDomain.Land,
                     UnitFamilyLinesData.UnitDomain.Naval,
                     UnitFamilyLinesData.UnitDomain.Air,
                     UnitFamilyLinesData.UnitDomain.Other
                 })
        {
            var group = _candidates.Where(c => c.Domain == domain)
                .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
            if (group.Count == 0) continue;
            var domainItem = new AdvancedDropdownItem(domain.ToString());
            foreach (var c in group)
            {
                var it = new AdvancedDropdownItem($"{c.Label}   ({c.Name})");
                _map[it] = c.Name;
                domainItem.AddChild(it);
            }
            root.AddChild(domainItem);
        }
        return root;
    }

    protected override void ItemSelected(AdvancedDropdownItem item)
    {
        if (_map.TryGetValue(item, out var name)) _onPick?.Invoke(name);
    }
}
