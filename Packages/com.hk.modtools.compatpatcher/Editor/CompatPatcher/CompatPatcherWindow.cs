using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace HK.CompatPatcher
{
    public class CompatPatcherWindow : EditorWindow
    {
        [Serializable] class SourceEntry { public string name; public string path; }

        enum StatusFilter { Conflicts, All, New, Identical }
        static readonly string[] STATUS_LABELS = { "Conflicts", "All", "New", "Identical" };
        enum Col { Element, Type, By, Winner, Status, Diffs }

        [SerializeField] List<SourceEntry> _sources = new List<SourceEntry>();
        [SerializeField] string _sidecarPath = "";

        List<HkMod> _mods;
        AnalyzeResult _result;
        Dictionary<string, Sidecar.Decision> _prior = new Dictionary<string, Sidecar.Decision>();
        readonly Dictionary<string, string> _choice = new Dictionary<string, string>();     // elemKey -> chosen mod
        readonly Dictionary<string, string> _elemStatus = new Dictionary<string, string>(); // elemKey -> new|changed|carried
        readonly Dictionary<string, string> _elemFp = new Dictionary<string, string>();     // elemKey -> fingerprint
        readonly HashSet<string> _patchNames = new HashSet<string>();                        // element names already in Assets/Databases/Patch/
        List<ElementRow> _view = new List<ElementRow>();

        // Load-order validation (recomputed every Compare): findings for the current order and for a
        // reversed order, so we can tell the user which hazards are order-caused vs intrinsic.
        List<Finding> _findings = new List<Finding>();
        List<Finding> _altFindings = new List<Finding>();
        string _altOrderName = "";
        string _validationNote = "";
        Vector2 _validationScroll;
        float _panelTop;   // Y where the 3 panels start (measured after the variable-height top controls)

        StatusFilter _status = StatusFilter.Conflicts;
        string _nameFilter = "";
        List<string> _types = new List<string>();
        string _typeFilter = "";
        AdvancedDropdownState _typeDdState = new AdvancedDropdownState();
        readonly Dictionary<string, string> _typeName = new Dictionary<string, string>(); // "guid:fileID" -> class name
        bool _needsReviewOnly;
        bool _hideWinnerOnly = true; // conflicts whose only diffs are ExtraInWinner (already in winner mod) are hidden by default

        MultiColumnHeader _header;
        MultiColumnHeaderState _headerState;
        Vector2 _tableScroll, _detailScroll;
        ElementRow _selected;
        const float ROW_H = 18f;
        static readonly Color ROW_ALT = new Color(1f, 1f, 1f, 0.03f);
        static readonly Color ROW_SEL = new Color(0.3f, 0.5f, 0.9f, 0.28f);

        [MenuItem("Tools/shakee's Tools/Compatibility Patcher", false, 4)]
        static void Open() => GetWindow<CompatPatcherWindow>("Compat Patcher");

        void OnEnable() { wantsMouseMove = true; BuildHeader(); ScanPatch(); }
        void OnFocus() { ScanPatch(); Repaint(); }

        // Always know which elements already live in Assets/Databases/Patch/ (any layout).
        void ScanPatch()
        {
            _patchNames.Clear();
            try
            {
                if (!Directory.Exists(PatchBuilder.PatchDir)) return;
                foreach (var f in Directory.EnumerateFiles(PatchBuilder.PatchDir, "*.asset", SearchOption.AllDirectories))
                    foreach (var el in ModReader.ParseElements(File.ReadAllText(f), f))
                        if (!el.IsRoot) _patchNames.Add(el.Name);
            }
            catch { /* patch folder may not exist yet */ }
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

        static string ElemKey(ElementRow r) => r.Type + "|" + r.Name;
        string FriendlyType(ElementRow r) => _typeName.TryGetValue(r.Type, out var n) ? n : r.TypeHint;

        // Resolve the real element class name from its m_Script (guid:fileID) via the script's MonoScript,
        // since the table has no live objects (unlike DatabaseBrowser's obj.GetType().Name). This turns
        // per-collection labels like "RPNDefinitionScienceENC" into the actual type "RpnDefinition".
        void ResolveTypeNames()
        {
            _typeName.Clear();
            var guids = new HashSet<string>();
            foreach (var r in _result.Rows)
            {
                int c = r.Type.IndexOf(':');
                if (c > 0) guids.Add(r.Type.Substring(0, c));
            }
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (!(o is MonoScript ms)) continue;
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ms, out var g, out long fid)) continue;
                    var cls = ms.GetClass();
                    if (cls != null) _typeName[g + ":" + fid] = cls.Name;
                }
            }
        }

        // Searchable type dropdown (same control DatabaseBrowser uses).
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
                root.AddChild(new AdvancedDropdownItem("(any type)"));
                foreach (var t in _types) root.AddChild(new AdvancedDropdownItem(t));
                return root;
            }
            protected override void ItemSelected(AdvancedDropdownItem item)
                => _onPick(item.name == "(any type)" ? "" : item.name);
        }

        // ---- header -------------------------------------------------------
        void BuildHeader()
        {
            var cols = new[]
            {
                MakeCol("Element", 240, 120), MakeCol("Type", 170, 80), MakeCol("By", 120, 60),
                MakeCol("Winner", 90, 50), MakeCol("Status", 90, 50), MakeCol("Diffs", 160, 60),
            };
            _headerState = new MultiColumnHeaderState(cols);
            _header = new MultiColumnHeader(_headerState);
            _header.ResizeToFit();
        }

        static MultiColumnHeaderState.Column MakeCol(string t, float w, float min) =>
            new MultiColumnHeaderState.Column
            {
                headerContent = new GUIContent(t), width = w, minWidth = min,
                autoResize = true, canSort = false, allowToggleVisibility = false,
                headerTextAlignment = TextAlignment.Left,
            };

        // ---- GUI ----------------------------------------------------------
        void OnGUI()
        {
            if (Event.current.type == EventType.MouseMove) Repaint();
            DrawSources();
            EditorGUILayout.Space(4);
            DrawSidecarAndActions();
            if (_result == null) { EditorGUILayout.HelpBox("Add the mods (load order top→bottom, last wins) and press Compare.", MessageType.Info); return; }
            EditorGUILayout.Space(4);
            DrawFilters();
            DrawStats();

            // The top controls above are variable-height (mod rows), so measure where they end and lay the
            // three panels out as absolute Rects from there (same approach as DatabaseBrowser). Measuring only
            // on Repaint — when GetLastRect is valid — and caching keeps it stable across the Layout pass.
            if (Event.current.type == EventType.Repaint)
                _panelTop = GUILayoutUtility.GetLastRect().yMax + 2f;
            float top = _panelTop > 1f ? _panelTop : 140f;

            // Three independent fixed containers — validation, the element list, and the per-element detail —
            // each drawn into its own Rect, so a selection changing the (variable-height) detail never reflows
            // the list: content changes stay contained inside each panel's scroll.
            Rect rest = new Rect(0, top, position.width, Mathf.Max(0f, position.height - top));
            LayoutThreePanels(rest, out Rect vRect, out Rect tRect, out Rect dRect);
            DrawValidation(vRect);
            DrawTable(tRect);
            DrawDetail(dRect);
        }

        // Split the remaining window area into validation (top), list (middle, gets the slack), detail (bottom).
        static void LayoutThreePanels(Rect rest, out Rect v, out Rect t, out Rect d)
        {
            const float gap = 4f;
            float h = rest.height;
            float vH = Mathf.Clamp(h * 0.22f, 48f, 170f);
            float dH = Mathf.Clamp(h * 0.34f, 120f, 340f);
            float tH = h - vH - dH - gap * 2f;
            if (tH < 120f) { dH = Mathf.Max(90f, dH - (120f - tH)); tH = h - vH - dH - gap * 2f; } // give the list a floor
            if (tH < 40f) { tH = Mathf.Max(40f, h - vH - gap); dH = Mathf.Max(0f, h - vH - tH - gap * 2f); }
            v = new Rect(rest.x, rest.y, rest.width, vH);
            t = new Rect(rest.x, v.yMax + gap, rest.width, tH);
            d = new Rect(rest.x, t.yMax + gap, rest.width, dH);
        }

        void DrawSources()
        {
            EditorGUILayout.LabelField("Mods — load order (top → bottom, last wins)", EditorStyles.boldLabel);
            int remove = -1, moveUp = -1, moveDown = -1;
            for (int i = 0; i < _sources.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _sources[i].name = EditorGUILayout.TextField(_sources[i].name, GUILayout.Width(140));
                EditorGUILayout.LabelField(_sources[i].path, EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(i == 0)) if (GUILayout.Button("▲", GUILayout.Width(24))) moveUp = i;
                using (new EditorGUI.DisabledScope(i == _sources.Count - 1)) if (GUILayout.Button("▼", GUILayout.Width(24))) moveDown = i;
                if (GUILayout.Button("✕", GUILayout.Width(24))) remove = i;
                EditorGUILayout.EndHorizontal();
            }
            if (remove >= 0) _sources.RemoveAt(remove);
            if (moveUp > 0) { var t = _sources[moveUp]; _sources[moveUp] = _sources[moveUp - 1]; _sources[moveUp - 1] = t; }
            if (moveDown >= 0 && moveDown < _sources.Count - 1) { var t = _sources[moveDown]; _sources[moveDown] = _sources[moveDown + 1]; _sources[moveDown + 1] = t; }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add .unitypackage / .zip / .assetbundle"))
            {
                var p = EditorUtility.OpenFilePanelWithFilters("Add mod package", "", new[] { "Mod package", "unitypackage,zip,assetbundle" });
                if (!string.IsNullOrEmpty(p)) AddSource(p);
            }
            if (GUILayout.Button("+ Add folder"))
            {
                var p = EditorUtility.OpenFolderPanel("Add mod folder", "", "");
                if (!string.IsNullOrEmpty(p)) AddSource(p);
            }
            EditorGUILayout.EndHorizontal();
        }

        void AddSource(string path) =>
            _sources.Add(new SourceEntry { path = path, name = Path.GetFileNameWithoutExtension(path.TrimEnd('/', '\\')) });

        void DrawSidecarAndActions()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Prior sidecar (optional)", GUILayout.Width(150));
            _sidecarPath = EditorGUILayout.TextField(_sidecarPath);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                var p = EditorUtility.OpenFilePanel("Prior sidecar", PatchBuilder.PatchDir, "json");
                if (!string.IsNullOrEmpty(p)) _sidecarPath = p;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_sources.Count < 2))
                if (GUILayout.Button("Compare", GUILayout.Height(26))) Compare();
            using (new EditorGUI.DisabledScope(_result == null))
            {
                if (GUILayout.Button("Import all conflicts (chosen)", GUILayout.Height(26), GUILayout.Width(210))) MassImport();
                if (GUILayout.Button("Export sidecar", GUILayout.Height(26), GUILayout.Width(130))) Export();
            }
            EditorGUILayout.EndHorizontal();
        }

        // ---- load-order validation panel (container 1) -------------------
        void DrawValidation(Rect rect)
        {
            GUILayout.BeginArea(rect);
            _validationScroll = EditorGUILayout.BeginScrollView(_validationScroll);

            // Fallback/availability note (e.g. vanilla bundle not mounted) — show regardless of findings.
            if (!string.IsNullOrEmpty(_validationNote))
                EditorGUILayout.HelpBox(_validationNote, MessageType.Warning);

            int errors = _findings.Count(f => f.Severity == FindingSeverity.Error);
            int warns = _findings.Count - errors;

            // Order-caused = hazards whose presence or resolution differs under the reversed order. Those are
            // the ones the user can influence by reordering; the rest only a patch edit can fix.
            var altByKey = _altFindings.GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.First());
            var goneInReverse = _findings.Where(f => !altByKey.ContainsKey(f.Key)).ToList();
            var detailChangedInReverse = _findings.Where(f => altByKey.TryGetValue(f.Key, out var a) && a.Detail != f.Detail).ToList();
            var introducedInReverse = _altFindings.Where(f => !_findings.Any(x => x.Key == f.Key)).ToList();
            var orderSensitiveKeys = new HashSet<string>(
                goneInReverse.Select(f => f.Key)
                .Concat(detailChangedInReverse.Select(f => f.Key))
                .Concat(introducedInReverse.Select(f => f.Key)));

            if (_findings.Count == 0 && introducedInReverse.Count == 0)
            {
                EditorGUILayout.HelpBox("Load-order validation: no known load-time hazards detected in this order.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.LabelField(
                    $"Load-order validation — {errors} error(s), {warns} warning(s)  ·  order-sensitive: {orderSensitiveKeys.Count}",
                    EditorStyles.boldLabel);

                foreach (var f in _findings.OrderBy(f => f.Severity).ThenBy(f => f.Element, StringComparer.OrdinalIgnoreCase))
                {
                    bool orderSensitive = orderSensitiveKeys.Contains(f.Key);
                    EditorGUILayout.HelpBox(f.Line + (orderSensitive ? "  [order-sensitive]" : ""),
                        f.Severity == FindingSeverity.Error ? MessageType.Error : MessageType.Warning);
                }

                // If reordering would change the picture, tell the user so they can act on it (▲▼ then Compare).
                if (orderSensitiveKeys.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("Load order matters here. Under the reverse order (").Append(_altOrderName).Append(") ");
                    if (goneInReverse.Count > 0) sb.Append(goneInReverse.Count).Append(" of these would not occur; ");
                    if (detailChangedInReverse.Count > 0) sb.Append(detailChangedInReverse.Count).Append(" resolve differently; ");
                    if (introducedInReverse.Count > 0) sb.Append(introducedInReverse.Count).Append(" new hazards would appear; ");
                    sb.Append("Reorder with ▲▼ above and press Compare again to re-validate.");
                    EditorGUILayout.HelpBox(sb.ToString(), MessageType.Warning);
                }
            }

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawFilters()
        {
            _status = (StatusFilter)GUILayout.Toolbar((int)_status, STATUS_LABELS);
            EditorGUILayout.BeginHorizontal();
            _nameFilter = EditorGUILayout.TextField("Name contains", _nameFilter);
            EditorGUILayout.LabelField("Type", GUILayout.Width(32));
            if (GUILayout.Button(_typeFilter.Length == 0 ? "(any type)" : _typeFilter, EditorStyles.popup, GUILayout.Width(240)))
            {
                var rect = GUILayoutUtility.GetLastRect();
                new TypeDropdown(_typeDdState, _types, picked => { _typeFilter = picked; Repaint(); }).Show(rect);
            }
            _needsReviewOnly = GUILayout.Toggle(_needsReviewOnly, "needs review only", GUILayout.Width(140));
            _hideWinnerOnly = GUILayout.Toggle(_hideWinnerOnly, "hide winner-only", GUILayout.Width(140));
            EditorGUILayout.EndHorizontal();
            ApplyFilter();
        }

        void DrawStats()
        {
            var s = _result.Stats;
            int need = _elemStatus.Values.Count(v => v == "new" || v == "changed");
            EditorGUILayout.LabelField(
                $"conflicts {s.Conflicts} (needs review {need}, odin {s.OdinConflicts}) · new {s.New} · identical {s.Identical} · roots {s.Roots} · showing {_view.Count}",
                EditorStyles.miniLabel);
        }

        void ApplyFilter()
        {
            string nm = _nameFilter.Trim().ToLowerInvariant();
            _view = _result.Rows.Where(r =>
                (_status == StatusFilter.All ||
                 (_status == StatusFilter.Conflicts && r.Status == ElemStatus.Conflict) ||
                 (_status == StatusFilter.New && r.Status == ElemStatus.New) ||
                 (_status == StatusFilter.Identical && r.Status == ElemStatus.Identical)) &&
                (nm.Length == 0 || (r.Name ?? "").ToLowerInvariant().Contains(nm)) &&
                (_typeFilter.Length == 0 || FriendlyType(r) == _typeFilter) &&
                (!_needsReviewOnly || NeedsReview(r)) &&
                (!_hideWinnerOnly || !IsWinnerOnlyConflict(r))
            ).ToList();
        }

        static bool IsWinnerOnlyConflict(ElementRow r)
        {
            if (r.Status != ElemStatus.Conflict || r.Conflict == null) return false;
            return r.Conflict.Diffs.Count > 0 && r.Conflict.Diffs.All(d => d.Kind == DiffKind.ExtraInWinner);
        }

        bool NeedsReview(ElementRow r)
        {
            if (r.Status != ElemStatus.Conflict) return false;
            return _elemStatus.TryGetValue(ElemKey(r), out var s) && (s == "new" || s == "changed");
        }

        string GetCell(ElementRow r, Col c)
        {
            switch (c)
            {
                case Col.Element: return r.Name;
                case Col.Type: return FriendlyType(r);
                case Col.By: return string.Join("/", r.Contributors);
                case Col.Winner: return r.Status == ElemStatus.Conflict && _choice.TryGetValue(ElemKey(r), out var ch) ? ch : r.Winner;
                case Col.Status:
                    string s = r.Status == ElemStatus.Conflict && _elemStatus.TryGetValue(ElemKey(r), out var st) ? st : r.Status.ToString();
                    if (_patchNames.Contains(r.Name)) s += "  ✓in patch";
                    return s;
                case Col.Diffs: return r.Summary;
                default: return "";
            }
        }

        // ---- element list (container 2) ----------------------------------
        void DrawTable(Rect rect)
        {
            if (_header == null) BuildHeader();
            GUILayout.BeginArea(rect);
            float totalW = _headerState.widthOfAllVisibleColumns;
            var visible = _headerState.visibleColumns;
            Rect headerRect = GUILayoutUtility.GetRect(10, 100000, _header.height, _header.height);
            _header.OnGUI(headerRect, _tableScroll.x);

            float viewH = Mathf.Max(0f, rect.height - _header.height - 2f); // fill the panel below its header
            Rect bodyArea = GUILayoutUtility.GetRect(10, 100000, viewH, viewH, GUILayout.ExpandWidth(true));
            Rect content = new Rect(0, 0, totalW, _view.Count * ROW_H);
            _tableScroll = GUI.BeginScrollView(bodyArea, _tableScroll, content);

            Vector2 mouse = Event.current.mousePosition;
            int first = Mathf.Max(0, Mathf.FloorToInt(_tableScroll.y / ROW_H));
            int last = Mathf.Min(_view.Count, Mathf.CeilToInt((_tableScroll.y + bodyArea.height) / ROW_H) + 1);
            for (int i = first; i < last; i++)
            {
                var r = _view[i];
                Rect rr = new Rect(0, i * ROW_H, totalW, ROW_H);
                if (r == _selected) EditorGUI.DrawRect(rr, ROW_SEL);
                else if ((i & 1) == 1) EditorGUI.DrawRect(rr, ROW_ALT);
                if (Event.current.type == EventType.MouseDown && rr.Contains(mouse)) { _selected = r; Repaint(); }
                for (int vc = 0; vc < visible.Length; vc++)
                {
                    Rect cell = _header.GetCellRect(vc, rr);
                    string text = GetCell(r, (Col)visible[vc]);
                    GUI.Label(cell, new GUIContent(text ?? "", text ?? ""), EditorStyles.miniLabel);
                }
            }
            GUI.EndScrollView();
            GUILayout.EndArea();
        }

        // ---- per-element detail (container 3) ----------------------------
        // Everything lives inside one scroll view bounded to the panel Rect, so the (variable-height) content
        // never reflows the list above — selecting a different element only changes what scrolls here.
        void DrawDetail(Rect rect)
        {
            GUILayout.BeginArea(rect);
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);

            if (_selected == null)
            {
                EditorGUILayout.HelpBox("Select an element from the list above.", MessageType.None);
            }
            else
            {
                var row = _selected;
                string key = ElemKey(row);
                EditorGUILayout.LabelField($"{row.Name}   ·   {row.TypeHint}   ·   {string.Join("/", row.Contributors)}", EditorStyles.boldLabel);

                if (row.Conflict == null)
                {
                    bool inPatch = _patchNames.Contains(row.Name);
                    EditorGUILayout.HelpBox(row.Status == ElemStatus.New
                        ? "New element (single mod). Import & Edit to bring it into the patch and adjust."
                        : row.Status == ElemStatus.Root ? "Collection/container object — not a gameplay element."
                        : "Identical across mods — no action needed.", MessageType.None);
                    using (new EditorGUI.DisabledScope(row.Status == ElemStatus.Root || inPatch))
                    {
                        EditorGUILayout.BeginHorizontal();
                        if (GUILayout.Button("Import & Edit into Patch/", GUILayout.Width(200))) ImportChosen(row, row.Winner);
                        if (GUILayout.Button("Compare side-by-side", GUILayout.Width(180))) OpenCompare(row);
                        EditorGUILayout.EndHorizontal();
                    }
                    if (inPatch)
                        EditorGUILayout.LabelField("✓ In patch — Compare side-by-side to edit the patch version.", EditorStyles.miniLabel);
                }
                else
                {
                    bool inPatch = _patchNames.Contains(row.Name);
                    string statusTag = _elemStatus.TryGetValue(key, out var st) ? "   [" + st + "]" : "";
                    EditorGUILayout.LabelField("Which mod's version wins?" + statusTag, EditorStyles.miniBoldLabel);
                    string chosen = _choice.TryGetValue(key, out var ch) ? ch : row.Winner;
                    foreach (var mod in row.Contributors)
                    {
                        EditorGUILayout.BeginHorizontal();
                        bool isChosen = chosen == mod;
                        if (GUILayout.Toggle(isChosen, "", GUILayout.Width(18)) && !isChosen) _choice[key] = mod;
                        EditorGUILayout.LabelField(mod + (mod == row.Winner ? "  (load-order winner)" : ""), GUILayout.Width(220));
                        EditorGUILayout.EndHorizontal();
                    }

                    using (new EditorGUI.DisabledScope(inPatch))
                    {
                        EditorGUILayout.BeginHorizontal();
                        if (GUILayout.Button("Import chosen into Patch/", GUILayout.Width(200)))
                            ImportChosen(row, _choice.TryGetValue(key, out var c2) ? c2 : row.Winner);
                        if (GUILayout.Button("Compare side-by-side", GUILayout.Width(180))) OpenCompare(row);
                        EditorGUILayout.EndHorizontal();
                    }
                    if (inPatch)
                        EditorGUILayout.LabelField("✓ In patch — Compare side-by-side to edit the patch version directly.", EditorStyles.miniLabel);

                    EditorGUILayout.Space(2);
                    string diffWinner = row.Winner;
                    bool diffOdin = row.Conflict.Odin;
                    List<Diff> diffList = row.Conflict.Diffs;
                    var patchEls = FindPatchHkElements(row.Name);
                    if (patchEls.Count > 0)
                    {
                        var patchEl = patchEls.FirstOrDefault(e => e.TypeHint == row.TypeHint) ?? patchEls[0];
                        diffList = ConflictAnalyzer.ComputeDiffs(patchEl, row.Elements.ToDictionary(kv => kv.Key, kv => kv.Value));
                        diffWinner = "Patch";
                        diffOdin = patchEl.Odin;
                    }
                    DiffGui.DrawTable(diffList, diffWinner, diffOdin);
                }
            }

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // One monotonically-increasing 0..1 bar across the whole Compare, so the user sees steady
        // progress instead of the bar snapping to 0.5f for validate / jumping near 1.0 after the mod
        // read. Phases are weighted roughly by their typical cost on a multi-mod project.
        //   0.00 .. 0.50  reading each mod in order
        //   0.50 .. 0.70  conflict analysis (per-element sub-progress)
        //   0.70 .. 0.75  resolving type names
        //   0.75 .. 0.80  scanning the patch directory
        //   0.80 .. 1.00  load-order validation (per-mod sub-progress)
        // The `sub` argument is each phase's own 0..1; the helper maps it into its slot and repaints.
        void ShowCompareProgress(double start, double end, string title, double sub, string detail)
        {
            double f = start + (end - start) * Math.Max(0, Math.Min(1, sub));
            EditorUtility.DisplayProgressBar(title, detail, (float)f);
        }

        // ---- actions ------------------------------------------------------
        void Compare()
        {
            try
            {
                _mods = new List<HkMod>();
                for (int i = 0; i < _sources.Count; i++)
                {
                    ShowCompareProgress(0.0, 0.5, "Compat Patcher",
                        (double)i / Math.Max(1, _sources.Count),
                        "Reading " + _sources[i].name + " (" + (i + 1) + " / " + _sources.Count + ")");
                    _mods.Add(ModReader.Load(_sources[i].name, _sources[i].path));
                }
                _prior = Sidecar.LoadIndex(_sidecarPath);
                _result = ConflictAnalyzer.Analyse(_mods, new Dictionary<string, Sidecar.Decision>(),
                    (sub, label) => ShowCompareProgress(0.5, 0.7, "Compat Patcher", sub, label));

                _choice.Clear(); _elemStatus.Clear(); _elemFp.Clear();
                foreach (var row in _result.Rows)
                {
                    if (row.Status != ElemStatus.Conflict) continue;
                    string key = ElemKey(row);
                    string fp = ElementFingerprint(row);
                    _elemFp[key] = fp;
                    if (_prior.TryGetValue(key, out var pd) && pd.fp == fp)
                    { _elemStatus[key] = "carried"; _choice[key] = row.Contributors.Contains(pd.choice) ? pd.choice : row.Winner; }
                    else if (_prior.ContainsKey(key))
                    { _elemStatus[key] = "changed"; _choice[key] = row.Winner; }
                    else { _elemStatus[key] = "new"; _choice[key] = row.Winner; }
                }

                ShowCompareProgress(0.7, 0.75, "Compat Patcher", 0, "Resolving type names…");
                ResolveTypeNames();
                _types = _result.Rows.Select(FriendlyType).Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x).ToList();
                _typeFilter = "";
                _selected = null;
                ShowCompareProgress(0.75, 0.8, "Compat Patcher", 0, "Scanning patch directory…");
                ScanPatch();
                ShowCompareProgress(0.8, 1.0, "Compat Patcher", 0, "Validating load order (Vanilla → mods)…");
                Validate((sub, label) => ShowCompareProgress(0.8, 1.0, "Compat Patcher", sub, label));
            }
            catch (Exception e) { Debug.LogError("[CompatPatcher] Compare failed: " + e); }
            finally { EditorUtility.ClearProgressBar(); }
        }

        // Load-order validation, re-run on every Compare. The real order is Vanilla → the mods below, so the
        // validator merges the mounted vanilla databases (base) under the mod overlay and replays the
        // reset-capable load-time rules. We evaluate the current order and — only if it has findings — the
        // reversed mod order as a control, to separate order-caused hazards (would change if you reorder)
        // from intrinsic ones (present whatever the order, so only editing the patch fixes them).
        void Validate(Action<double, string> progress = null)
        {
            _findings = new List<Finding>();
            _altFindings.Clear();
            _altOrderName = "";
            _validationNote = "";

            using var validator = LoadOrderValidator.Build();
            _validationNote = validator.Note;

            _findings = validator.Evaluate(_mods, progress);

            // Full dump to the console so runs can be diffed and nothing is hidden by the panel's collapsing.
            DumpValidation("current order  Vanilla → " + string.Join(" → ", _mods.Select(m => m.Name)), _findings);

            if (_findings.Count == 0) return;

            var reversed = Enumerable.Reverse(_mods).ToList();
            _altOrderName = string.Join(" → ", new[] { "Vanilla" }.Concat(reversed.Select(m => m.Name)));
            _altFindings = validator.Evaluate(reversed);
        }

        // Console record of every finding (grouped by rule code) — persists across runs so the user can see
        // exactly what changed between two Compares, independent of the on-screen panel.
        static void DumpValidation(string orderLabel, List<Finding> findings)
        {
            int errors = findings.Count(f => f.Severity == FindingSeverity.Error);
            var sb = new StringBuilder();
            sb.AppendLine($"[CompatPatcher] Load-order validation — {orderLabel}");
            sb.AppendLine($"  {errors} error(s), {findings.Count - errors} warning(s), {findings.Count} total");
            foreach (var g in findings.GroupBy(f => f.Code).OrderBy(g => g.Key))
            {
                sb.AppendLine($"  {g.Key}  ({g.Count()}):");
                foreach (var f in g.OrderBy(f => f.Element, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine("    " + f.Line);
            }
            Debug.Log(sb.ToString());
        }

        string ElementFingerprint(ElementRow row)
        {
            var sb = new StringBuilder();
            foreach (var mod in row.Elements.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                var el = row.Elements[mod];
                sb.Append(mod).Append('=');
                if (el.Body != null)
                {
                    var f = UnityYaml.Flatten(el.Body);
                    foreach (var k in f.Keys.OrderBy(x => x, StringComparer.Ordinal))
                        sb.Append(k).Append(':').Append(f[k]).Append(';');
                }
                else foreach (var r in el.Refs.OrderBy(x => x, StringComparer.Ordinal)) sb.Append(r).Append(',');
                sb.Append('|');
            }
            using var sha = SHA1.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return BitConverter.ToString(h).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }

        void OpenCompare(ElementRow row)
        {
            // hand the compare window the whole current view so it can switch elements from a list
            var items = new List<CompatCompareWindow.CompareItem>();
            int idx = 0;
            foreach (var r in _view)
            {
                var versions = r.Contributors
                    .Select(mn => (mn, _mods.FirstOrDefault(m => m.Name == mn), r.Elements.TryGetValue(mn, out var el) ? el : null))
                    .Where(v => v.Item2 != null && v.Item3 != null).ToList();
                if (versions.Count == 0) continue;
                if (r == row) idx = items.Count;
                items.Add(new CompatCompareWindow.CompareItem
                {
                    name = r.Name,
                    typeHint = r.TypeHint,
                    versions = versions,
                    winner = r.Winner,
                    odin = r.Conflict?.Odin ?? false,
                    diffs = r.Conflict?.Diffs ?? new List<Diff>(),
                    inPatch = _patchNames.Contains(r.Name)
                });
            }
            CompatCompareWindow.Show(items, idx);
        }

        // an element's mappers (UIMapper, DescriptorMapper, …) live under the same name, different type
        static IEnumerable<HkElement> MappersFor(HkMod mod, HkElement el) =>
            mod.Elements.Values.Where(e => e.Name == el.Name && e.Type != el.Type && !e.IsRoot);

        void ImportChosen(ElementRow row, string sourceModName)
        {
            var mod = _mods?.FirstOrDefault(m => m.Name == sourceModName);
            if (mod == null || row.Elements == null || !row.Elements.TryGetValue(sourceModName, out var el) || el == null)
            { Debug.LogWarning($"[CompatPatcher] '{row.Name}' has no loaded version from mod '{sourceModName}' (re-run Compare?)."); return; }

            var set = new Dictionary<string, (HkMod mod, HkElement el)>();
            set[el.Key] = (mod, el);
            foreach (var m in MappersFor(mod, el)) if (!set.ContainsKey(m.Key)) set[m.Key] = (mod, m);
            PatchBuilder.ImportElements(set.Values);
            ScanPatch();
            SelectInPatch(el.TypeHint, el.Name);
        }

        void MassImport()
        {
            var conflicts = _result.Rows.Where(r => r.Status == ElemStatus.Conflict && !_patchNames.Contains(r.Name)).ToList();
            if (!EditorUtility.DisplayDialog("Import all conflicts",
                $"Import the chosen version of {conflicts.Count} conflict elements (plus their mappers) into {PatchBuilder.PatchDir}?",
                "Import", "Cancel")) return;

            // pass 1: each conflict's own chosen element (a mapper that is itself a conflict keeps its own choice)
            var chosen = new Dictionary<string, (HkMod mod, HkElement el)>();
            foreach (var row in conflicts)
            {
                string c = _choice.TryGetValue(ElemKey(row), out var v) ? v : row.Winner;
                var mod = _mods.FirstOrDefault(m => m.Name == c);
                if (mod != null && row.Elements.TryGetValue(c, out var el) && el != null) chosen[el.Key] = (mod, el);
            }
            // pass 2: fill in mappers that aren't their own conflict row (identical / single-mod), from the same mod
            foreach (var kv in chosen.Values.ToList())
                foreach (var m in MappersFor(kv.mod, kv.el))
                    if (!chosen.ContainsKey(m.Key)) chosen[m.Key] = (kv.mod, m);
            int n = PatchBuilder.ImportElements(chosen.Values);
            ScanPatch();
            Debug.Log($"[CompatPatcher] Mass import: {n} elements (incl. mappers) → {PatchBuilder.PatchDir}.");
            EditorUtility.DisplayDialog("Compat Patcher", $"Imported {n} elements (chosen versions + mappers) into the patch.", "OK");
        }

        void SelectInPatch(string typeHint, string name)
        {
            string path = PatchBuilder.PatchDir + "/" + typeHint + ".asset";
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && o.name == name) { Selection.activeObject = o; EditorGUIUtility.PingObject(o); return; }
        }

        void Export()
        {
            string sidecar = string.IsNullOrEmpty(_sidecarPath)
                ? PatchBuilder.PatchDir + "/CompatPatch.sidecar.json" : _sidecarPath;
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar));

            var decisions = new List<Sidecar.Decision>();
            foreach (var row in _result.Rows)
            {
                if (row.Status != ElemStatus.Conflict) continue;
                string key = ElemKey(row);
                decisions.Add(new Sidecar.Decision
                {
                    sig = key, fp = _elemFp.TryGetValue(key, out var fp) ? fp : "",
                    choice = _choice.TryGetValue(key, out var c) ? c : row.Winner,
                    kind = "element", element = row.Name
                });
            }
            Sidecar.Save(sidecar, _result.LoadOrder, decisions);
            _sidecarPath = sidecar;
            AssetDatabase.Refresh();
            Debug.Log($"[CompatPatcher] Sidecar written: {sidecar}  ({decisions.Count} element decisions).");
            EditorUtility.DisplayDialog("Compat Patcher",
                $"Sidecar written to:\n{sidecar}\n\n{decisions.Count} per-element decisions recorded.\n\n" +
                "Use “Import chosen into Patch/” per element to build the patch (Assets/Databases/Patch/).", "OK");
        }
    }
}
