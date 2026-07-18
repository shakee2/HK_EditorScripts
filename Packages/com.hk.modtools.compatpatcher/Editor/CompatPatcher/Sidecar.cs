using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Companion metadata for incremental review: which conflicts you marked resolved (winner/choice OK
    /// without importing), plus a fingerprint of the source values at that time. Matching fingerprint on
    /// re-Compare → still resolved (hidden from "needs review"); mismatch → resurfaces as changed.
    /// Also stores the mod list (name + path, in load order) so reopening the sidecar refills Compare sources.
    /// Does not build Patch/ — import is separate.
    /// </summary>
    [Serializable]
    public class Sidecar
    {
        [Serializable]
        public class Decision
        {
            public string sig;       // type|name (ElemKey)
            public string fp;        // fingerprint of the source values when resolved
            public string choice;    // mod name accepted (usually load-order winner)
            public string kind;      // "element"
            public string element;   // element name (readability)
        }

        [Serializable]
        public class Source
        {
            public string name;
            public string path;
        }

        /// <summary>Mods in load order (last wins). Preferred over <see cref="loadOrder"/>.</summary>
        public List<Source> sources = new List<Source>();

        /// <summary>Legacy name-only list; kept for older sidecars and readability.</summary>
        public List<string> loadOrder = new List<string>();

        public List<Decision> decisions = new List<Decision>();

        public static Sidecar Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                return JsonUtility.FromJson<Sidecar>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CompatPatcher] Sidecar load failed: " + e.Message);
                return null;
            }
        }

        public static Dictionary<string, Decision> LoadIndex(string path)
        {
            var idx = new Dictionary<string, Decision>();
            var sc = Load(path);
            if (sc?.decisions != null)
                foreach (var d in sc.decisions)
                    if (!string.IsNullOrEmpty(d.sig)) idx[d.sig] = d;
            return idx;
        }

        public static void Save(string path, IList<Source> sources, IEnumerable<Decision> decisions)
        {
            var sc = new Sidecar();
            if (sources != null)
            {
                foreach (var s in sources)
                {
                    if (s == null || string.IsNullOrEmpty(s.name)) continue;
                    sc.sources.Add(new Source { name = s.name, path = s.path ?? "" });
                }
            }
            sc.loadOrder = sc.sources.Select(s => s.name).ToList();
            if (decisions != null) sc.decisions.AddRange(decisions);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonUtility.ToJson(sc, true));
        }

        /// <summary>
        /// Insert or replace one decision, keeping other decisions and the source list.
        /// Used for per-diff Resolve (kind=<c>diff</c>) without rewriting the whole review state.
        /// </summary>
        public static void UpsertDecision(string path, Decision decision)
        {
            if (string.IsNullOrEmpty(path) || decision == null || string.IsNullOrEmpty(decision.sig)) return;
            var sc = Load(path) ?? new Sidecar();
            var bySig = new Dictionary<string, Decision>();
            if (sc.decisions != null)
                foreach (var d in sc.decisions)
                    if (!string.IsNullOrEmpty(d.sig)) bySig[d.sig] = d;
            bySig[decision.sig] = decision;
            Save(path, sc.sources, bySig.Values);
        }

        public static string DefaultPath => PatchBuilder.PatchDir + "/CompatPatch.sidecar.json";
    }
}
