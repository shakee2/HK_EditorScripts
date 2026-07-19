using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HK.ModTools.Shared;
using UnityEditor;
using UnityEngine;

namespace HK.CompatPatcher
{
    public partial class CompatPatcherWindow : EditorWindow
    {
        [Serializable] class SourceEntry { public string name; public string path; }

        enum StatusFilter { Conflicts, All, New, Identical }
        static readonly string[] STATUS_LABELS = { "Conflicts", "All", "New", "Identical" };

        enum TypeSortMode { DiffDesc, NameAsc }

        class TypeGroup
        {
            public string TypeName;
            public List<ElementRow> Rows = new List<ElementRow>();
            /// <summary>Cached name-sorted copy of <see cref="Rows"/> for the expanded child list.</summary>
            public List<ElementRow> SortedRows;
            public int DiffCount;
            // Per-group draw caches. Groups are recreated on every RebuildTypeGroups(), which is
            // called after any resolve/import/filter change, so these stay valid until the next
            // rebuild and never need explicit invalidation.
            public string StatsLabel;
            public float StatsWidth = -1f;
            public int PendingResolve = -1;
        }

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
        [SerializeField] bool _showPatchOrphans;
        [SerializeField] bool _showValidation;

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
        readonly List<TypeGroup> _typeGroups = new List<TypeGroup>();
        readonly HashSet<string> _expandedTypes = new HashSet<string>(StringComparer.Ordinal);
        string _selectedType = "";
        List<MassChange.Candidate> _patternCandidates = new List<MassChange.Candidate>();
        bool _patternsDirty = true;
        /// <summary>True while a deferred type-switch is tearing down Compare / rebuilding patterns.</summary>
        bool _typeSwitchPending;
        int _typeSwitchGen;
        /// <summary>Type whose child list is deferred one tick after expand (avoids expand MouseUp hitch).</summary>
        string _expandSettleType;
        int _expandSettleGen;
        bool _rightPaneCompare;
        CompatCompareWindow _hostedCompare;
        /// <summary>Element name highlighted in the type tree while embedded Compare is open.</summary>
        string _selectedElementName = "";

        // Load-order validation (recomputed every Compare): findings for the current order and for a
        // reversed order, so we can tell the user which hazards are order-caused vs intrinsic.
        List<Finding> _findings = new List<Finding>();
        List<Finding> _altFindings = new List<Finding>();
        string _altOrderName = "";
        string _validationNote = "";
        float _panelTop;   // Y where the workspace starts (after cards)
        float _typeListWidth = 280f;
        bool _draggingTypeSplit;

        StatusFilter _status = StatusFilter.Conflicts;
        string _nameFilter = "";
        readonly Dictionary<string, string> _typeName = new Dictionary<string, string>(); // "guid:fileID" -> class name
        bool _needsReviewOnly;
        bool _hideWinnerOnly = true; // conflicts whose only diffs are ExtraInWinner (already in winner mod) are hidden by default

        Vector2 _typeListScroll, _patternScroll, _validationCardScroll, _orphanCardScroll;
        readonly Dictionary<string, Vector2> _typeElemScroll = new Dictionary<string, Vector2>(StringComparer.Ordinal);
        bool _viewDirty = true;
        const float ROW_H = 18f;
        const float TYPE_ELEM_MAX_H = 240f;
        const float EL_ROW_H = 20f;
        static readonly Color ROW_ALT = new Color(1f, 1f, 1f, 0.03f);
        static readonly Color ROW_SEL = new Color(0.3f, 0.5f, 0.9f, 0.28f);
        static readonly Color EL_CARD_BG = new Color(0f, 0f, 0f, 0.18f);
        static readonly Color DOT_PATCH = new Color(0.35f, 0.78f, 0.42f, 1f);
        static readonly Color DOT_RESOLVED = new Color(0.92f, 0.72f, 0.28f, 1f);
        static readonly Color DOT_REVIEW = new Color(0.90f, 0.35f, 0.32f, 1f);
        static readonly Color DOT_OTHER = new Color(0.55f, 0.55f, 0.55f, 1f);

        const string PrefShowValidation = "CompatPatcher.ShowValidation";
        const string PrefShowOrphans = "CompatPatcher.ShowOrphans";
        const string PrefTypeListWidth = "CompatPatcher.TypeListWidth";
        const string PrefTypeSort = "CompatPatcher.TypeSort";

        TypeSortMode _typeSort = TypeSortMode.DiffDesc;

        [MenuItem("Tools/shakee's Tools/Compatibility Patcher", false, 4)]
        static void Open() => GetWindow<CompatPatcherWindow>("Compat Patcher");

        void OnEnable()
        {
            wantsMouseMove = false;
            ScanPatch();
            _showValidation = EditorPrefs.GetBool(PrefShowValidation, false);
            _showPatchOrphans = EditorPrefs.GetBool(PrefShowOrphans, false);
            _typeListWidth = EditorPrefs.GetFloat(PrefTypeListWidth, 280f);
            _typeSort = (TypeSortMode)EditorPrefs.GetInt(PrefTypeSort, (int)TypeSortMode.DiffDesc);
        }

