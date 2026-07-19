using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HK.ModTools.Shared;
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

        enum PatchOrphanKind
        {
            GoneFromMods,       // in Patch, in 0 compared mods — true orphan
            SoleModLeft,        // in Patch, only 1 mod still defines it — conflict dissolved
            ModsAgree,          // in Patch, ≥2 mods still define it but Identical — patch overrides for nothing
        }

        class PatchEntry
        {
            public string Name;
            public string Type;       // m_Script guid:fileID
            public string TypeHint;   // collection stem / file stem
            public string AssetPath;  // Assets/Databases/Patch/….asset
        }

        class PatchOrphan
        {
            public PatchOrphanKind Kind;
            public PatchEntry Entry;
            public string Detail;     // e.g. remaining mod name
        }

        [SerializeField] List<SourceEntry> _sources = new List<SourceEntry>();
        [SerializeField] string _sidecarPath = "";
        [SerializeField] bool _showMountedBundles;
        [SerializeField] bool _showPatchOrphans = true;

        List<HkMod> _mods;
        AnalyzeResult _result;
        UnlockCarrierIndex _unlockIndex;
        Dictionary<string, Sidecar.Decision> _prior = new Dictionary<string, Sidecar.Decision>();
        readonly Dictionary<string, string> _choice = new Dictionary<string, string>();     // elemKey -> chosen mod
        readonly Dictionary<string, string> _elemStatus = new Dictionary<string, string>(); // elemKey -> new|changed|resolved
        readonly Dictionary<string, string> _elemFp = new Dictionary<string, string>();     // elemKey -> fingerprint
        readonly HashSet<string> _patchNames = new HashSet<string>();                        // element names already in Assets/Databases/Patch/
        readonly List<PatchEntry> _patchEntries = new List<PatchEntry>();
        readonly List<PatchOrphan> _patchOrphans = new List<PatchOrphan>();
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
        bool _showResolvedDiffs; // carried per-diff Resolve rows in detail panel

        MultiColumnHeader _header;
        MultiColumnHeaderState _headerState;
        Vector2 _tableScroll, _detailScroll;
        ElementRow _selected;
        readonly HashSet<string> _multiSelect = new HashSet<string>(); // ElemKey multi-select for Mass Change
        bool _viewDirty = true;
        // Cached Patch-vs-mods diffs for the selected conflict (FindPatchHkElements+ComputeDiffs is disk-heavy).
        ElementRow _detailDiffRow;
        List<Diff> _detailDiffList;
        string _detailDiffWinner;
        bool _detailDiffOdin;
        const float ROW_H = 18f;
        static readonly Color ROW_ALT = new Color(1f, 1f, 1f, 0.03f);
        static readonly Color ROW_SEL = new Color(0.3f, 0.5f, 0.9f, 0.28f);

        [MenuItem("Tools/shakee's Tools/Compatibility Patcher", false, 4)]
        static void Open() => GetWindow<CompatPatcherWindow>("Compat Patcher");

        void OnEnable() { wantsMouseMove = false; BuildHeader(); ScanPatch(); }
        void OnFocus() { ScanPatch(); _viewDirty = true; InvalidateDetailDiffs(); Repaint(); }

        // Always know which elements already live in Assets/Databases/Patch/ (any layout).
        void ScanPatch()
        {
            _patchNames.Clear();
            _patchEntries.Clear();
            InvalidateDetailDiffs();
            try
            {
                if (!Directory.Exists(PatchBuilder.PatchDir)) return;
                foreach (var f in Directory.EnumerateFiles(PatchBuilder.PatchDir, "*.asset", SearchOption.AllDirectories))
                {
                    string norm = f.Replace('\\', '/');
                    string text;
                    try { text = File.ReadAllText(f); }
                    catch { continue; }
                    foreach (var el in ModReader.ParseElements(text, norm))
                    {
                        if (el.IsRoot || string.IsNullOrEmpty(el.Name)) continue;
                        _patchNames.Add(el.Name);
                        _patchEntries.Add(new PatchEntry
                        {
                            Name = el.Name,
                            Type = el.Type,
                            TypeHint = el.TypeHint,
                            AssetPath = norm,
                        });
                    }
                }
            }
            catch { /* patch folder may not exist yet */ }
        }

        void InvalidateDetailDiffs()
        {
            _detailDiffRow = null;
            _detailDiffList = null;
        }

        /// <summary>
        /// Patch entries that no longer need to override the compared mods: gone from all mods,
        /// only one mod left, or mods now Identical. Still-Conflict rows are not orphans.
        /// Matched by element <see cref="PatchEntry.Name"/> (same as ✓in patch), not script Type key
        /// (YAML vs live MonoScript keys can differ).
        /// </summary>
        void ComputePatchOrphans()
        {
            _patchOrphans.Clear();
            if (_mods == null || _result == null || _patchEntries.Count == 0) return;

            var nameToMods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var mod in _mods)
                foreach (var el in mod.Elements.Values)
                {
                    if (el.IsRoot || string.IsNullOrEmpty(el.Name)) continue;
                    if (!nameToMods.TryGetValue(el.Name, out var set))
                        nameToMods[el.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(mod.Name);
                }

            var rowsByName = _result.Rows
                .Where(r => r.Status != ElemStatus.Root && !string.IsNullOrEmpty(r.Name))
                .GroupBy(r => r.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            foreach (var pe in _patchEntries)
            {
                nameToMods.TryGetValue(pe.Name, out var mods);
                int n = mods?.Count ?? 0;
                if (n == 0)
                {
                    _patchOrphans.Add(new PatchOrphan
                    {
                        Kind = PatchOrphanKind.GoneFromMods,
                        Entry = pe,
                        Detail = "not defined by any compared mod — patch still forces it",
                    });
                    continue;
                }

                rowsByName.TryGetValue(pe.Name, out var rows);
                bool stillConflict = rows != null && rows.Any(r => r.Status == ElemStatus.Conflict);
                if (stillConflict) continue;

                if (n == 1)
                {
                    string sole = mods.First();
                    _patchOrphans.Add(new PatchOrphan
                    {
                        Kind = PatchOrphanKind.SoleModLeft,
                        Entry = pe,
                        Detail = $"only '{sole}' still defines it — conflict dissolved; patch still overrides",
                    });
                    continue;
                }

                bool allIdentical = rows != null && rows.Count > 0
                    && rows.All(r => r.Status == ElemStatus.Identical);
                if (allIdentical)
                {
                    _patchOrphans.Add(new PatchOrphan
                    {
                        Kind = PatchOrphanKind.ModsAgree,
                        Entry = pe,
                        Detail = "mods now Identical — patch still overrides for no conflict",
                    });
                }
            }

            DumpPatchOrphans();
        }

        void DumpPatchOrphans()
        {
            if (_patchOrphans.Count == 0)
            {
                Debug.Log("[CompatPatcher] Patch orphans — 0 (no stale Patch/ overrides vs current mods).");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"[CompatPatcher] Patch orphans — {_patchOrphans.Count} total");
            foreach (var g in _patchOrphans.GroupBy(o => o.Kind).OrderBy(g => g.Key))
            {
                sb.AppendLine($"  {KindLabel(g.Key)}  ({g.Count()}):");
                foreach (var o in g.OrderBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"    {o.Entry.Name}  ({o.Entry.TypeHint})  {o.Detail}");
            }
            Debug.Log(sb.ToString());
        }

        static string KindLabel(PatchOrphanKind k) => k switch
        {
            PatchOrphanKind.GoneFromMods => "gone from mods",
            PatchOrphanKind.SoleModLeft => "sole mod left",
            PatchOrphanKind.ModsAgree => "mods agree (identical)",
            _ => k.ToString(),
        };

        List<HkElement> FindPatchHkElements(string elementName)
        {
            var result = new List<HkElement>();
            if (string.IsNullOrEmpty(elementName) || _patchEntries.Count == 0) return result;
            // Only parse files we already know contain this name — never rescan all of Patch/ every frame.
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pe in _patchEntries)
            {
                if (pe.Name != elementName || string.IsNullOrEmpty(pe.AssetPath)) continue;
                paths.Add(pe.AssetPath);
            }
            foreach (var path in paths)
            {
                string text;
                try { text = File.ReadAllText(path); }
                catch { continue; }
                foreach (var el in ModReader.ParseElements(text, path))
                    if (el.Name == elementName && !el.IsRoot)
                        result.Add(el);
            }
            return result;
        }

        void EnsureDetailDiffs(ElementRow row)
        {
            if (row == null || row.Conflict == null)
            {
                InvalidateDetailDiffs();
                return;
            }
            if (_detailDiffRow == row && _detailDiffList != null) return;

            _detailDiffRow = row;
            _detailDiffList = row.Conflict.Diffs;
            _detailDiffWinner = row.Winner;
            _detailDiffOdin = row.Conflict.Odin;

            var patchEls = FindPatchHkElements(row.Name);
            if (patchEls.Count == 0) return;
            var patchEl = patchEls.FirstOrDefault(e => e.TypeHint == row.TypeHint) ?? patchEls[0];
            _detailDiffList = ConflictAnalyzer.ComputeDiffs(patchEl, row.Elements.ToDictionary(kv => kv.Key, kv => kv.Value));
            ConflictAnalyzer.FinalizeDiffs(_detailDiffList, row.Type, row.Name, _prior);
            _detailDiffWinner = "Patch";
            _detailDiffOdin = patchEl.Odin;
        }

        static string ElemKey(ElementRow r) => r.Type + "|" + r.Name;

        /// <summary>
        /// Concrete element class name for filters/UI — never the collection file stem (<see cref="ElementRow.TypeHint"/>).
        /// Prefer live CLR type; else cached MonoScript/live map; else FullName tail. Empty if unknown.
        /// </summary>
        string FriendlyType(ElementRow r)
        {
            if (r == null) return "";
            if (r.Elements != null)
            {
                foreach (var el in r.Elements.Values)
                {
                    if (el?.LiveObject == null || el.IsRoot) continue;
                    if (el.LiveObject is Amplitude.Framework.IDatatableElementCollection) continue;
                    return el.LiveObject.GetType().Name;
                }
            }
            if (!string.IsNullOrEmpty(r.Type) && _typeName.TryGetValue(r.Type, out var mapped) && !string.IsNullOrEmpty(mapped))
                return mapped;
            // LiveElementBuilder / analyzer may store CLR FullName when MonoScript lookup fails
            if (!string.IsNullOrEmpty(r.Type) && r.Type.IndexOf(':') < 0)
            {
                int dot = r.Type.LastIndexOf('.');
                if (dot >= 0 && dot < r.Type.Length - 1)
                    return r.Type.Substring(dot + 1);
                // Bare name only if it isn't the collection stem
                if (!string.Equals(r.Type, r.TypeHint, StringComparison.Ordinal))
                    return r.Type;
            }
            return "";
        }

        // Resolve concrete class names into _typeName (guid:fileID → e.g. TechnologyDefinition).
        // Live objects first (authoritative for assetbundles); MonoScript path for YAML-only rows.
        void ResolveTypeNames()
        {
            _typeName.Clear();
            if (_result?.Rows == null) return;

            foreach (var r in _result.Rows)
            {
                if (r.Elements == null) continue;
                foreach (var el in r.Elements.Values)
                {
                    if (el?.LiveObject == null || el.IsRoot || string.IsNullOrEmpty(el.Type)) continue;
                    if (el.LiveObject is Amplitude.Framework.IDatatableElementCollection) continue;
                    _typeName[el.Type] = el.LiveObject.GetType().Name;
                }
            }

            var guids = new HashSet<string>();
            foreach (var r in _result.Rows)
            {
                if (string.IsNullOrEmpty(r.Type) || _typeName.ContainsKey(r.Type)) continue;
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
                _types = types ?? new List<string>();
                _onPick = onPick;
                // minSize floor only — height cap + anchor is AdvancedDropdownHeight.ShowCapped.
                int rows = Math.Min(_types.Count + 1, 22);
                float h = 44f + rows * 18f;
                minimumSize = new Vector2(240, Mathf.Clamp(h, 100f, AdvancedDropdownHeight.DefaultMaxHeight));
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
            if (WindowMinimize.DrawMinimizedChrome(this, typeof(CompatCompareWindow))) return;

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.FlexibleSpace();
            WindowMinimize.DrawToolbarButton(this, typeof(CompatCompareWindow));
            EditorGUILayout.EndHorizontal();

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

            DrawMountedBundles();
        }

        void DrawMountedBundles()
        {
            EditorGUILayout.Space(4);
            _showMountedBundles = EditorGUILayout.Foldout(_showMountedBundles, "Loaded mod bundles (Compat Patcher mounts)", true);
            if (!_showMountedBundles) return;

            var entries = CompatBundleMounts.ListMountedCompatProviders();
            if (entries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No mod assetbundles mounted by Compat Patcher yet. Compare with .assetbundle sources mounts them for the session. Game FX/UI/data bundles are not listed here.",
                    MessageType.None);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"{entries.Count} mounted", EditorStyles.miniLabel);
            if (GUILayout.Button("Unload all", GUILayout.Width(90)))
            {
                CompatBundleMounts.UnloadAllOurs();
                Repaint();
            }
            EditorGUILayout.EndHorizontal();

            foreach (var e in entries)
            {
                EditorGUILayout.BeginHorizontal();
                string status = e.Stale ? "stale" : "mounted";
                EditorGUILayout.LabelField($"{e.Label}  [{status}]", GUILayout.Width(180));
                EditorGUILayout.LabelField(e.Path, EditorStyles.miniLabel);
                if (GUILayout.Button("Force unload", GUILayout.Width(100)))
                {
                    CompatBundleMounts.ForceUnload(e.ProviderName);
                    Repaint();
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        void AddSource(string path) =>
            _sources.Add(new SourceEntry { path = path, name = CompatBundleMounts.SuggestModName(path) });

        void DrawSidecarAndActions()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Prior sidecar (optional)", GUILayout.Width(150));
            _sidecarPath = EditorGUILayout.TextField(_sidecarPath);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                var p = EditorUtility.OpenFilePanel("Prior sidecar", PatchBuilder.PatchDir, "json");
                if (!string.IsNullOrEmpty(p))
                {
                    _sidecarPath = p;
                    ApplySidecarSources(p);
                }
            }
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_sidecarPath) || !File.Exists(_sidecarPath)))
            {
                if (GUILayout.Button("Load mods", GUILayout.Width(90)))
                    ApplySidecarSources(_sidecarPath);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_sources.Count < 2))
                if (GUILayout.Button("Compare", GUILayout.Height(26))) Compare();
            using (new EditorGUI.DisabledScope(_result == null))
            {
                if (GUILayout.Button("Resolve all as winner", GUILayout.Height(26), GUILayout.Width(160)))
                    MassResolveAsWinner();
                if (GUILayout.Button("Import all conflicts (Winner Mod)", GUILayout.Height(26), GUILayout.Width(230))) MassImport();
                if (GUILayout.Button("Mass Change…", GUILayout.Height(26), GUILayout.Width(130))) OpenMassChange();
                if (GUILayout.Button("Export sidecar", GUILayout.Height(26), GUILayout.Width(130))) Export();
            }
            EditorGUILayout.EndHorizontal();
            if (_result != null && _multiSelect.Count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    $"Mass Change selection: {_multiSelect.Count} row(s) (Ctrl/Cmd+click in list)",
                    EditorStyles.miniLabel);
                if (GUILayout.Button("Clear selection", GUILayout.Width(120)))
                {
                    _multiSelect.Clear();
                    Repaint();
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        /// <summary>
        /// Refill the Compare source list from the sidecar (name + path, load order).
        /// Legacy sidecars with only <c>loadOrder</c> names keep matching paths already in the list.
        /// </summary>
        void ApplySidecarSources(string path)
        {
            var sc = Sidecar.Load(path);
            if (sc == null)
            {
                Debug.LogWarning("[CompatPatcher] Could not load sidecar: " + path);
                return;
            }

            var priorByName = _sources
                .Where(s => !string.IsNullOrEmpty(s.name))
                .GroupBy(s => s.name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().path, StringComparer.OrdinalIgnoreCase);

            _sources.Clear();
            if (sc.sources != null && sc.sources.Count > 0)
            {
                foreach (var s in sc.sources)
                {
                    if (s == null || string.IsNullOrEmpty(s.name)) continue;
                    string p = s.path ?? "";
                    if (string.IsNullOrEmpty(p) && priorByName.TryGetValue(s.name, out var kept))
                        p = kept;
                    _sources.Add(new SourceEntry { name = s.name, path = p });
                    if (string.IsNullOrEmpty(p) || !File.Exists(p) && !Directory.Exists(p))
                        Debug.LogWarning($"[CompatPatcher] Sidecar mod '{s.name}' path missing or not found: '{p}'. Re-add the file.");
                }
            }
            else if (sc.loadOrder != null && sc.loadOrder.Count > 0)
            {
                // Legacy name-only sidecar
                foreach (var name in sc.loadOrder)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    priorByName.TryGetValue(name, out var p);
                    _sources.Add(new SourceEntry { name = name, path = p ?? "" });
                    if (string.IsNullOrEmpty(p))
                        Debug.LogWarning($"[CompatPatcher] Legacy sidecar has mod name '{name}' but no path — re-add that mod, then Export sidecar.");
                }
            }

            _sidecarPath = path;
            Debug.Log($"[CompatPatcher] Sidecar loaded: {_sources.Count} mod(s) in load order from {path}.");
            Repaint();
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
            var prevStatus = _status;
            var prevName = _nameFilter;
            var prevType = _typeFilter;
            var prevNeeds = _needsReviewOnly;
            var prevHide = _hideWinnerOnly;

            _status = (StatusFilter)GUILayout.Toolbar((int)_status, STATUS_LABELS);
            EditorGUILayout.BeginHorizontal();
            _nameFilter = EditorGUILayout.TextField("Name contains", _nameFilter);
            EditorGUILayout.LabelField("Type", GUILayout.Width(32));
            // Reserve the button rect explicitly — GetLastRect-after-Button is unreliable here because
            // OnGUI also measures _panelTop from GetLastRect after DrawStats on the same frame.
            Rect typeBtn = GUILayoutUtility.GetRect(240, EditorGUIUtility.singleLineHeight, GUILayout.Width(240));
            string typeLabel = _typeFilter.Length == 0 ? "(any type)" : _typeFilter;
            if (EditorGUI.DropdownButton(typeBtn, new GUIContent(typeLabel), FocusType.Keyboard, EditorStyles.popup))
            {
                var dd = new TypeDropdown(_typeDdState, _types, picked =>
                {
                    _typeFilter = picked;
                    _viewDirty = true;
                    Repaint();
                });
                AdvancedDropdownHeight.ShowCapped(dd, typeBtn);
            }
            _needsReviewOnly = GUILayout.Toggle(_needsReviewOnly, "needs review only", GUILayout.Width(140));
            _hideWinnerOnly = GUILayout.Toggle(_hideWinnerOnly, "hide winner-only", GUILayout.Width(140));
            EditorGUILayout.EndHorizontal();

            if (_viewDirty
                || prevStatus != _status
                || prevName != _nameFilter
                || prevType != _typeFilter
                || prevNeeds != _needsReviewOnly
                || prevHide != _hideWinnerOnly)
            {
                ApplyFilter();
                _viewDirty = false;
            }
        }

        void DrawStats()
        {
            var s = _result.Stats;
            int need = _elemStatus.Values.Count(v => v == "new" || v == "changed");
            int resolved = _elemStatus.Values.Count(v => v == "resolved");
            int orphans = _patchOrphans.Count;
            EditorGUILayout.LabelField(
                $"conflicts {s.Conflicts} (needs review {need}, resolved {resolved}, odin {s.OdinConflicts}) · new {s.New} · identical {s.Identical} · roots {s.Roots} · showing {_view.Count}"
                + (orphans > 0 ? $" · patch orphans {orphans}" : ""),
                EditorStyles.miniLabel);
            DrawPatchOrphans();
        }

        void DrawPatchOrphans()
        {
            if (_result == null) return;
            int n = _patchOrphans.Count;
            string title = n == 0
                ? "Patch orphans — none"
                : $"Patch orphans — {n} (Patch/ overrides that no longer match a live conflict)";
            _showPatchOrphans = EditorGUILayout.Foldout(_showPatchOrphans, title, true);
            if (!_showPatchOrphans) return;

            if (n == 0)
            {
                EditorGUILayout.HelpBox(
                    "Every element in Assets/Databases/Patch/ either still conflicts across the compared mods, or Patch/ is empty.",
                    MessageType.None);
                return;
            }

            EditorGUILayout.HelpBox(
                "These Patch/ elements still load last and override. Gone = dropped by all mods. Sole = only one mod left. Identical = mods agree now. Remove if the override is stale; keep if you still want a custom edit.",
                MessageType.Warning);

            PatchOrphan toRemove = null;
            foreach (var o in _patchOrphans.OrderBy(x => x.Kind).ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"[{KindLabel(o.Kind)}]  {o.Entry.Name}  ({o.Entry.TypeHint})", GUILayout.MinWidth(280));
                EditorGUILayout.LabelField(o.Detail, EditorStyles.miniLabel);
                if (GUILayout.Button("Compare", GUILayout.Width(72)))
                    OpenCompareOrphan(o);
                if (GUILayout.Button("Ping", GUILayout.Width(44)))
                    PingPatchEntry(o.Entry);
                if (GUILayout.Button("Remove", GUILayout.Width(64)))
                    toRemove = o;
                EditorGUILayout.EndHorizontal();
            }

            if (toRemove != null)
                RemovePatchOrphan(toRemove);
        }

        /// <summary>
        /// Side-by-side for a Patch orphan: nav list is the current orphan set (Patch always present).
        /// </summary>
        void OpenCompareOrphan(PatchOrphan focus)
        {
            if (focus?.Entry == null || _mods == null) return;
            var items = new List<CompatCompareWindow.CompareItem>();
            int idx = 0;
            foreach (var o in _patchOrphans.OrderBy(x => x.Kind).ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                var item = BuildCompareItemForOrphan(o);
                if (item == null) continue;
                if (ReferenceEquals(o, focus)) idx = items.Count;
                items.Add(item);
            }
            if (items.Count == 0)
            {
                EditorUtility.DisplayDialog("Compat Patcher",
                    "Could not open Compare for this orphan (no mod or Patch version found).", "OK");
                return;
            }
            CompatCompareWindow.Show(items, idx, OnCompareResolveAsWinner, _unlockIndex);
        }

        CompatCompareWindow.CompareItem BuildCompareItemForOrphan(PatchOrphan o)
        {
            if (o?.Entry == null) return null;
            var row = FindRowForPatchEntry(o.Entry);
            if (row != null)
            {
                var item = BuildCompareItem(row);
                if (item != null) item.inPatch = true;
                return item;
            }

            // Gone from analyze rows — still show whatever mods define the name + Patch.
            var versions = new List<(string mod, HkMod modObj, HkElement el)>();
            foreach (var mod in _mods)
            {
                HkElement best = null;
                foreach (var el in mod.Elements.Values)
                {
                    if (el.IsRoot || el.Name != o.Entry.Name) continue;
                    if (el.Type == o.Entry.Type
                        || string.Equals(el.TypeHint, o.Entry.TypeHint, StringComparison.OrdinalIgnoreCase))
                    {
                        best = el;
                        break;
                    }
                    best ??= el;
                }
                if (best != null) versions.Add((mod.Name, mod, best));
            }
            return new CompatCompareWindow.CompareItem
            {
                name = o.Entry.Name,
                typeHint = o.Entry.TypeHint,
                typeName = FriendlyTypeHint(o.Entry.TypeHint) ?? o.Entry.TypeHint,
                typeKey = o.Entry.Type,
                versions = versions,
                winner = versions.Count > 0 ? versions[versions.Count - 1].mod : "",
                odin = false,
                diffs = new List<Diff>(),
                inPatch = true,
                resolved = false,
            };
        }

        ElementRow FindRowForPatchEntry(PatchEntry pe)
        {
            if (pe == null || _result == null) return null;
            var matches = _result.Rows
                .Where(r => r.Status != ElemStatus.Root && r.Name == pe.Name)
                .ToList();
            if (matches.Count == 0) return null;
            return matches.FirstOrDefault(r => r.Type == pe.Type
                    || string.Equals(r.TypeHint, pe.TypeHint, StringComparison.OrdinalIgnoreCase))
                ?? matches[0];
        }

        string FriendlyTypeHint(string typeHint)
        {
            if (string.IsNullOrEmpty(typeHint)) return typeHint;
            // Prefer resolved class name when the type filter already knows this stem.
            foreach (var r in _result?.Rows ?? Enumerable.Empty<ElementRow>())
            {
                if (string.Equals(r.TypeHint, typeHint, StringComparison.OrdinalIgnoreCase))
                    return FriendlyType(r);
            }
            return typeHint;
        }

        void PingPatchEntry(PatchEntry pe)
        {
            if (pe == null || string.IsNullOrEmpty(pe.AssetPath)) return;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(pe.AssetPath))
            {
                if (o != null && o.name == pe.Name)
                {
                    Selection.activeObject = o;
                    EditorGUIUtility.PingObject(o);
                    return;
                }
            }
            var main = AssetDatabase.LoadMainAssetAtPath(pe.AssetPath);
            if (main != null) { Selection.activeObject = main; EditorGUIUtility.PingObject(main); }
        }

        void RemovePatchOrphan(PatchOrphan o)
        {
            if (o?.Entry == null) return;
            if (!EditorUtility.DisplayDialog("Remove from Patch/",
                $"Remove '{o.Entry.Name}' from:\n{o.Entry.AssetPath}\n\n{o.Detail}",
                "Remove", "Cancel"))
                return;
            if (!PatchBuilder.RemoveNamedElement(o.Entry.AssetPath, o.Entry.Name))
            {
                Debug.LogWarning($"[CompatPatcher] Could not remove '{o.Entry.Name}' from {o.Entry.AssetPath}.");
                return;
            }
            Debug.Log($"[CompatPatcher] Removed orphan '{o.Entry.Name}' from {o.Entry.AssetPath}.");
            ScanPatch();
            ComputePatchOrphans();
            Repaint();
        }

        void ApplyFilter()
        {
            string nm = _nameFilter.Trim().ToLowerInvariant();
            _view = _result.Rows.Where(r =>
                // Collection containers (blank Type column) — never gameplay elements.
                !IsCollectionContainerRow(r) &&
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

        /// <summary>
        /// Datatable collection roots (file stem == element name) — FriendlyType is empty for these.
        /// </summary>
        static bool IsCollectionContainerRow(ElementRow r)
        {
            if (r == null) return true;
            if (r.Status == ElemStatus.Root) return true;
            if (!string.IsNullOrEmpty(r.TypeHint)
                && string.Equals(r.Name, r.TypeHint, StringComparison.Ordinal))
                return true;
            if (r.Elements != null)
            {
                foreach (var el in r.Elements.Values)
                    if (el != null && el.IsRoot) return true;
            }
            return false;
        }

        static bool IsWinnerOnlyConflict(ElementRow r)
        {
            if (r.Status != ElemStatus.Conflict || r.Conflict == null) return false;
            return r.Conflict.Diffs.Count > 0 && r.Conflict.Diffs.All(d => d.Kind == DiffKind.ExtraInWinner);
        }

        bool NeedsReview(ElementRow r)
        {
            if (r.Status != ElemStatus.Conflict) return false;
            if (_patchNames.Contains(r.Name)) return false;
            return _elemStatus.TryGetValue(ElemKey(r), out var s) && (s == "new" || s == "changed");
        }

        bool IsResolved(ElementRow r) =>
            r != null && _elemStatus.TryGetValue(ElemKey(r), out var s) && s == "resolved";

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
                    if (IsResolved(r)) s = "✓resolved";
                    if (_patchNames.Contains(r.Name)) s += (s.Length > 0 ? "  " : "") + "✓in patch";
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
                bool multiOn = _multiSelect.Contains(ElemKey(r));
                if (r == _selected || multiOn) EditorGUI.DrawRect(rr, ROW_SEL);
                else if ((i & 1) == 1) EditorGUI.DrawRect(rr, ROW_ALT);
                if (Event.current.type == EventType.MouseDown && rr.Contains(mouse))
                {
                    bool ctrl = Event.current.control || Event.current.command;
                    string ek = ElemKey(r);
                    if (ctrl)
                    {
                        if (!_multiSelect.Add(ek)) _multiSelect.Remove(ek);
                        _selected = r;
                        InvalidateDetailDiffs();
                    }
                    else
                    {
                        if (_selected != r)
                        {
                            _selected = r;
                            InvalidateDetailDiffs();
                        }
                    }
                    Repaint();
                }
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
                EditorGUILayout.LabelField($"{(_unlockIndex != null ? _unlockIndex.FormatElementLabel(row.Name) : row.Name)}   ·   {FriendlyType(row)}   ·   {string.Join("/", row.Contributors)}", EditorStyles.boldLabel);

                if (row.Conflict == null)
                {
                    bool inPatch = _patchNames.Contains(row.Name);
                    EditorGUILayout.HelpBox(row.Status == ElemStatus.New
                        ? "New element (single mod). Import & Edit to bring it into the patch and adjust."
                        : row.Status == ElemStatus.Root ? "Collection/container object — not a gameplay element."
                        : "Identical across mods — no action needed.", MessageType.None);
                    EditorGUILayout.BeginHorizontal();
                    using (new EditorGUI.DisabledScope(row.Status == ElemStatus.Root || inPatch))
                    {
                        if (GUILayout.Button("Import & Edit into Patch/", GUILayout.Width(200))) ImportChosen(row, row.Winner);
                    }
                    if (GUILayout.Button("Compare side-by-side", GUILayout.Width(180))) OpenCompare(row);
                    EditorGUILayout.EndHorizontal();
                    if (inPatch)
                        EditorGUILayout.LabelField("✓ In patch — use Compare side-by-side to inspect/edit the patch version.", EditorStyles.miniLabel);
                }
                else
                {
                    bool inPatch = _patchNames.Contains(row.Name);
                    bool resolved = IsResolved(row);
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
                    using (new EditorGUI.DisabledScope(inPatch))
                    {
                        if (GUILayout.Button("Import chosen into Patch/", GUILayout.Width(200)))
                            ImportChosen(row, _choice.TryGetValue(key, out var c2) ? c2 : row.Winner);
                    }
                    using (new EditorGUI.DisabledScope(resolved || inPatch))
                    {
                        if (GUILayout.Button("Mark resolved (accept chosen)", GUILayout.Width(210)))
                            MarkResolved(row, _choice.TryGetValue(key, out var c3) ? c3 : row.Winner);
                    }
                    if (GUILayout.Button("Compare side-by-side", GUILayout.Width(180))) OpenCompare(row);
                    EditorGUILayout.EndHorizontal();
                    if (inPatch)
                        EditorGUILayout.LabelField("✓ In patch — use Compare side-by-side to inspect/edit the patch version.", EditorStyles.miniLabel);
                    else if (resolved)
                        EditorGUILayout.LabelField("✓ Resolved — winner accepted without import. You can still Import chosen if you change your mind.", EditorStyles.miniLabel);

                    EditorGUILayout.Space(2);
                    EnsureDetailDiffs(row);
                    var detailDiffs = _detailDiffList ?? row.Conflict.Diffs;
                    int resolvedN = detailDiffs?.Count(d => d.Status == "carried") ?? 0;
                    if (resolvedN > 0)
                    {
                        EditorGUILayout.BeginHorizontal();
                        _showResolvedDiffs = GUILayout.Toggle(_showResolvedDiffs,
                            $"Show resolved ({resolvedN})", EditorStyles.miniButton, GUILayout.Width(130));
                        EditorGUILayout.EndHorizontal();
                    }
                    DiffGui.DrawTable(detailDiffs,
                        _detailDiffWinner ?? row.Winner,
                        _detailDiffList != null ? _detailDiffOdin : row.Conflict.Odin,
                        onApplyPattern: (diff, srcMod) => ApplyMassChangeFromDiff(diff, srcMod),
                        countInPatchForPath: CountPatchMatchesForDiff,
                        onResolveDiff: ResolveDetailDiff,
                        hideResolved: !_showResolvedDiffs,
                        unlockIndex: _unlockIndex,
                        currentElementName: row.Name,
                        winnerRefsOnElement: UnlockCarrierIndex.CollectClassifiableRefNamesFromElement(
                            WinnerElementForDetail(row)));
                }
            }

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        HkElement WinnerElementForDetail(ElementRow row)
        {
            if (row == null) return null;
            string w = _detailDiffWinner ?? row.Winner;
            if (string.Equals(w, "Patch", StringComparison.Ordinal))
            {
                var patchEls = FindPatchHkElements(row.Name);
                if (patchEls.Count == 0) return null;
                return patchEls.FirstOrDefault(e => e.TypeHint == row.TypeHint) ?? patchEls[0];
            }
            if (row.Elements != null && row.Elements.TryGetValue(w, out var el))
                return el;
            return null;
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
                    {
                        _elemStatus[key] = "resolved";
                        _choice[key] = row.Contributors.Contains(pd.choice) ? pd.choice : row.Winner;
                    }
                    else if (_prior.ContainsKey(key))
                    {
                        _elemStatus[key] = "changed";
                        _choice[key] = row.Contributors.Contains(_prior[key].choice) ? _prior[key].choice : row.Winner;
                    }
                    else
                    {
                        _elemStatus[key] = "new";
                        _choice[key] = row.Winner;
                    }
                }

                ShowCompareProgress(0.7, 0.75, "Compat Patcher", 0, "Resolving type names…");
                ResolveTypeNames();
                _types = _result.Rows
                    .Where(r => r.Status != ElemStatus.Root)
                    .Select(FriendlyType)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct()
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (_typeFilter.Length > 0 && !_types.Contains(_typeFilter)) _typeFilter = "";
                _selected = null;
                _multiSelect.Clear();
                ShowCompareProgress(0.75, 0.8, "Compat Patcher", 0, "Scanning patch directory…");
                ScanPatch();
                ComputePatchOrphans();
                ShowCompareProgress(0.78, 0.82, "Compat Patcher", 0, "Building unlock carrier index…");
                _unlockIndex = UnlockCarrierIndex.Build(_mods);
                ShowCompareProgress(0.8, 1.0, "Compat Patcher", 0, "Validating load order (Vanilla → mods)…");
                Validate((sub, label) => ShowCompareProgress(0.8, 1.0, "Compat Patcher", sub, label));
                InvalidateDetailDiffs();
                _viewDirty = true;
                ApplyFilter();
                _viewDirty = false;
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
                var item = BuildCompareItem(r);
                if (item == null) continue;
                if (r == row) idx = items.Count;
                items.Add(item);
            }
            CompatCompareWindow.Show(items, idx, OnCompareResolveAsWinner, _unlockIndex);
        }

        CompatCompareWindow.CompareItem BuildCompareItem(ElementRow r)
        {
            if (r == null || _mods == null) return null;
            var versions = r.Contributors
                .Select(mn => (mn, _mods.FirstOrDefault(m => m.Name == mn), r.Elements.TryGetValue(mn, out var el) ? el : null))
                .Where(v => v.Item2 != null && v.Item3 != null).ToList();
            if (versions.Count == 0) return null;
            return new CompatCompareWindow.CompareItem
            {
                name = r.Name,
                typeHint = r.TypeHint,
                typeName = FriendlyType(r),
                typeKey = r.Type,
                versions = versions,
                winner = r.Winner,
                odin = r.Conflict?.Odin ?? false,
                diffs = r.Conflict?.Diffs ?? new List<Diff>(),
                inPatch = _patchNames.Contains(r.Name),
                resolved = IsResolved(r),
            };
        }

        void OnCompareResolveAsWinner(CompatCompareWindow.CompareItem item)
        {
            if (item == null || _result == null) return;
            var row = _result.Rows.FirstOrDefault(r =>
                r.Name == item.name && (r.Type == item.typeKey || r.TypeHint == item.typeHint));
            if (row == null || row.Status != ElemStatus.Conflict)
            {
                Debug.LogWarning($"[CompatPatcher] Resolve as winner: no conflict row for '{item.name}'.");
                return;
            }
            MarkResolved(row, item.winner);
            item.resolved = true;
        }

        void MarkResolved(ElementRow row, string choiceMod, bool persist = true)
        {
            if (row == null || row.Status != ElemStatus.Conflict) return;
            string key = ElemKey(row);
            if (string.IsNullOrEmpty(choiceMod)) choiceMod = row.Winner;
            _choice[key] = choiceMod;
            if (!_elemFp.ContainsKey(key)) _elemFp[key] = ElementFingerprint(row);
            _elemStatus[key] = "resolved";
            if (persist)
            {
                PersistSidecar();
                _viewDirty = true;
                ApplyFilter();
                _viewDirty = false;
                Repaint();
            }
        }

        /// <summary>
        /// Mass actions operate on the current filtered list (<see cref="_view"/>), not every conflict.
        /// Skips already-in-patch and ✓resolved rows.
        /// </summary>
        List<ElementRow> MassActionTargets()
        {
            ApplyFilter();
            return _view.Where(r =>
                r.Status == ElemStatus.Conflict
                && !_patchNames.Contains(r.Name)
                && !IsResolved(r)
                && NeedsReview(r)).ToList();
        }

        void MassResolveAsWinner()
        {
            if (_result == null) return;
            var pending = MassActionTargets();
            if (pending.Count == 0)
            {
                EditorUtility.DisplayDialog("Compat Patcher",
                    "No conflicts in the current filtered list need review (or all are already in patch / resolved).", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Resolve all as winner",
                $"Mark {pending.Count} conflict(s) from the current filtered list as resolved, accepting each load-order winner?\n\n"
                + $"Showing {_view.Count} row(s) with current filters.\n"
                + "Does not import into Patch/. Writes the sidecar so this is remembered next session.",
                "Resolve", "Cancel")) return;

            foreach (var row in pending)
                MarkResolved(row, row.Winner, persist: false);
            PersistSidecar();
            _viewDirty = true;
            ApplyFilter();
            _viewDirty = false;
            Repaint();
            Debug.Log($"[CompatPatcher] Mass resolve as winner: {pending.Count} (filtered) → sidecar.");
            EditorUtility.DisplayDialog("Compat Patcher", $"Marked {pending.Count} conflict(s) resolved (winner accepted).", "OK");
        }

        void ResolveDetailDiff(Diff d)
        {
            if (d == null || _selected == null) return;
            if (string.IsNullOrEmpty(d.Sig) || string.IsNullOrEmpty(d.Fp))
                ConflictAnalyzer.FinalizeDiffs(new[] { d }, _selected.Type, _selected.Name, _prior);
            var decision = new Sidecar.Decision
            {
                sig = d.Sig,
                fp = d.Fp,
                choice = _detailDiffWinner ?? _selected.Winner ?? "accepted",
                kind = "diff",
                element = _selected.Name,
            };
            Sidecar.UpsertDecision(EnsureSidecarPath(), decision);
            _prior[d.Sig] = decision;
            d.Status = "carried";
            d.Choice = decision.choice;
            // Keep conflict.Diffs in sync when detail list is a live recompute copy.
            if (_selected.Conflict?.Diffs != null)
            {
                foreach (var cd in _selected.Conflict.Diffs)
                    if (cd.Sig == d.Sig) { cd.Status = "carried"; cd.Choice = decision.choice; }
            }
            Repaint();
        }

        string EnsureSidecarPath()
        {
            if (string.IsNullOrEmpty(_sidecarPath))
                _sidecarPath = Sidecar.DefaultPath;
            return _sidecarPath;
        }

        /// <summary>
        /// Write resolved decisions + current mod list (name/path) to the sidecar.
        /// Called on Mark resolved / mass resolve / Export.
        /// </summary>
        void PersistSidecar()
        {
            string path = EnsureSidecarPath();
            var bySig = Sidecar.LoadIndex(path);

            if (_result != null)
            {
                foreach (var row in _result.Rows)
                {
                    if (row.Status != ElemStatus.Conflict) continue;
                    string key = ElemKey(row);
                    if (!_elemStatus.TryGetValue(key, out var st)) continue;
                    if (st == "resolved")
                    {
                        bySig[key] = new Sidecar.Decision
                        {
                            sig = key,
                            fp = _elemFp.TryGetValue(key, out var fp) ? fp : ElementFingerprint(row),
                            choice = _choice.TryGetValue(key, out var c) ? c : row.Winner,
                            kind = "element",
                            element = row.Name,
                        };
                    }
                    else if (st == "new")
                        bySig.Remove(key);
                    // "changed": keep prior decision in file (stale fp) so next Compare still sees "changed"
                }
            }

            var sources = _sources.Select(s => new Sidecar.Source { name = s.name, path = s.path }).ToList();
            Sidecar.Save(path, sources, bySig.Values);
            _sidecarPath = path;
            // Import only the sidecar file (not a full Refresh) so the Project window sees it without
            // stalling the editor after mass import/resolve.
            string assetPath = path.Replace('\\', '/');
            int assetsIdx = assetPath.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (assetsIdx >= 0)
                AssetDatabase.ImportAsset(assetPath.Substring(assetsIdx + 1), ImportAssetOptions.ForceUpdate);
            else if (assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        void OpenMassChange()
        {
            if (_result == null || _view == null || _view.Count == 0)
            {
                EditorUtility.DisplayDialog("Mass Change",
                    "No rows in the current filtered view. Compare mods and set filters (e.g. Type) first.", "OK");
                return;
            }

            IEnumerable<ElementRow> scopeRows = _view;
            HashSet<string> customKeys = null;
            if (_multiSelect.Count > 0)
            {
                customKeys = new HashSet<string>(_multiSelect);
                scopeRows = _view.Where(r => customKeys.Contains(ElemKey(r))).ToList();
                if (!scopeRows.Any())
                {
                    EditorUtility.DisplayDialog("Mass Change",
                        "Custom selection has no rows in the current filtered view. Clear selection or adjust filters.", "OK");
                    return;
                }
            }

            var candidates = MassChange.ComputeCandidates(scopeRows);
            if (candidates.Count == 0)
            {
                EditorUtility.DisplayDialog("Mass Change",
                    "No field-level conflict diffs in scope. Filter to conflict elements that differ on concrete fields.", "OK");
                return;
            }

            var pathByName = BuildPatchPathByName();
            MassFieldChangeWindow.Show(
                candidates,
                pathByName,
                new HashSet<string>(_patchNames),
                customKeys,
                ElemKey,
                onDone: () =>
                {
                    ScanPatch();
                    InvalidateDetailDiffs();
                    _viewDirty = true;
                    ApplyFilter();
                    _viewDirty = false;
                    Repaint();
                });
        }

        Dictionary<string, string> BuildPatchPathByName()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in _patchEntries)
            {
                if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                d[e.Name] = e.AssetPath;
            }
            return d;
        }

        int CountPatchMatchesForDiff(Diff diff)
        {
            if (diff == null || _view == null) return 0;
            var pool = _multiSelect.Count > 0
                ? _view.Where(r => _multiSelect.Contains(ElemKey(r)))
                : _view.AsEnumerable();
            int n = 0;
            foreach (var row in pool)
            {
                if (!_patchNames.Contains(row.Name) || row.Conflict?.Diffs == null) continue;
                if (row.Conflict.Diffs.Any(d => d.Path == diff.Path && d.Kind == diff.Kind))
                    n++;
            }
            return n;
        }

        void ApplyMassChangeFromDiff(Diff diff, string sourceMod)
        {
            if (diff == null || string.IsNullOrEmpty(sourceMod)) return;
            var parsed = FieldApplier.Parse(diff.Path, diff.Kind,
                diff.Values.FirstOrDefault(kv => kv.Key == sourceMod).Value);
            if (!FieldApplier.IsSupported(parsed))
            {
                EditorUtility.DisplayDialog("Mass Change",
                    "Unsupported pattern: " + (parsed.UnsupportedReason ?? "?"), "OK");
                return;
            }

            var pool = (_multiSelect.Count > 0
                ? _view.Where(r => _multiSelect.Contains(ElemKey(r)))
                : _view).Where(r => r.Conflict?.Diffs != null
                    && r.Conflict.Diffs.Any(d => d.Path == diff.Path && d.Kind == diff.Kind)).ToList();

            int inPatch = pool.Count(r => _patchNames.Contains(r.Name));
            int notInPatch = pool.Count - inPatch;
            if (inPatch == 0)
            {
                EditorUtility.DisplayDialog("Mass Change",
                    "No matching elements in Patch for this pattern. Import into Patch/ first.", "OK");
                return;
            }

            string confirm =
                $"Apply field change to {inPatch} Patch element(s)?\n\n"
                + $"Path: {diff.Path}\nSource mod: {sourceMod}\n";
            if (notInPatch > 0)
                confirm += $"\n{notInPatch} matching row(s) not in Patch will be skipped.";
            if (!EditorUtility.DisplayDialog("Mass Change", confirm, "Apply", "Cancel")) return;

            var candidate = new MassChange.Candidate
            {
                Path = diff.Path,
                Kind = diff.Kind,
                Sources = new List<string> { sourceMod },
                Elements = pool,
                Preview = diff.Values.TryGetValue(sourceMod, out var pv) ? pv : null,
                Action = sourceMod,
                ApplyKind = parsed.Kind,
            };
            var stats = MassChange.Apply(new[] { candidate }, BuildPatchPathByName(),
                _multiSelect.Count > 0 ? _multiSelect : null, ElemKey);
            ScanPatch();
            InvalidateDetailDiffs();
            _viewDirty = true;
            ApplyFilter();
            _viewDirty = false;
            EditorUtility.DisplayDialog("Mass Change",
                $"Applied: {stats.Applied}\nAlready had value: {stats.SkippedAlready}\n"
                + $"Not in Patch: {stats.SkippedNotInPatch}\nFailed: {stats.Failed}", "OK");
            Repaint();
        }

        void ImportChosen(ElementRow row, string sourceModName)
        {
            var mod = _mods?.FirstOrDefault(m => m.Name == sourceModName);
            if (mod == null || row.Elements == null || !row.Elements.TryGetValue(sourceModName, out var el) || el == null)
            { Debug.LogWarning($"[CompatPatcher] '{row.Name}' has no loaded version from mod '{sourceModName}' (re-run Compare?)."); return; }

            // Primary element only — mappers are separate conflict rows; import those explicitly if needed.
            PatchBuilder.ImportElement(mod, el);
            ScanPatch();
            // Import settles the conflict for review purposes too.
            if (row.Status == ElemStatus.Conflict)
                MarkResolved(row, sourceModName);
            SelectInPatch(el.TypeHint, el.Name);
        }

        void MassImport()
        {
            var conflicts = MassActionTargets();
            if (conflicts.Count == 0)
            {
                EditorUtility.DisplayDialog("Compat Patcher",
                    "No unresolved conflicts in the current filtered list to import.", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Import all conflicts",
                $"Import the Winner Mod version of {conflicts.Count} unresolved conflict(s) from the current filtered list into {PatchBuilder.PatchDir}?\n\n"
                + $"Showing {_view.Count} row(s) with current filters.\n"
                + "Skips ✓resolved and ✓in patch.\n"
                + "Always uses each row's load-order winner (ignores per-row radio choice).\n"
                + "Attached mappers are not auto-imported — only rows that are conflicts themselves.",
                "Import", "Cancel")) return;

            var toImport = new Dictionary<string, (HkMod mod, HkElement el)>();
            foreach (var row in conflicts)
            {
                string winner = row.Winner;
                var mod = _mods.FirstOrDefault(m => m.Name == winner);
                if (mod != null && row.Elements.TryGetValue(winner, out var el) && el != null)
                    toImport[el.Key] = (mod, el);
            }
            int n = PatchBuilder.ImportElements(toImport.Values);
            ScanPatch();
            foreach (var row in conflicts)
            {
                string key = ElemKey(row);
                _choice[key] = row.Winner;
                if (!_elemFp.ContainsKey(key)) _elemFp[key] = ElementFingerprint(row);
                _elemStatus[key] = "resolved";
            }
            PersistSidecar();
            _viewDirty = true;
            ApplyFilter();
            _viewDirty = false;
            Debug.Log($"[CompatPatcher] Mass import: {n} elements (Winner Mod, filtered) → {PatchBuilder.PatchDir}.");
            EditorUtility.DisplayDialog("Compat Patcher", $"Imported {n} elements (Winner Mod versions) into the patch.", "OK");
        }

        void SelectInPatch(string typeHint, string name)
        {
            string path = PatchBuilder.PatchDir + "/" + typeHint + ".asset";
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && o.name == name) { Selection.activeObject = o; EditorGUIUtility.PingObject(o); return; }
        }

        void Export()
        {
            if (_result == null)
            {
                EditorUtility.DisplayDialog("Compat Patcher", "Run Compare first.", "OK");
                return;
            }
            PersistSidecar();
            int n = Sidecar.LoadIndex(_sidecarPath).Count;
            Debug.Log($"[CompatPatcher] Sidecar written: {_sidecarPath}  ({n} resolved decision(s)).");
            EditorUtility.DisplayDialog("Compat Patcher",
                $"Sidecar written to:\n{_sidecarPath}\n\n{n} resolved decision(s) recorded.\n\n"
                + "✓resolved = winner/choice accepted without import.\n✓in patch = imported into Patch/.\n\n"
                + "Next Compare with this sidecar path restores resolved rows automatically.", "OK");
        }
    }
}
