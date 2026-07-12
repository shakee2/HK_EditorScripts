using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amplitude.Framework.Asset;
using Amplitude.Mercury.Animation;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
// Both Amplitude.Framework.Asset.AssetDatabase and UnityEditor.AssetDatabase are in scope;
// alias the Amplitude one so the mount/unmount calls read unqualified (matching
// VanillaDatabaseMount / ArchiveTranslations) while Unity's stay fully qualified below.
using AssetDatabase = Amplitude.Framework.Asset.AssetDatabase;

/// <summary>
/// Asset Explorer for the vanilla Humankind asset bundles. The Mod Editor's own
/// "Asset Explorer" window only *browses* the bundles (textures, models, materials,
/// collections, …) with no way to pull anything into the project. This window mounts
/// any bundle found under &lt;Humankind&gt;/AssetBundles/&lt;folder&gt;/&lt;file&gt;.assetbundle,
/// lists every descriptor the provider exposes (main assets + sub-assets), previews the
/// selected one (texture thumbnail, mesh stats, or embedded inspector), and imports it:
///   - Texture2D / Sprite  -> exported as a PNG into a chosen project folder.
///   - anything else       -> cloned via Object.Instantiate + AssetDatabase.CreateAsset
///                            into a chosen project folder (same clone trick Probing.cs
///                            step 3 proved viable for Amplitude ScriptableObjects).
///
/// Reuses VanillaDatabaseMount for the MercuryDatabases bundle (so the existing
/// Database Browser and this explorer share one mount of it); mounts every other
/// bundle on demand and unmounts on window close so we don't leak providers.
/// </summary>
public class AssetExplorer : EditorWindow
{
    // ── Bundle picker state ───────────────────────────────────────────────────
    // Discovered bundles, grouped by their folder name under AssetBundles/.
    class BundleInfo { public string folder; public string file; public string fullPath; }
    List<BundleInfo> _bundles;
    BundleInfo _bundle;                 // currently selected bundle
    AdvancedDropdownState _bundleDdState = new();

    // ── Mounted provider for the selected (non-MercuryDatabases) bundle ──────
    static string ProviderName(BundleInfo b) => Path.GetFileName(b.fullPath).ToLowerInvariant();
    IAssetProvider _provider;
    string _providerName;               // the provider name we actually mounted (for unmount)
    const string MercuryDatabasesProvider = "mercurydatabases.assetbundle";

    // ── Descriptors ──────────────────────────────────────────────────────────
    class Desc
    {
        public AssetDescriptor descriptor;
        public Type type;
        public string typeName;
        public string name;
        public bool isSub;              // came from FetchAllSubAssetsOfType
        public UnityEngine.Object loaded; // cached loaded asset (lazy)
    }
    List<Desc> _descs = new();
    List<Desc> _view = new();

    // ── Filters ──────────────────────────────────────────────────────────────
    string _search = "";
    List<string> _types = new();
    string _typeFilter = "";
    AdvancedDropdownState _typeDdState = new();
    enum GroupMode { None, Type, Folder }
    GroupMode _groupMode = GroupMode.Type;
    HashSet<string> _collapsed = new();

    // flattened display (header rows + entry rows) for the virtualized list. `collapseKey` is the
    // unique key tracked in `_collapsed` (prefixed per mode so Type/Folder keys can't collide);
    // `indent` is the tree depth (in 14px steps) used by both header foldouts and leaf rows.
    class DI { public bool isHeader; public string headerType; public string collapseKey; public int count; public Desc desc; public int indent; }
    List<DI> _display = new();

    // ── Selection + preview ──────────────────────────────────────────────────
    Desc _selected;
    Editor _editor;
    Vector2 _listScroll, _inspScroll;
    Texture2D _texPreview;             // readable copy of a Texture2D for the preview box

    // ── Import ───────────────────────────────────────────────────────────────
    string _importFolder = "Assets/Imported";
    string ImportFolderKey => "AssetExplorer.ImportFolder";

    // ── Layout ───────────────────────────────────────────────────────────────
    float _leftWidth = 420f;
    bool _draggingSplit;
    const float ROW_H = 18f;
    const float SPLIT_W = 5f;
    const float TYPE_COL_W = 190f;

    static readonly Color ROW_ALT   = new Color(1f, 1f, 1f, 0.03f);
    static readonly Color ROW_HOVER = new Color(0.3f, 0.5f, 0.9f, 0.18f);
    static readonly Color ROW_SEL   = new Color(0.3f, 0.5f, 0.9f, 0.35f);
    static readonly Color ROW_LINE  = new Color(0f, 0f, 0f, 0.20f);
    static readonly Color HEADER_BG = new Color(1f, 1f, 1f, 0.07f);

    GUIStyle _typeColStyle, _headerStyle;

