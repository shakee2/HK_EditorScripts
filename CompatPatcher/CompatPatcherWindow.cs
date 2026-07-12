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

        StatusFilter _status = StatusFilter.Conflicts;
        string _nameFilter = "";
        List<string> _types = new List<string>();
        string _typeFilter = "";
        AdvancedDropdownState _typeDdState = new AdvancedDropdownState();
        readonly Dictionary<string, string> _typeName = new Dictionary<string, string>(); // "guid:fileID" -> class name
        bool _needsReviewOnly;

        MultiColumnHeader _header;
        MultiColumnHeaderState _headerState;
        Vector2 _tableScroll, _detailScroll;
        ElementRow _selected;
        const float ROW_H = 18f;
        static readonly Color ROW_ALT = new Color(1f, 1f, 1f, 0.03f);
        static readonly Color ROW_SEL = new Color(0.3f, 0.5f, 0.9f, 0.28f);

        [MenuItem("Tools/Compatibility Patcher", false, 4)]
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
            DrawTable();
            DrawDetail();
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
            if (GUILayout.Button("+ Add .unitypackage / .zip"))
            {
                var p = EditorUtility.OpenFilePanelWithFilters("Add mod package", "", new[] { "Mod package", "unitypackage,zip" });
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
                (!_needsReviewOnly || NeedsReview(r))
            ).ToList();
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

        void DrawTable()
        {
            if (_header == null) BuildHeader();
            float totalW = _headerState.widthOfAllVisibleColumns;
            var visible = _headerState.visibleColumns;
            Rect headerRect = GUILayoutUtility.GetRect(10, 100000, _header.height, _header.height);
            _header.OnGUI(headerRect, _tableScroll.x);

            float viewH = Mathf.Max(140, position.height * 0.40f);
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
        }

        // ---- detail: per-element winner + read-only diffs -----------------
        void DrawDetail()
        {
            EditorGUILayout.Space(2);
            if (_selected == null) { EditorGUILayout.HelpBox("Select an element above.", MessageType.None); return; }
            var row = _selected;
            string key = ElemKey(row);
            EditorGUILayout.LabelField($"{row.Name}   ·   {row.TypeHint}   ·   {string.Join("/", row.Contributors)}", EditorStyles.boldLabel);

            if (row.Conflict == null)
            {
                EditorGUILayout.HelpBox(row.Status == ElemStatus.New
                    ? "New element (single mod). Import & Edit to bring it into the patch and adjust."
                    : row.Status == ElemStatus.Root ? "Collection/container object — not a gameplay element."
                    : "Identical across mods — no action needed.", MessageType.None);
                using (new EditorGUI.DisabledScope(row.Status == ElemStatus.Root))
                {
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Import & Edit into Patch/", GUILayout.Width(200))) ImportChosen(row, row.Winner);
                    if (GUILayout.Button("Compare side-by-side", GUILayout.Width(180))) OpenCompare(row);
                    EditorGUILayout.EndHorizontal();
                }
                if (_patchNames.Contains(row.Name))
                    EditorGUILayout.LabelField("✓ In patch — Compare side-by-side to edit the patch version.", EditorStyles.miniLabel);
                return;
            }

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

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Compare side-by-side", GUILayout.Width(180))) OpenCompare(row);
            if (GUILayout.Button("Import chosen into Patch/", GUILayout.Width(200)))
                ImportChosen(row, _choice.TryGetValue(key, out var c2) ? c2 : row.Winner);
            EditorGUILayout.EndHorizontal();
            if (_patchNames.Contains(row.Name))
                EditorGUILayout.LabelField("✓ In patch — Compare side-by-side to edit the patch version directly.", EditorStyles.miniLabel);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(row.Conflict.Odin
                ? "Differences (read-only) — Odin element: references only, use Compare side-by-side for full fields:"
                : "Differences (read-only):", EditorStyles.miniBoldLabel);
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll, GUILayout.ExpandHeight(true));
            foreach (var d in row.Conflict.Diffs)
            {
                if (d.Kind == DiffKind.ExtraInWinner)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.LabelField($"ONLY in winner ({row.Winner})   {d.Path}", EditorStyles.miniBoldLabel);
                    if (d.Values.TryGetValue("*winner*", out var wv) && wv != UnityYaml.Missing)
                        EditorGUILayout.LabelField("    " + Short(wv), EditorStyles.miniLabel);
                    EditorGUILayout.EndVertical();
                    continue;
                }
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                string kindTag = d.Kind == DiffKind.MissingInWinner ? "MISSING in winner" : "CHANGED";
                EditorGUILayout.LabelField($"{kindTag}   {d.Path}", EditorStyles.miniBoldLabel);
                foreach (var kv in d.Values.OrderBy(k => k.Key == "*winner*" ? "" : k.Key))
                {
                    string who = kv.Key == "*winner*" ? row.Winner + " (winner)" : kv.Key;
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(who, GUILayout.Width(150));
                    EditorGUILayout.LabelField(Short(kv.Value), EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        static string Short(string v) => v == UnityYaml.Missing ? "(absent)" : (v != null && v.Length > 140 ? v.Substring(0, 138) + "…" : v);

        // ---- actions ------------------------------------------------------
        void Compare()
        {
            try
            {
                _mods = new List<HkMod>();
                for (int i = 0; i < _sources.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("Compat Patcher", "Reading " + _sources[i].name, (float)i / _sources.Count);
                    _mods.Add(ModReader.Load(_sources[i].name, _sources[i].path));
                }
                _prior = Sidecar.LoadIndex(_sidecarPath);
                _result = ConflictAnalyzer.Analyse(_mods, new Dictionary<string, Sidecar.Decision>());

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

                ResolveTypeNames();
                _types = _result.Rows.Select(FriendlyType).Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x).ToList();
                _typeFilter = "";
                _selected = null;
                ScanPatch();
            }
            catch (Exception e) { Debug.LogError("[CompatPatcher] Compare failed: " + e); }
            finally { EditorUtility.ClearProgressBar(); }
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
                items.Add(new CompatCompareWindow.CompareItem { name = r.Name, typeHint = r.TypeHint, versions = versions });
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
            var conflicts = _result.Rows.Where(r => r.Status == ElemStatus.Conflict).ToList();
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
