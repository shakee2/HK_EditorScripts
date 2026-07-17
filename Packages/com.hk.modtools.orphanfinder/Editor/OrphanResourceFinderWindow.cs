// OrphanResourceFinderWindow.cs
// ---------------------------------------------------------------------------
// Editor tool for Humankind / Amplitude Unity mods.
// Finds unreferenced ("orphaned") assets (images by default, or 3D/mesh resources) in a
// review and delete them, shrinking your published mod package.
//
// WHY THIS IS NEEDED
//   Images in a Unity "Resources" folder are force-included in the built mod
//   whether or not anything uses them. Amplitude references a texture by a
//   4-integer {a,b,c,d} GUID struct inside .asset files (UI mappers / presentation
//   defs) - NOT by Unity's hex GUID or by file name - so leftovers from reworks
//   silently bloat the download.
//
// HOW IT WORKS
//   * Collects every {a,b,c,d} reference from source .asset/.prefab/.unity files
//     (excluding the built AssetBundles folder, which would hide real orphans).
//   * Maps each image's Unity GUID into that {a,b,c,d} form. The byte encoding is
//     AUTO-CALIBRATED each scan (tries several byte orders, keeps the one that
//     matches the most real references), so it stays correct across projects.
//   * Optional file-name cross-check keeps any image whose name appears as text
//     in source (covers images loaded by name via Resources.Load).
//
// RESOURCE KINDS
//   * Images (default)  — .png/.jpg/.tga/.psd/... , the original behaviour.
//   * 3D / meshes       — baked assets (.asset/.prefab/.mat) + raw models
//                         (.fbx/.obj/.glb/.gltf/.blend/.mesh).
//   * All non-script    — both of the above.
//   For the 3D/All kinds the reference scan ALSO reads JSON int[4] GUID arrays
//   (e.g. "skel":[a,b,c,d] in a mod's model registry), since baked meshes /
//   skeletons / atlases are frequently referenced from JSON rather than from a
//   .asset. Reference detection is deliberately GENEROUS — a false "referenced"
//   only costs you unreclaimed space, while a false "orphan" could delete a used
//   asset — so review the list and keep version control before deleting.
//
// USAGE:  Tools > shakee's Tools > Orphan Resource Finder
//   Deletion moves files (and their .meta) to the OS Trash/Recycle Bin (recoverable).
//   Editor-only - never included in a build. Always use version control / a backup.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public class OrphanResourceFinderWindow : EditorWindow
{
    static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".dds", ".bmp", ".gif", ".tif", ".tiff" };
    // Baked-mesh & model candidate types. .asset/.prefab/.mat are ALSO reference sources (an asset can reference others
    // and be referenced) — that's fine; only truly unreferenced ones surface.
    static readonly string[] ModelExt = { ".asset", ".prefab", ".mat", ".fbx", ".obj", ".glb", ".gltf", ".blend", ".mesh" };
    static readonly Regex RefRx = new Regex(
        @"guid:\s*a:\s*(-?\d+)\s*b:\s*(-?\d+)\s*c:\s*(-?\d+)\s*d:\s*(-?\d+)", RegexOptions.Compiled);
    // Amplitude {a,b,c,d} GUIDs stored as a 4-int JSON array (e.g. "skel":[a,b,c,d] in a model registry). Whitespace/
    // newlines between elements are allowed (pretty-printed JSON). Deliberately broad — over-matching keeps assets, which
    // is the safe direction for an orphan finder.
    static readonly Regex JsonQuadRx = new Regex(
        @"\[\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*\]", RegexOptions.Compiled);
    // Unity-native reference: `{fileID: N, guid: <32 hex>, type: N}` in YAML asset/prefab/material files.
    static readonly Regex UnityGuidRx = new Regex(@"guid:\s*([0-9a-fA-F]{32})\b", RegexOptions.Compiled);

    enum ResourceKind { Images, Models3D, All }

    string _imagesFolder = "Assets/Resources";
    ResourceKind _kind = ResourceKind.Images;
    bool _nameCrossCheck = true;
    Vector2 _scroll;
    string _status = "Set a folder and press Scan.";
    string _info = "";
    readonly List<Row> _rows = new List<Row>();
    long _totalBytes;

    class Row { public string Path; public long Size; public bool Sel; }

    [MenuItem("Tools/shakee's Tools/Orphan Resource Finder")]
    static void Open()
    {
        var w = GetWindow<OrphanResourceFinderWindow>("Orphan Resources");
        w.minSize = new Vector2(600, 380);
    }

    void OnGUI()
    {
        // --- toolbar ---
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label("Resources folder", GUILayout.Width(100));
            _imagesFolder = GUILayout.TextField(_imagesFolder, EditorStyles.toolbarTextField, GUILayout.Width(230));
            if (GUILayout.Button("…", EditorStyles.toolbarButton, GUILayout.Width(24)))
            {
                string abs = EditorUtility.OpenFolderPanel("Folder to scan", Application.dataPath, "");
                if (!string.IsNullOrEmpty(abs)) _imagesFolder = ToAssetPath(abs);
            }
            if (GUILayout.Button("Scan", EditorStyles.toolbarButton, GUILayout.Width(60))) Scan();
            GUILayout.Label("Kind", GUILayout.Width(32));
            _kind = (ResourceKind)EditorGUILayout.EnumPopup(_kind, EditorStyles.toolbarPopup, GUILayout.Width(90));
            _nameCrossCheck = GUILayout.Toggle(_nameCrossCheck, "Name cross-check", EditorStyles.toolbarButton, GUILayout.Width(120));
            GUILayout.FlexibleSpace();
        }

        EditorGUILayout.HelpBox(_status + (string.IsNullOrEmpty(_info) ? "" : "\n" + _info),
            _rows.Count > 0 ? MessageType.Info : MessageType.None);

        // --- selection controls ---
        if (_rows.Count > 0)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select all", GUILayout.Width(80))) _rows.ForEach(r => r.Sel = true);
                if (GUILayout.Button("None", GUILayout.Width(55))) _rows.ForEach(r => r.Sel = false);
                if (GUILayout.Button("Invert", GUILayout.Width(60))) _rows.ForEach(r => r.Sel = !r.Sel);
                GUILayout.FlexibleSpace();
                GUILayout.Label(_rows.Count(r => r.Sel) + " selected", EditorStyles.miniLabel);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var r in _rows)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    r.Sel = EditorGUILayout.Toggle(r.Sel, GUILayout.Width(18));
                    GUILayout.Label(SizeStr(r.Size), GUILayout.Width(72));
                    if (GUILayout.Button(r.Path, EditorStyles.label))
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(r.Path);
                        if (obj) { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            // --- footer ---
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(string.Format("Total {0} files, {1}  |  selected {2}",
                    _rows.Count, SizeStr(_totalBytes), SizeStr(_rows.Where(r => r.Sel).Sum(r => r.Size))),
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Export list…", EditorStyles.toolbarButton, GUILayout.Width(90))) Export();
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.55f, 0.55f);
                if (GUILayout.Button("Delete selected → Trash", EditorStyles.toolbarButton, GUILayout.Width(170))) DeleteSelected();
                GUI.backgroundColor = prev;
            }
        }
    }

    void Scan()
    {
        try
        {
            if (!AssetDatabase.IsValidFolder(_imagesFolder))
            { _status = "Not a valid project folder: " + _imagesFolder; _info = ""; _rows.Clear(); return; }

            string root = Application.dataPath;
            EditorUtility.DisplayProgressBar("Orphan Resource Finder", "Collecting references…", 0.15f);

            bool scan3D = _kind != ResourceKind.Images;
            var candExt = CandidateExt();
            string kindLabel = _kind == ResourceKind.Images ? "images" : _kind == ResourceKind.Models3D ? "3D resources" : "resources";

            // 1) referenced {a,b,c,d} set (Amplitude FakeAssetReference format), + in 3D/All the Unity hex-GUID refs.
            // Exclude the built bundle (it embeds every asset, hiding real orphans). One read per source file.
            var used = new HashSet<string>();
            var usedHex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // Unity `guid: <hex32>` refs (3D safety)
            var serFiles = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(p => (p.EndsWith(".asset") || p.EndsWith(".prefab") || p.EndsWith(".unity") ||
                             (scan3D && p.EndsWith(".mat"))) && !IsExcluded(p));
            foreach (var f in serFiles)
            {
                string t = File.ReadAllText(f);
                foreach (Match m in RefRx.Matches(t))
                    used.Add(m.Groups[1].Value + "|" + m.Groups[2].Value + "|" + m.Groups[3].Value + "|" + m.Groups[4].Value);
                // Unity-native references (a prefab -> its mesh/material, a material -> its atlas) are hex GUIDs, NOT the
                // Amplitude {a,b,c,d} form — catch them too so 3D mode never flags a mesh a prefab actually uses.
                if (scan3D)
                    foreach (Match m in UnityGuidRx.Matches(t)) usedHex.Add(m.Groups[1].Value);
            }

            // 1b) 3D/All: baked meshes/skeletons/atlases are often referenced from JSON registries as int[4] GUID arrays
            // rather than from a .asset — read those too. (Generous: over-matching keeps assets, the safe direction.)
            if (scan3D)
            {
                EditorUtility.DisplayProgressBar("Orphan Resource Finder", "Collecting JSON GUID references…", 0.3f);
                var jsonFiles = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Where(p => !IsExcluded(p));
                foreach (var f in jsonFiles)
                    foreach (Match m in JsonQuadRx.Matches(File.ReadAllText(f)))
                        used.Add(m.Groups[1].Value + "|" + m.Groups[2].Value + "|" + m.Groups[3].Value + "|" + m.Groups[4].Value);
            }

            // 2) candidate resources + their Unity GUIDs
            EditorUtility.DisplayProgressBar("Orphan Resource Finder", "Gathering " + kindLabel + "…", 0.45f);
            string imgAbs = ToAbsPath(_imagesFolder);
            var imgGuid = new Dictionary<string, string>(); // assetPath -> hex guid
            foreach (var p in Directory.GetFiles(imgAbs, "*.*", SearchOption.AllDirectories))
            {
                if (p.EndsWith(".meta") || !candExt.Contains(Path.GetExtension(p).ToLowerInvariant())) continue;
                string ap = ToAssetPath(p);
                string g = AssetDatabase.AssetPathToGUID(ap);
                if (!string.IsNullOrEmpty(g)) imgGuid[ap] = g;
            }
            if (imgGuid.Count == 0) { _status = "No " + kindLabel + " found in " + _imagesFolder; _info = ""; _rows.Clear(); return; }

            // 3) auto-calibrate the GUID encoding
            EditorUtility.DisplayProgressBar("Orphan Resource Finder", "Calibrating…", 0.65f);
            var encoders = Encoders();
            string best = null; int bestScore = -1;
            foreach (var kv in encoders)
            {
                int s = imgGuid.Values.Count(h => used.Contains(kv.Value(HexToBytes(h))));
                if (s > bestScore) { bestScore = s; best = kv.Key; }
            }
            var enc = encoders[best];
            _info = string.Format("Encoding '{0}' matched {1}/{2} {3} as referenced.", best, bestScore, imgGuid.Count, kindLabel);

            // 4) orphans = referenced by NEITHER the Amplitude {a,b,c,d} form (calibrated) NOR — in 3D/All — a Unity hex ref.
            var orphans = imgGuid.Where(kv => !used.Contains(enc(HexToBytes(kv.Value)))
                                           && !(scan3D && usedHex.Contains(kv.Value)))
                                 .Select(kv => kv.Key).ToList();

            // 5) name cross-check (keep anything whose name appears in source text)
            if (_nameCrossCheck && orphans.Count > 0)
            {
                EditorUtility.DisplayProgressBar("Orphan Resource Finder", "Name cross-check…", 0.85f);
                var still = new HashSet<string>(orphans);
                var nameOf = orphans.ToDictionary(p => p, p => Path.GetFileNameWithoutExtension(p));
                var textFiles = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(p => (p.EndsWith(".asset") || p.EndsWith(".prefab") || p.EndsWith(".unity") ||
                                 p.EndsWith(".cs") || p.EndsWith(".json") || p.EndsWith(".xml") || p.EndsWith(".txt")) && !IsExcluded(p));
                // A candidate .asset/.prefab is itself a text file containing its own m_Name — reading it would self-match
                // and keep every such asset. Skip a candidate's OWN file when testing its name (.meta files aren't read).
                foreach (var f in textFiles)
                {
                    if (still.Count == 0) break;
                    string fap = ToAssetPath(f);
                    string t = File.ReadAllText(f);
                    foreach (var p in still.ToList())
                        if (fap != p && t.Contains(nameOf[p])) still.Remove(p);
                }
                orphans = orphans.Where(still.Contains).ToList();
            }

            _rows.Clear();
            foreach (var p in orphans.OrderBy(p => p))
                _rows.Add(new Row { Path = p, Size = new FileInfo(ToAbsPath(p)).Length, Sel = true });
            _totalBytes = _rows.Sum(r => r.Size);
            _status = string.Format("{0} unreferenced {1} ({2}). Review, then delete or export.", _rows.Count, kindLabel, SizeStr(_totalBytes));
            if (scan3D) _status += "  (3D mode: verify against re-bake needs before deleting.)";
            if (bestScore < imgGuid.Count / 2)
                _status += "  WARNING: under 50% matched - results may be unreliable, review carefully.";
        }
        catch (Exception e) { _status = "Error: " + e.Message; }
        finally { EditorUtility.ClearProgressBar(); }
    }

    void DeleteSelected()
    {
        var sel = _rows.Where(r => r.Sel).ToList();
        if (sel.Count == 0) { _status = "Nothing selected."; return; }
        if (!EditorUtility.DisplayDialog("Delete orphaned resources",
            string.Format("Move {0} file(s) ({1}) and their .meta to the Trash/Recycle Bin?\n\nRecoverable from the Recycle Bin.",
                sel.Count, SizeStr(sel.Sum(r => r.Size))), "Delete", "Cancel")) return;

        int ok = 0;
        foreach (var r in sel) if (AssetDatabase.MoveAssetToTrash(r.Path)) ok++;
        AssetDatabase.Refresh();
        _rows.RemoveAll(r => r.Sel);
        _totalBytes = _rows.Sum(r => r.Size);
        _status = string.Format("Moved {0} file(s) to Trash. {1} remaining. Rebuild your mod to capture the size saving.", ok, _rows.Count);
    }

    void Export()
    {
        string path = EditorUtility.SaveFilePanel("Export orphan list",
            Directory.GetParent(Application.dataPath).FullName, "unreferenced-images", "txt");
        if (string.IsNullOrEmpty(path)) return;
        var sb = new StringBuilder();
        sb.AppendLine("# Unreferenced images: " + _rows.Count + ", " + SizeStr(_totalBytes));
        sb.AppendLine("# " + _info);
        sb.AppendLine();
        foreach (var r in _rows) sb.AppendLine(string.Format("{0,10}  {1}", SizeStr(r.Size), r.Path));
        File.WriteAllText(path, sb.ToString());
        _status = "Exported to " + path;
    }

    // ---------- helpers ----------
    string[] CandidateExt()
    {
        switch (_kind)
        {
            case ResourceKind.Models3D: return ModelExt;
            case ResourceKind.All:      return ImageExt.Concat(ModelExt).ToArray();
            default:                    return ImageExt;
        }
    }

    static bool IsExcluded(string p)
    {
        p = p.Replace('\\', '/');
        return p.Contains("/AssetBundles/") || p.Contains("/Library/") || p.Contains("/Temp/");
    }

    static string ToAssetPath(string full)
    {
        full = full.Replace('\\', '/');
        string dp = Application.dataPath.Replace('\\', '/'); // ".../Assets"
        return full.StartsWith(dp) ? "Assets" + full.Substring(dp.Length) : full;
    }

    static string ToAbsPath(string assetPath)
    {
        string dp = Application.dataPath.Replace('\\', '/');           // ".../Assets"
        string projRoot = dp.Substring(0, dp.Length - "Assets".Length); // ".../"
        return projRoot + assetPath;
    }

    static byte[] HexToBytes(string h)
    {
        var b = new byte[16];
        for (int i = 0; i < 16; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
        return b;
    }
    static byte[] Nibble(byte[] b)
    {
        var r = new byte[16];
        for (int i = 0; i < 16; i++) r[i] = (byte)(((b[i] << 4) | (b[i] >> 4)) & 0xFF);
        return r;
    }
    static byte[] Reverse(byte[] b) { var r = (byte[])b.Clone(); Array.Reverse(r); return r; }
    static string Quad(byte[] b)
    {
        return BitConverter.ToInt32(b, 0) + "|" + BitConverter.ToInt32(b, 4) + "|" +
               BitConverter.ToInt32(b, 8) + "|" + BitConverter.ToInt32(b, 12);
    }
    static string QuadGroupRev(byte[] b)
    {
        Func<int, int> R = o => BitConverter.ToInt32(new byte[] { b[o + 3], b[o + 2], b[o + 1], b[o] }, 0);
        return R(0) + "|" + R(4) + "|" + R(8) + "|" + R(12);
    }

    static Dictionary<string, Func<byte[], string>> Encoders()
    {
        return new Dictionary<string, Func<byte[], string>>
        {
            { "LE",                 b => Quad(b) },
            { "FullReverse",        b => Quad(Reverse(b)) },
            { "NibbleSwap",         b => Quad(Nibble(b)) },
            { "NibbleSwap+Reverse", b => Quad(Reverse(Nibble(b))) },
            { "GroupReverse",       b => QuadGroupRev(b) },
        };
    }

    static string SizeStr(long bytes)
    {
        if (bytes >= 1048576) return (bytes / 1048576.0).ToString("0.00") + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
        return bytes + " B";
    }
}
