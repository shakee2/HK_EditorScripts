using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// The patch's companion metadata: the resolution decisions plus the fingerprint of the
    /// source values each was made against. On re-patch, a decision whose fingerprint still
    /// matches is carried (hidden); a changed one resurfaces for review.
    /// </summary>
    [Serializable]
    public class Sidecar
    {
        [Serializable]
        public class Decision
        {
            public string sig;       // type|name|path|kind
            public string fp;        // fingerprint of the source values when decided
            public string choice;    // "union" | mod name | "custom"
            public string kind;      // DiffKind
            public string element;   // element name (readability)
        }

        public List<string> loadOrder = new List<string>();
        public List<Decision> decisions = new List<Decision>();

        public static Dictionary<string, Decision> LoadIndex(string path)
        {
            var idx = new Dictionary<string, Decision>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return idx;
            try
            {
                var sc = JsonUtility.FromJson<Sidecar>(File.ReadAllText(path));
                if (sc?.decisions != null)
                    foreach (var d in sc.decisions)
                        if (!string.IsNullOrEmpty(d.sig)) idx[d.sig] = d;
            }
            catch (Exception e) { Debug.LogWarning("[CompatPatcher] Sidecar load failed: " + e.Message); }
            return idx;
        }

        public static void Save(string path, List<string> loadOrder, IEnumerable<Decision> decisions)
        {
            var sc = new Sidecar { loadOrder = new List<string>(loadOrder) };
            sc.decisions.AddRange(decisions);
            File.WriteAllText(path, JsonUtility.ToJson(sc, true));
        }
    }
}
