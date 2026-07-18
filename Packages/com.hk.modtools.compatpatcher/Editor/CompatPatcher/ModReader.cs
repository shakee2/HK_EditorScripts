using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Amplitude.Framework;
using Amplitude.Framework.Asset;
using UnityEditor;
using UnityEngine;
using AssetDatabase = Amplitude.Framework.Asset.AssetDatabase;

namespace HK.CompatPatcher
{
    /// <summary>One database element flattened out of a mod, keyed by name (path-agnostic).</summary>
    public class HkElement
    {
        public string Name;                 // m_Name
        public string Type;                 // m_Script guid:fileID (concrete element class)
        public string TypeHint;             // source file stem (friendly label, e.g. TechnologyDefinition)
        public string SourcePath;           // logical path inside the mod (info only)
        public bool IsRoot;                 // collection container object (m_Name == file stem)
        public bool Odin;                   // payload in Odin node stream
        public List<string> DocLines;       // raw YAML document (for verbatim copy on PICK)
        public Dictionary<string, object> Body;       // parsed field tree (null if opaque)
        public HashSet<string> Refs;        // referenced element names
        public Dictionary<string, string> Flat;       // lazily-filled path -> value
        /// <summary>Live mounted object for assetbundle sources (no YAML staging needed).</summary>
        public UnityEngine.Object LiveObject;
        public string Key => Type + "" + Name;
    }

    /// <summary>A loaded mod: element map keyed by (type,name).</summary>
    public class HkMod
    {
        public string Name;
        public string Path;
        public int FileCount;
        public bool FromAssetBundle;
        public Dictionary<string, HkElement> Elements = new Dictionary<string, HkElement>();
        public Dictionary<string, string> RawFiles = new Dictionary<string, string>(); // logical path -> full .asset text (for staging)
    }

    public static class ModReader
    {
        public static HkMod Load(string modName, string path)
        {
            var mod = new HkMod { Name = modName, Path = path };
            string ext = System.IO.Path.GetExtension(path)?.ToLowerInvariant();
            if (ext == ".assetbundle")
            {
                LoadFromAssetBundle(mod, path);
                return mod;
            }

            foreach (var (logicalPath, text) in EnumerateDatabaseAssets(path))
            {
                mod.FileCount++;
                mod.RawFiles[logicalPath] = text;
                foreach (var el in ParseElements(text, logicalPath))
                    AddElement(mod, el);
            }
            return mod;
        }

        static void AddElement(HkMod mod, HkElement el)
        {
            if (el == null || string.IsNullOrEmpty(el.Name)) return;
            if (mod.Elements.TryGetValue(el.Key, out var prior))
            {
                // Deterministic in-mod last-wins: keep lexicographically greater SourcePath
                int cmp = string.Compare(el.SourcePath, prior.SourcePath, StringComparison.Ordinal);
                if (cmp < 0) return;
                if (cmp == 0)
                {
                    // Same collection path: FetchAllSubAssets can yield the *identical* Unity object twice —
                    // that is noise, keep last silently. Two distinct objects (or YAML docs) with the same
                    // name in one .asset are real in-file duplicates (even if byte-identical) — warn.
                    bool sameLiveInstance = prior.LiveObject != null && el.LiveObject != null
                                            && ReferenceEquals(prior.LiveObject, el.LiveObject);
                    if (!sameLiveInstance)
                    {
                        string typeName = ElementTypeLabel(el, prior);
                        Debug.LogWarning(
                            $"[CompatPatcher] Duplicate element '{el.Name}' (type {typeName}, collection {el.TypeHint}) "
                            + $"in mod '{mod.Name}': defined twice in '{el.SourcePath}' — keeping latter.");
                    }
                    mod.Elements[el.Key] = el;
                    return;
                }
                {
                    string typeName = ElementTypeLabel(el, prior);
                    Debug.LogWarning(
                        $"[CompatPatcher] Duplicate element '{el.Name}' (type {typeName}, collection {el.TypeHint}) "
                        + $"in mod '{mod.Name}': '{prior.SourcePath}' vs '{el.SourcePath}' — keeping latter.");
                }
            }
            mod.Elements[el.Key] = el;
        }

        static string ElementTypeLabel(HkElement el, HkElement prior)
        {
            string typeName = el.LiveObject != null ? el.LiveObject.GetType().Name
                : (prior?.LiveObject != null ? prior.LiveObject.GetType().Name : null);
            return string.IsNullOrEmpty(typeName) ? (el.TypeHint ?? "?") : typeName;
        }