    [MenuItem("Tools/Asset Explorer", false, 7)]
    static void Open() => GetWindow<AssetExplorer>("Asset Explorer");

    void OnEnable()
    {
        wantsMouseMove = true;
        _importFolder = EditorPrefs.GetString(ImportFolderKey, _importFolder);
        RefreshBundleList();
    }

    void OnDisable()
    {
        if (_editor != null) DestroyImmediate(_editor);
        if (_texPreview != null) { DestroyImmediate(_texPreview); _texPreview = null; }
        Unmount();
    }

    void InitStyles()
    {
        if (_typeColStyle == null)
            _typeColStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = EditorGUIUtility.isProSkin
                    ? new Color(0.75f, 0.75f, 0.75f) : new Color(0.3f, 0.3f, 0.3f) }
            };
        if (_headerStyle == null)
            _headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
    }

    // ── Bundle discovery ──────────────────────────────────────────────────────
    // Walks <Humankind>/AssetBundles/<folder>/<file>.assetbundle. The MercuryDatabases
    // bundle is included so the explorer can browse it via the shared VanillaDatabaseMount
    // mount (no double-mount); every other bundle gets its own on-demand mount below.
    void RefreshBundleList()
    {
        _bundles = new List<BundleInfo>();
        string mercuryFolder = Amplitude.Mercury.Production.Modification.ModuleEditor.MercuryFolderPath;
        if (string.IsNullOrEmpty(mercuryFolder)) return;
        string root = Path.Combine(mercuryFolder, "AssetBundles");
        if (!Directory.Exists(root)) return;

        foreach (var dir in Directory.GetDirectories(root))
        {
            foreach (var f in Directory.GetFiles(dir, "*.assetbundle"))
            {
                _bundles.Add(new BundleInfo
                {
                    folder = Path.GetFileName(dir),
                    file = Path.GetFileName(f),
                    fullPath = f
                });
            }
        }
        _bundles = _bundles.OrderBy(b => b.folder).ThenBy(b => b.file).ToList();
    }

    // ── Mount / unmount ───────────────────────────────────────────────────────
    bool MountBundle(BundleInfo b, out string error)
    {
        error = null;
        // MercuryDatabases is owned by VanillaDatabaseMount — reuse it so the Database
        // Browser and this window share a single mount (Amplitude throws on double-mount).
        if (ProviderName(b) == MercuryDatabasesProvider)
        {
            if (!VanillaDatabaseMount.TryMount(out error)) return false;
            _provider = null;               // signals "use VanillaDatabaseMount" to the loaders
            _providerName = MercuryDatabasesProvider;
            return true;
        }

        if (_provider != null && _providerName == ProviderName(b)) return true; // already mounted

        Unmount();                          // different bundle — drop the previous one first

        if (!File.Exists(b.fullPath))
        {
            error = $"Bundle not found: {b.fullPath}";
            return false;
        }
        try
        {
            bool ok = AssetDatabase.TryMountAssetBundle(ProviderName(b), b.fullPath, uint.MaxValue,
                out _provider, Amplitude.Framework.Asset.AssetBundle.Options.None);
            if (!ok) { error = $"Failed to mount: {b.fullPath}"; return false; }
            _providerName = ProviderName(b);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Exception mounting {b.fullPath}: {ex.Message}";
            return false;
        }
    }

    void Unmount()
    {
        // Only unmount bundles *we* mounted. VanillaDatabaseMount owns MercuryDatabases;
        // touching it here would break the Database Browser mid-session.
        if (_provider != null && !string.IsNullOrEmpty(_providerName)
            && _providerName != MercuryDatabasesProvider)
        {
            try { AssetDatabase.UnmountAssetBundle(_providerName); } catch { }
        }
        _provider = null;
        _providerName = null;
    }

    // Resolve the live provider for the current bundle (VanillaDatabaseMount's or ours).
    IAssetProvider ActiveProvider
    {
        get
        {
            if (_providerName == MercuryDatabasesProvider)
            {
                // VanillaDatabaseMount exposes IsVanillaAsset etc., but for raw descriptor
                // enumeration we need the provider itself. Recover it from AllProviders by name
                // (same recovery pattern ArchiveTranslations uses after a domain reload).
                foreach (var p in AssetDatabase.AllProviders)
                    if (p.Name == MercuryDatabasesProvider) return p;
                return null;
            }
            return _provider;
        }
    }

    // ── Load descriptors for the current bundle ──────────────────────────────
    void LoadDescriptors()
    {
        _descs.Clear();
        _selected = null;
        ClearEditor();
        ClearPreview();

        if (_bundle == null) { ApplyFilters(); return; }
        if (!MountBundle(_bundle, out var error))
        {
            Debug.LogError($"[AssetExplorer] {error}");
            ApplyFilters();
            return;
        }

        var provider = ActiveProvider;
        if (provider == null)
        {
            Debug.LogError($"[AssetExplorer] provider not available for '{_bundle.file}'.");
            ApplyFilters();
            return;
        }

        try
        {
            var descriptors = new List<AssetDescriptor>();
            provider.AddAllAssetDescriptors(descriptors, AssetProviderOption.AskForType);
            int n = descriptors.Count, i = 0;
            foreach (var d in descriptors)
            {
                if (++i % 128 == 0)
                    EditorUtility.DisplayProgressBar("Asset Explorer", "Indexing descriptors…", i / (float)n);
                var t = d.GetAssetType();
                _descs.Add(new Desc
                {
                    descriptor = d,
                    type = t,
                    typeName = t != null ? t.Name : d.TypeAsString ?? "?",
                    name = string.IsNullOrEmpty(d.FileName) ? "(unnamed)" : d.FileName,
                    isSub = false
                });

                // Sub-assets: rows inside a *Collection, sub-meshes/materials inside a model
                // asset, etc. FetchAllSubAssetsOfType filters by type on the provider side, so
                // probing UnityEngine.Object catches every sub-asset in one pass (this is what
                // VanillaDatabaseMount.LoadAllOfType and Unity's LoadAllAssetsAtPath do). The
                // call returns already-loaded objects, so sub.name is free; we cap the count per
                // descriptor to stay responsive on huge bundles (the main asset is always listed
                // regardless, so sub-asset names are a convenience, not the only entry point).
                if (t != null)
                {
                    int subCount = 0;
                    foreach (var sub in provider.FetchAllSubAssetsOfType(d.Guid, typeof(UnityEngine.Object)))
                    {
                        if (sub == null) continue;
                        if (subCount++ >= MaxSubAssetsPerDescriptor) break;
                        var st = sub.GetType();
                        _descs.Add(new Desc
                        {
                            descriptor = d,           // sub-assets share the owning descriptor
                            type = st,
                            typeName = st.Name,
                            name = sub.name,
                            isSub = true,
                            loaded = sub              // already loaded by FetchAllSubAssetsOfType
                        });
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[AssetExplorer] enumerating descriptors threw (provider likely stale): {e.Message}");
        }
        finally { EditorUtility.ClearProgressBar(); }

        ApplyFilters();
    }

    // Cap on how many sub-asset names we enumerate per descriptor, so a single bundle
    // entry with hundreds of sub-objects (e.g. a packed model with many materials) doesn't
    // blow up the list. The main asset is always listed separately, so this only trims
    // the sub-asset convenience rows.
    const int MaxSubAssetsPerDescriptor = 64;

    // ── Filters / display ─────────────────────────────────────────────────────
    void ApplyFilters()
    {
        _types = _descs.Select(d => d.typeName).Distinct().OrderBy(s => s).ToList();
        if (_typeFilter.Length > 0 && !_types.Contains(_typeFilter)) _typeFilter = "";

        string s = _search.Trim().ToLowerInvariant();
        _view = _descs.Where(d =>
            (_typeFilter.Length == 0 || d.typeName == _typeFilter) &&
            (s.Length == 0 || d.name.ToLowerInvariant().Contains(s) || d.typeName.ToLowerInvariant().Contains(s))
        ).OrderBy(d => d.typeName).ThenBy(d => d.name).ToList();
        BuildDisplay();
        Repaint();
    }

    void BuildDisplay()
    {
        _display = new List<DI>(_view.Count + 32);
        switch (_groupMode)
        {
            case GroupMode.None:
                foreach (var d in _view) _display.Add(new DI { desc = d });
                break;

            case GroupMode.Type:
                foreach (var g in _view.GroupBy(d => d.typeName).OrderBy(g => g.Key))
                {
                    string key = "T:" + g.Key;
                    bool collapsed = _collapsed.Contains(key);
                    _display.Add(new DI { isHeader = true, headerType = g.Key, collapseKey = key, count = g.Count() });
                    if (!collapsed)
                        foreach (var d in g.OrderBy(x => x.name))
                            _display.Add(new DI { desc = d, indent = 1 });
                }
                break;

            case GroupMode.Folder:
                BuildFolderDisplay();
                break;
        }
    }

    // ── Folder tree (mirrors the bundle's actual path hierarchy, e.g.
    //    Fragments/Humans/Human_Female_0/Body/Unit_Body_A.asset) ───────────────
    class FolderNode
    {
        public string fullPath, name;
        public List<FolderNode> children = new();
        public List<Desc> leaves = new();   // assets directly in this folder (mains + their subs)
    }

    static string FolderOf(Desc d)
    {
        string p = (d.descriptor.FilePath ?? d.descriptor.FileName ?? "").Replace('\\', '/');
        int slash = p.LastIndexOf('/');
        return slash >= 0 ? p.Substring(0, slash) : "";
    }

    void BuildFolderDisplay()
    {
        var root = new FolderNode { fullPath = "", name = "" };
        foreach (var d in _view)
        {
            string folder = FolderOf(d);
            var node = root;
            string acc = "";
            if (folder.Length > 0)
                foreach (var seg in folder.Split('/'))
                {
                    acc = acc.Length == 0 ? seg : acc + "/" + seg;
                    var existing = node.children.FirstOrDefault(c => c.name == seg);
                    if (existing == null) { existing = new FolderNode { fullPath = acc, name = seg }; node.children.Add(existing); }
                    node = existing;
                }
            node.leaves.Add(d);
        }
        FlattenFolder(root, 0);
    }

    int CountAssetsRecursive(FolderNode node)
    {
        int c = node.leaves.Count(d => !d.isSub);
        foreach (var ch in node.children) c += CountAssetsRecursive(ch);
        return c;
    }

    void FlattenFolder(FolderNode node, int depth)
    {
        foreach (var child in node.children.OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase))
        {
            string key = "F:" + child.fullPath;
            bool collapsed = _collapsed.Contains(key);
            _display.Add(new DI { isHeader = true, headerType = child.name, collapseKey = key, count = CountAssetsRecursive(child), indent = depth });
            if (collapsed) continue;
            FlattenFolder(child, depth + 1);

            // Cluster leaves by their owning file (main + its sub-assets together), main first,
            // clusters ordered by the main asset's name (or the first item's, for an orphan sub).
            var clusters = child.leaves
                .GroupBy(d => d.descriptor.FilePath ?? d.descriptor.FileName ?? "")
                .Select(g => new { items = g.OrderBy(x => x.isSub).ThenBy(x => x.name).ToList() })
                .OrderBy(c => c.items[0].name, StringComparer.OrdinalIgnoreCase);
            foreach (var cluster in clusters)
                foreach (var d in cluster.items)
                    _display.Add(new DI { desc = d, indent = depth + 1 + (d.isSub ? 1 : 0) });
        }
    }

    // ── Lazy load + preview ──────────────────────────────────────────────────
    UnityEngine.Object LoadAsset(Desc d)
    {
        if (d.loaded != null) return d.loaded;
        var provider = ActiveProvider;
        if (provider == null) return null;
        try { d.loaded = provider.LoadAsset<UnityEngine.Object>(d.descriptor); }
        catch (Exception e) { Debug.LogWarning($"[AssetExplorer] LoadAsset threw for '{d.name}': {e.Message}"); }
        return d.loaded;
    }

    void ClearEditor()
    {
        if (_editor != null) { DestroyImmediate(_editor); _editor = null; }
    }

    void ClearPreview()
    {
        if (_texPreview != null) { DestroyImmediate(_texPreview); _texPreview = null; }
    }

    void Select(Desc d)
    {
        bool changed = _selected != d;
        _selected = d;
        if (changed) { ClearEditor(); ClearPreview(); _inspScroll = Vector2.zero; }
        Repaint();
    }

    // ── GUI ───────────────────────────────────────────────────────────────────
    void OnGUI()
    {
        InitStyles();
        if (Event.current.type == EventType.MouseMove) Repaint();

        // Toolbar
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        if (GUILayout.Button("Refresh Bundles", EditorStyles.toolbarButton, GUILayout.Width(120)))
        { RefreshBundleList(); }
        GUILayout.Space(6);
        GUILayout.Label("Bundle:", EditorStyles.toolbarButton);
        string label = _bundle == null ? "(none)" : $"{_bundle.folder}/{_bundle.file}";
        if (GUILayout.Button(label, EditorStyles.toolbarPopup, GUILayout.Width(Mathf.Min(300, label.Length * 7 + 20))))
        {
            if (_bundles == null) RefreshBundleList();
            var dd = new BundleDropdown(_bundleDdState, _bundles, picked =>
            {
                _bundle = _bundles.FirstOrDefault(b => (b.folder + "/" + b.file) == picked);
                LoadDescriptors();
            });
            dd.Show(GUILayoutUtility.GetLastRect());
        }
        GUILayout.FlexibleSpace();
        GUILayout.Label($"{_view.Count} / {_descs.Count} items", EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();

        float toolbarH = EditorStyles.toolbar.fixedHeight > 0f ? EditorStyles.toolbar.fixedHeight : 21f;
        float top = toolbarH;
        float h = position.height - top;

        DrawLeft(new Rect(0, top, _leftWidth, h));
        DrawSplitter(new Rect(_leftWidth, top, SPLIT_W, h));
        DrawRight(new Rect(_leftWidth + SPLIT_W, top, position.width - _leftWidth - SPLIT_W, h));
    }

    // ── Left: filters + list ──────────────────────────────────────────────────
    void DrawLeft(Rect area)
    {
        GUILayout.BeginArea(area);

        // Bundle list (collapsible) when no bundle selected yet, or always show filters.
        EditorGUI.BeginChangeCheck();
        _search = EditorGUILayout.TextField("Search", _search);
        if (EditorGUI.EndChangeCheck()) ApplyFilters();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Type", GUILayout.Width(40));
        if (GUILayout.Button(_typeFilter.Length == 0 ? "(any)" : _typeFilter, EditorStyles.popup))
        {
            var dd = new TypeDropdown(_typeDdState, _types, picked => { _typeFilter = picked; ApplyFilters(); });
            dd.Show(GUILayoutUtility.GetLastRect());
        }
        int modeIdx = (int)_groupMode;
        int newIdx = GUILayout.Toolbar(modeIdx, new[] { "None", "Type", "Folder" }, EditorStyles.miniButton, GUILayout.Width(150));
        if (newIdx != modeIdx) { _groupMode = (GroupMode)newIdx; BuildDisplay(); Repaint(); }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(2);

        // Virtualized list
        Rect listArea = GUILayoutUtility.GetRect(10, 10000, 10, 100000,
            GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        float contentW = listArea.width - 16;
        Rect content = new Rect(0, 0, contentW, _display.Count * ROW_H);

        _listScroll = GUI.BeginScrollView(listArea, _listScroll, content);
        int first = Mathf.Max(0, Mathf.FloorToInt(_listScroll.y / ROW_H));
        int last = Mathf.Min(_display.Count, Mathf.CeilToInt((_listScroll.y + listArea.height) / ROW_H) + 1);
        Vector2 mouse = Event.current.mousePosition;

        string toggleKey = null;
        for (int i = first; i < last; i++)
        {
            var di = _display[i];
            Rect row = new Rect(0, i * ROW_H, contentW, ROW_H);
            float levelIndent = di.indent * 14f;

            if (di.isHeader)
            {
                EditorGUI.DrawRect(row, HEADER_BG);
                bool expanded = EditorGUI.Foldout(new Rect(row.x + 2 + levelIndent, row.y, row.width - 4 - levelIndent, row.height),
                    !_collapsed.Contains(di.collapseKey), $"{di.headerType}  ({di.count})", true, _headerStyle);
                if (expanded == _collapsed.Contains(di.collapseKey)) toggleKey = di.collapseKey;
                EditorGUI.DrawRect(new Rect(0, row.yMax - 1, contentW, 1), ROW_LINE);
                continue;
            }

            var d = di.desc;

            // Right-click context menu for import
            if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && row.Contains(mouse))
            {
                Event.current.Use();
                var desc = d;
                Select(desc);
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Import to project…"), false, () => Import(desc));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Copy name"), false, () =>
                {
                    GUIUtility.systemCopyBuffer = desc.name ?? "";
                });
                menu.AddItem(new GUIContent("Copy descriptor path"), false, () =>
                {
                    GUIUtility.systemCopyBuffer = desc.descriptor.FilePath ?? desc.descriptor.FileName ?? "";
                });
                menu.AddItem(new GUIContent("Copy GUID"), false, () =>
                {
                    // For a sub-asset this is the OWNING descriptor's GUID (sub-assets embedded in a
                    // *Collection/model don't have their own separate Amplitude GUID) — the same value
                    // AssetUtility/AssetDatabase resolve to at runtime, so it's what you actually want to
                    // paste into a *Ref GUID field (e.g. Tools/Pawn Fragment/Author Window's "MaterialRef GUID").
                    GUIUtility.systemCopyBuffer = desc.descriptor.Guid.ToString();
                });
                if (LoadAsset(desc) is MeshCollection mc)
                {
                    // The join key AnimationManager.GetMeshCollection actually looks up by is
                    // SourcePrefab (the rig/model prefab GUID this collection was baked from), NOT the
                    // collection asset's own GUID above — that's what you want to copy to reuse/override
                    // an existing skeleton's identity (see Tier1MeshBaker's "Force SourcePrefab GUID").
                    menu.AddItem(new GUIContent("Copy SourcePrefab GUID"), false, () =>
                    {
                        GUIUtility.systemCopyBuffer = mc.SourcePrefab.ToString();
                    });
                }
                menu.ShowAsContext();
            }

            bool selected = d == _selected;
            bool hover = row.Contains(mouse);
            if (selected)          EditorGUI.DrawRect(row, ROW_SEL);
            else if (hover)        EditorGUI.DrawRect(row, ROW_HOVER);
            else if ((i & 1) == 1) EditorGUI.DrawRect(row, ROW_ALT);

            bool showTypeColumn = _groupMode != GroupMode.Type;
            float typeW = showTypeColumn ? TYPE_COL_W : 0f;
            Rect nameRect = new Rect(row.x + 4 + levelIndent, row.y, row.width - 8 - levelIndent - typeW, row.height);
            string prefix = d.isSub ? "· " : "  ";
            if (GUI.Button(nameRect, new GUIContent(prefix + d.name, $"{d.typeName}{(d.isSub ? " (sub-asset)" : "")}"), EditorStyles.label))
                Select(d);

            if (showTypeColumn)
            {
                Rect typeRect = new Rect(row.xMax - TYPE_COL_W - 4, row.y, TYPE_COL_W, row.height);
                GUI.Label(typeRect, new GUIContent(d.typeName, d.typeName), _typeColStyle);
            }
            EditorGUI.DrawRect(new Rect(0, row.yMax - 1, contentW, 1), ROW_LINE);
        }
        GUI.EndScrollView();

        if (toggleKey != null)
        {
            if (_collapsed.Contains(toggleKey)) _collapsed.Remove(toggleKey);
            else _collapsed.Add(toggleKey);
            BuildDisplay();
            Repaint();
        }

        GUILayout.EndArea();
    }

    void DrawSplitter(Rect r)
    {
        EditorGUI.DrawRect(r, new Color(0, 0, 0, 0.3f));
        EditorGUIUtility.AddCursorRect(r, MouseCursor.ResizeHorizontal);
        var e = Event.current;
        if (e.type == EventType.MouseDown && r.Contains(e.mousePosition)) { _draggingSplit = true; e.Use(); }
        if (_draggingSplit && e.type == EventType.MouseDrag)
        {
            _leftWidth = Mathf.Clamp(e.mousePosition.x, 240f, position.width - 320f);
            Repaint(); e.Use();
        }
        if (e.type == EventType.MouseUp) _draggingSplit = false;
    }

    // ── Right: preview + inspector + import ───────────────────────────────────
    void DrawRight(Rect area)
    {
        GUILayout.BeginArea(area);

        if (_selected == null)
        {
            EditorGUILayout.HelpBox(
                "Pick a bundle from the toolbar, then select an asset on the left.\n" +
                "Right-click an item (or use the Import button below) to copy it into the project.",
                MessageType.Info);
            GUILayout.EndArea();
            return;
        }

        var obj = LoadAsset(_selected);
        var t = _selected.type;

        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label(_selected.name, EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Ping", EditorStyles.toolbarButton, GUILayout.Width(50)) && obj != null)
        { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
        EditorGUILayout.EndHorizontal();

        // Preview area: texture thumbnail or mesh render. Falls through to inspector.
        Rect previewArea = GUILayoutUtility.GetRect(10, 128, 10, 128, GUILayout.ExpandWidth(true));
        DrawPreview(previewArea, obj, t);

        EditorGUILayout.Space(4);

        // Import controls
        EditorGUILayout.LabelField("Import", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        _importFolder = EditorGUILayout.TextField("Folder", _importFolder);
        if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(ImportFolderKey, _importFolder);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Pick…", GUILayout.Width(60)))
        {
            string abs = EditorUtility.OpenFolderPanel("Import destination", "Assets", "");
            if (!string.IsNullOrEmpty(abs))
            {
                string proj = System.IO.Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
                string rel = FileUtil.GetProjectRelativePath(abs.Replace('\\', '/'));
                if (!string.IsNullOrEmpty(rel) && rel.StartsWith("Assets")) _importFolder = rel;
                else EditorUtility.DisplayDialog("Asset Explorer", "Choose a folder inside the project's Assets/ folder.", "OK");
            }
        }
        using (new EditorGUI.DisabledScope(obj == null))
        {
            if (GUILayout.Button("Import Asset")) Import(_selected);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(6);

        // Embedded inspector
        if (obj != null)
        {
            if (_editor == null || _editor.target != obj)
            {
                ClearEditor();
                _editor = Editor.CreateEditor(obj);
            }
            float prevLabel = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Clamp(area.width * 0.38f, 110f, 200f);
            _inspScroll = EditorGUILayout.BeginScrollView(_inspScroll);
            EditorGUILayout.BeginVertical(GUILayout.Width(area.width - 24));
            try { _editor.OnInspectorGUI(); }
            catch (Exception ex)
            {
                EditorGUILayout.HelpBox(
                    "Embedded inspector failed to render (often an Odin editor expecting the real " +
                    "Inspector). Use Ping to edit it in the docked Inspector.\n\n" + ex.Message,
                    MessageType.Warning);
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndScrollView();
            EditorGUIUtility.labelWidth = prevLabel;
        }
        else
        {
            EditorGUILayout.HelpBox("Asset could not be loaded from the bundle (it may be a type " +
                "the editor can't instantiate, or the provider is stale).", MessageType.Warning);
        }

        GUILayout.EndArea();
    }

    void DrawPreview(Rect r, UnityEngine.Object obj, Type type)
    {
        EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.35f));
        if (obj == null)
        {
            GUI.Label(r, "(not loadable)", EditorStyles.miniLabel);
            return;
        }

        // Texture2D / Sprite: show a readable thumbnail.
        if (obj is Texture2D tex)
        {
            EnsureTexPreview(tex);
            if (_texPreview != null)
            {
                float a = _texPreview.width / (float)_texPreview.height;
                Rect draw = FitAspect(r, a);
                GUI.DrawTexture(draw, _texPreview, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.Label(r, "Texture not readable (no PNG preview).", EditorStyles.miniLabel);
            }
            return;
        }
        if (obj is Sprite sprite && sprite.texture != null)
        {
            EnsureTexPreview(sprite.texture);
            if (_texPreview != null)
            {
                Rect draw = FitAspect(r, _texPreview.width / (float)_texPreview.height);
                GUI.DrawTexture(draw, _texPreview, ScaleMode.ScaleToFit);
            }
            return;
        }

        // Mesh: render with PreviewRenderUtility for a tumbling thumbnail, plus stats.
        if (obj is Mesh mesh)
        {
            DrawMeshPreview(r, mesh);
            return;
        }

        // Fallback: type + size label, since we have nothing visual to draw.
        GUI.Label(r, $"{type.Name}\n{obj.GetType().FullName}", EditorStyles.miniLabel);
    }

    static Rect FitAspect(Rect area, float aspect)
    {
        float a = area.width / area.height;
        Rect r;
        if (a > aspect)
        {
            float w = area.height * aspect;
            r = new Rect(area.x + (area.width - w) * 0.5f, area.y, w, area.height);
        }
        else
        {
            float h = area.width / aspect;
            r = new Rect(area.x, area.y + (area.height - h) * 0.5f, area.width, h);
        }
        return r;
    }

    // Make a readable copy of a (possibly GPU-only / non-readable) Texture2D so we can
    // EncodeToPNG on import and draw a thumbnail here. Uses RenderTexture blit so it works
    // even when the source has isReadable == false.
    void EnsureTexPreview(Texture2D src)
    {
        if (_texPreview != null && _texPreview.name == src.name) return;
        if (_texPreview != null) DestroyImmediate(_texPreview);
        try
        {
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            _texPreview = new Texture2D(src.width, src.height, TextureFormat.ARGB32, false);
            _texPreview.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            _texPreview.Apply();
            _texPreview.name = src.name;
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
        }
        catch (Exception e)
        {
            if (_texPreview != null) { DestroyImmediate(_texPreview); _texPreview = null; }
            Debug.LogWarning($"[AssetExplorer] preview blit failed for '{src.name}': {e.Message}");
        }
    }

    void DrawMeshPreview(Rect r, Mesh mesh)
    {
        // A full tumbling PreviewRenderUtility render needs a Material we can't reliably pull
        // from an arbitrary bundle, so we show informative stats instead. The mesh itself is
        // still importable via the Import button (cloned as a .asset) and viewable in the
        // docked Inspector / a model preview window.
        EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.45f));
        string stats = $"Mesh: {mesh.name}\nverts: {mesh.vertexCount}\nsubmeshes: {mesh.subMeshCount}\n" +
                       $"bounds: {mesh.bounds.size}";
        GUI.Label(r, stats, EditorStyles.miniLabel);
    }

    // ── Import ────────────────────────────────────────────────────────────────
    void Import(Desc d)
    {
        var obj = LoadAsset(d);
        if (obj == null) { Debug.LogError($"[AssetExplorer] cannot import '{d.name}' — asset not loadable."); return; }

        EnsureImportFolder();
        string safeName = SanitizeFileName(d.name);
        string dst = UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{_importFolder}/{safeName}");

        // Texture2D / Sprite -> PNG export (the most common thing people want out of the
        // asset explorer). We make a readable copy and write PNG bytes; Sprite reuses its
        // texture.
        if (obj is Texture2D tex)
        {
            ImportTextureAsPNG(tex, dst + ".png");
            return;
        }
        if (obj is Sprite sprite && sprite.texture != null)
        {
            ImportTextureAsPNG(sprite.texture, dst + ".png");
            return;
        }

        // GameObject (a model/prefab root) -> PrefabUtility.SaveAsPrefabAsset, NOT
        // AssetDatabase.CreateAsset (which only writes ScriptableObject/plain-Object .assets and
        // throws "Couldn't add object to asset file because 'X' is a GameObject!" otherwise).
        if (obj is GameObject go)
        {
            GameObject instance = null;
            try
            {
                instance = UnityEngine.Object.Instantiate(go);
                instance.name = go.name;
                var prefab = PrefabUtility.SaveAsPrefabAsset(instance, dst + ".prefab");
                Debug.Log($"[AssetExplorer] imported '{d.name}' ({d.typeName}) -> {dst}.prefab");
                EditorGUIUtility.PingObject(prefab);
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetExplorer] prefab import failed for '{d.name}': {e.Message}");
            }
            finally
            {
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            }
            return;
        }

        // Anything else: clone via Instantiate + CreateAsset (the same approach Probing.cs
        // step 3 validated for Amplitude ScriptableObjects). This produces a project asset
        // that survives AssetDatabase.Refresh and can be inspected/bundled like any mod asset.
        try
        {
            var clone = UnityEngine.Object.Instantiate(obj);
            clone.name = obj.name;
            // AssetDatabase.CreateAsset only accepts types Unity knows how to serialize to a
            // .asset file. ScriptableObjects and plain Objects work; MonoBehaviours inside a
            // bundle generally don't, and we report that cleanly instead of corrupting the db.
            UnityEditor.AssetDatabase.CreateAsset(clone, dst + ".asset");
            UnityEditor.AssetDatabase.ImportAsset(dst + ".asset");
            EditorUtility.SetDirty(clone);
            Debug.Log($"[AssetExplorer] imported '{d.name}' ({d.typeName}) -> {dst}.asset");
            EditorGUIUtility.PingObject(clone);
        }
        catch (Exception e)
        {
            Debug.LogError($"[AssetExplorer] clone+create failed for '{d.name}' ({d.typeName}): {e.Message}\n" +
                          "This type likely can't be authored outside Amplitude's import path. For meshes/textures use the PNG/Mesh import path.");
        }
    }

    void ImportTextureAsPNG(Texture2D src, string relPath)
    {
        // relPath is already a project-relative "Assets/.../X.png" path (built via
        // GenerateUniqueAssetPath in Import). Make sure its folder exists, then write the PNG.
        EnsureImportFolder();
        string dir = System.IO.Path.GetDirectoryName(relPath);
        if (!UnityEditor.AssetDatabase.IsValidFolder(dir)) EnsureFolder(dir);
        string rel = relPath;

        try
        {
            EnsureTexPreview(src);
            if (_texPreview == null) { Debug.LogError($"[AssetExplorer] cannot read texture '{src.name}' for PNG export."); return; }
            byte[] png = _texPreview.EncodeToPNG();
            if (png == null) { Debug.LogError($"[AssetExplorer] EncodeToPNG returned null for '{src.name}'."); return; }
            File.WriteAllBytes(rel, png);
            UnityEditor.AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceUpdate);
            Debug.Log($"[AssetExplorer] imported texture '{src.name}' -> {rel}");
            var imp = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
            if (imp != null) EditorGUIUtility.PingObject(imp);
        }
        catch (Exception e)
        {
            Debug.LogError($"[AssetExplorer] PNG export failed for '{src.name}': {e.Message}");
        }
    }

    void EnsureImportFolder()
    {
        if (UnityEditor.AssetDatabase.IsValidFolder(_importFolder)) return;
        EnsureFolder(_importFolder);
    }

    static void EnsureFolder(string folderPath)
    {
        if (UnityEditor.AssetDatabase.IsValidFolder(folderPath)) return;
        var parts = folderPath.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!UnityEditor.AssetDatabase.IsValidFolder(next)) UnityEditor.AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    static string SanitizeFileName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "asset";
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }

    // ── Dropdowns ─────────────────────────────────────────────────────────────
    class BundleDropdown : AdvancedDropdown
    {
        readonly List<BundleInfo> _bundles;
        readonly Action<string> _onPick;
        public BundleDropdown(AdvancedDropdownState state, List<BundleInfo> bundles, Action<string> onPick) : base(state)
        {
            _bundles = bundles; _onPick = onPick;
            minimumSize = new Vector2(320, 360);
        }
        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem("Bundles");
            if (_bundles == null || _bundles.Count == 0)
            {
                root.AddChild(new AdvancedDropdownItem("(no bundles — set Humankind folder)"));
                return root;
            }
            var byFolder = _bundles.GroupBy(b => b.folder).OrderBy(g => g.Key);
            foreach (var g in byFolder)
            {
                var f = new AdvancedDropdownItem(g.Key);
                foreach (var b in g)
                {
                    // The leaf's name is what ItemSelected receives; use the full "folder/file"
                    // as the name so the picker is unambiguous even if two folders share a file
                    // name. (AdvancedDropdownItem has no userData field, so the name IS the key.)
                    string key = b.folder + "/" + b.file;
                    f.AddChild(new AdvancedDropdownItem(key));
                }
                root.AddChild(f);
            }
            return root;
        }
        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            // Leaf items carry the full "folder/file" key; folder headers have children so
            // they're never selected directly. (hasChildren is non-public, so check the
            // public children enumerable instead.)
            if (item.children != null && item.children.Any()) return;
            _onPick(item.name);
        }
    }

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