        void OnFocus()
        {
            ScanPatch();
            _viewDirty = true;
            _patternsDirty = true;
            Repaint();
        }

        void OnDisable()
        {
            ExitEmbeddedCompare();
        }

        // Always know which elements already live in Assets/Databases/Patch/ (any layout).
        void ScanPatch()
        {
            _patchNames.Clear();
            _patchEntries.Clear();
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

        void InvalidateDetailDiffs() { /* detail DiffGui removed — patterns + Compare hold diffs */ }

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
            if (_result == null)
            {
                EditorGUILayout.HelpBox("Add the mods (load order top→bottom, last wins) and press Compare.", MessageType.Info);
                return;
            }
            EditorGUILayout.Space(4);
            DrawPostCompareChrome();

            if (Event.current.type == EventType.Repaint)
                _panelTop = GUILayoutUtility.GetLastRect().yMax + 2f;
            float top = _panelTop > 1f ? _panelTop : 140f;
            Rect rest = new Rect(0, top, position.width, Mathf.Max(0f, position.height - top));
            DrawWorkspace(rest);
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
            CompatCompareWindow.Show(items, idx, OnCompareResolveAsWinner, _unlockIndex, OnCompareImported);
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

        // One monotonically-increasing 0..1 bar across the whole Compare, so the user sees steady
        // progress instead of the bar snapping between phases.
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
                _selectedType = "";
                _expandedTypes.Clear();
                _patternsDirty = true;
                ShowCompareProgress(0.75, 0.8, "Compat Patcher", 0, "Scanning patch directory…");
                ScanPatch();
                ComputePatchOrphans();
                ShowCompareProgress(0.78, 0.82, "Compat Patcher", 0, "Building unlock carrier index…");
                _unlockIndex = UnlockCarrierIndex.Build(_mods);

                ShowCompareProgress(0.8, 1.0, "Compat Patcher", 0, "Validating load order (Vanilla → mods)…");
                Validate((sub, label) => ShowCompareProgress(0.8, 1.0, "Compat Patcher", sub, label));
                ApplyFilter();
                RebuildTypeGroups();
                AutoExpandHazardCardsAfterCompare();
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

        /// <summary>
        /// Compare Import landed an element in Patch/ — refresh ● markers / filters and treat as resolved.
        /// </summary>
        void OnCompareImported(CompatCompareWindow.CompareItem item, string sourceMod)
        {
            ScanPatch();
            if (item != null)
            {
                item.inPatch = true;
                item.resolved = true;
                if (_result != null)
                {
                    var row = _result.Rows.FirstOrDefault(r =>
                        r.Name == item.name && (r.Type == item.typeKey || r.TypeHint == item.typeHint));
                    if (row != null && row.Status == ElemStatus.Conflict)
                    {
                        string choice = !string.IsNullOrEmpty(sourceMod) ? sourceMod : item.winner;
                        MarkResolved(row, choice);
                        return;
                    }
                }
            }
            // Non-conflict import (or row missing): still rebuild list markers.
            RebuildTypeGroups();
            _patternsDirty = true;
            Repaint();
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
                RebuildTypeGroups();
                _patternsDirty = true;
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
            RebuildTypeGroups();
            _patternsDirty = true;
            _viewDirty = false;
            Repaint();
            Debug.Log($"[CompatPatcher] Mass resolve as winner: {pending.Count} (filtered) → sidecar.");
            EditorUtility.DisplayDialog("Compat Patcher", $"Marked {pending.Count} conflict(s) resolved (winner accepted).", "OK");
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
            if (_result == null)
            {
                EditorUtility.DisplayDialog("Mass Change", "Compare mods first.", "OK");
                return;
            }
            ApplyFilter();
            RebuildTypeGroups();
            var g = _typeGroups.FirstOrDefault(x => x.TypeName == _selectedType);
            var scopeRows = g != null ? g.Rows : _view;
            if (scopeRows == null || scopeRows.Count == 0)
            {
                EditorUtility.DisplayDialog("Mass Change",
                    "No rows in scope. Select a type with conflicts, or adjust filters.", "OK");
                return;
            }

            var candidates = MassChange.ComputeCandidates(scopeRows);
            if (candidates.Count == 0)
            {
                EditorUtility.DisplayDialog("Mass Change",
                    "No field-level conflict diffs in scope.", "OK");
                return;
            }

            var pathByName = BuildPatchPathByName();
            MassFieldChangeWindow.Show(
                candidates,
                pathByName,
                new HashSet<string>(_patchNames),
                null,
                ElemKey,
                onDone: () =>
                {
                    ScanPatch();
                    _patternsDirty = true;
                    _viewDirty = true;
                    ApplyFilter();
                    RebuildTypeGroups();
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
            else
            {
                RebuildTypeGroups();
                _patternsDirty = true;
                Repaint();
            }
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
            RebuildTypeGroups();
            _patternsDirty = true;
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