        static void LoadFromAssetBundle(HkMod mod, string path)
        {
            mod.FromAssetBundle = true;
            var provider = CompatBundleMounts.EnsureMounted(path, mod.Name);
            var descriptors = new List<AssetDescriptor>();
            provider.AddAllAssetDescriptors(descriptors, AssetProviderOption.AskForType);
            foreach (var d in descriptors.OrderBy(x => x.FileName ?? "", StringComparer.Ordinal)
                                         .ThenBy(x => x.FilePath ?? "", StringComparer.Ordinal))
            {
                var collectionType = d.GetAssetType();
                if (collectionType == null || !typeof(DatatableElementCollection).IsAssignableFrom(collectionType))
                    continue;
                var collection = provider.LoadAsset<DatatableElementCollection>(d);
                if (collection == null) continue;
                collection.Initialize();
                var elementType = collection.DatatableElementType;
                if (elementType == null) continue;

                string logicalPath = NormalizeBundleDatabasePath(d.FilePath, d.FileName);
                string stem = System.IO.Path.GetFileNameWithoutExtension(logicalPath);
                mod.FileCount++;

                var collectionEl = LiveElementBuilder.Build(collection, logicalPath, stem);
                if (collectionEl != null) AddElement(mod, collectionEl);

                var elements = provider.FetchAllSubAssetsOfType(d.Guid, elementType)
                    .Where(o => o is IDatatableElement && o != collection)
                    .OrderBy(o => o.name, StringComparer.Ordinal)
                    .ToArray();
                foreach (var element in elements)
                {
                    if (element is IDatatableElement de) de.Initialize();
                    var el = LiveElementBuilder.Build(element, logicalPath, stem);
                    if (el != null) AddElement(mod, el);
                }
            }
        }

        // ---- source enumeration -------------------------------------------
        public static IEnumerable<(string path, string text)> EnumerateDatabaseAssets(string path)
        {
            if (Directory.Exists(path))
            {
                foreach (var fp in Directory.EnumerateFiles(path, "*.asset", SearchOption.AllDirectories))
                {
                    var norm = fp.Replace('\\', '/');
                    if (norm.Contains("/Assets/Databases/"))
                        yield return (norm, File.ReadAllText(fp));
                }
                yield break;
            }
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".zip")
            {
                using var fs = File.OpenRead(path);
                using var z = new ZipArchive(fs, ZipArchiveMode.Read);
                foreach (var e in z.Entries)
                {
                    var n = e.FullName.Replace('\\', '/');
                    if (n.EndsWith(".asset") && n.Contains("/Assets/Databases/"))
                        yield return (n, ReadEntry(e));
                }
            }
            else if (ext == ".unitypackage")
            {
                foreach (var kv in ReadUnityPackageDatabaseAssets(path))
                    yield return (kv.Key, kv.Value);
            }
            else if (ext == ".assetbundle")
            {
                throw new InvalidDataException(
                    "EnumerateDatabaseAssets does not support .assetbundle (use ModReader.Load, which mounts in-memory).");
            }
            else
            {
                throw new InvalidDataException("Unsupported mod source (use .unitypackage, .zip, .assetbundle, or a folder): " + path);
            }
        }

        static string ReadEntry(ZipArchiveEntry e)
        {
            using var s = e.Open();
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }

        static string NormalizeBundleDatabasePath(string filePath, string fileName)
        {
            string p = (filePath ?? "").Replace('\\', '/');
            if (p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) return p;
            string name = string.IsNullOrEmpty(fileName) ? "BundleCollection.asset" : fileName;
            if (!name.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) name += ".asset";
            return "Assets/Databases/AssetBundle/" + name;
        }

        /// <summary>Deep-clone a mounted bundle object for Import when a mutable copy is needed.</summary>
        public static UnityEngine.Object CloneFromBundle(UnityEngine.Object source)
        {
            if (source == null) return null;
            var dest = ScriptableObject.CreateInstance(source.GetType());
            dest.name = source.name;
            var correctScript = MonoScript.FromScriptableObject(dest);
            if (correctScript == null)
            {
                var monos = Resources.FindObjectsOfTypeAll<MonoScript>();
                foreach (var m in monos)
                {
                    if (m.GetClass() == source.GetType())
                    { correctScript = m; break; }
                }
            }
            EditorUtility.CopySerialized(source, dest);
            if (correctScript != null)
            {
                using var so = new SerializedObject(dest);
                var p = so.FindProperty("m_Script");
                if (p != null)
                {
                    p.objectReferenceValue = correctScript;
                    so.ApplyModifiedProperties();
                }
            }
            return dest;
        }

        // ---- .unitypackage = gzip(tar of <guid>/{pathname,asset,asset.meta}) ----
        static IEnumerable<KeyValuePair<string, string>> ReadUnityPackageDatabaseAssets(string path)
        {
            byte[] tar;
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                gz.CopyTo(ms);
                tar = ms.ToArray();
            }

            var pathnames = new Dictionary<string, string>();     // guid -> logical path
            var assets = new Dictionary<string, byte[]>();        // guid -> asset bytes
            foreach (var (name, data) in ReadTar(tar))
            {
                int slash = name.IndexOf('/');
                if (slash <= 0) continue;
                string guid = name.Substring(0, slash);
                string leaf = name.Substring(slash + 1);
                if (leaf == "pathname")
                {
                    var p = Encoding.UTF8.GetString(data).Trim();
                    int nl = p.IndexOfAny(new[] { '\r', '\n' });
                    if (nl >= 0) p = p.Substring(0, nl);
                    pathnames[guid] = p.Trim();
                }
                else if (leaf == "asset")
                {
                    assets[guid] = data;
                }
            }

            foreach (var kv in pathnames)
            {
                var p = kv.Value.Replace('\\', '/');
                if (p.EndsWith(".asset") && p.Contains("Assets/Databases/") && assets.TryGetValue(kv.Key, out var bytes))
                    yield return new KeyValuePair<string, string>(p, Encoding.UTF8.GetString(bytes));
            }
        }

        // Minimal USTAR reader (unitypackage uses short entry names, no GNU extensions).
        static IEnumerable<(string name, byte[] data)> ReadTar(byte[] tar)
        {
            int pos = 0;
            while (pos + 512 <= tar.Length)
            {
                // name (0..100)
                int end = 0; while (end < 100 && tar[pos + end] != 0) end++;
                string name = Encoding.UTF8.GetString(tar, pos, end);
                if (name.Length == 0) yield break; // two zero blocks terminate

                // size (124..136) octal
                long size = 0;
                for (int i = 124; i < 124 + 12; i++)
                {
                    byte b = tar[pos + i];
                    if (b == 0 || b == ' ') continue;
                    if (b < '0' || b > '7') break;
                    size = size * 8 + (b - '0');
                }
                char typeFlag = (char)tar[pos + 156];
                pos += 512;

                if ((typeFlag == '0' || typeFlag == '\0') && size > 0 && pos + size <= tar.Length)
                {
                    var data = new byte[size];
                    Array.Copy(tar, pos, data, 0, size);
                    yield return (name, data);
                }
                pos += (int)((size + 511) / 512) * 512; // advance past data, padded to 512
            }
        }

        // ---- split a .asset into elements ---------------------------------
        public static List<HkElement> ParseElements(string text, string sourcePath)
        {
            var outp = new List<HkElement>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            string stem = System.IO.Path.GetFileNameWithoutExtension(sourcePath);

            var starts = new List<int>();
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].StartsWith("--- !u!")) starts.Add(i);
            starts.Add(lines.Length);

            for (int s = 0; s < starts.Count - 1; s++)
            {
                var doc = new List<string>();
                for (int i = starts[s]; i < starts[s + 1]; i++) doc.Add(lines[i]);

                string name = null, type = "?";
                foreach (var l in doc)
                {
                    var t = l.TrimStart();
                    if (name == null && t.StartsWith("m_Name:"))
                        name = l.Substring(l.IndexOf(':') + 1).Trim();
                    else if (t.StartsWith("m_Script:"))
                    {
                        // m_Script: {fileID: 123, guid: abc..., type: 3}
                        var fid = Extract(t, "fileID:");
                        var guid = Extract(t, "guid:");
                        if (guid != null) type = guid + ":" + (fid ?? "0");
                    }
                }
                if (name == null) continue;

                var body = UnityYaml.ParseElementBody(doc);
                outp.Add(new HkElement
                {
                    Name = name,
                    Type = type,
                    TypeHint = stem,
                    SourcePath = sourcePath,
                    IsRoot = name == stem || UnityYaml.IsContainerBody(body),
                    Body = body,
                    Odin = UnityYaml.IsOdinPayload(body),
                    DocLines = doc,
                    Refs = UnityYaml.CollectRefs(doc),
                });
            }
            return outp;
        }

        static string Extract(string s, string token)
        {
            int i = s.IndexOf(token, StringComparison.Ordinal);
            if (i < 0) return null;
            i += token.Length;
            while (i < s.Length && s[i] == ' ') i++;
            int j = i;
            while (j < s.Length && s[j] != ',' && s[j] != '}' && s[j] != ' ') j++;
            return s.Substring(i, j - i).Trim();
        }
    }
}
