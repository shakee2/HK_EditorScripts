using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Groups recurring field diffs for Mass Change and orchestrates Patch-only applies.
    /// </summary>
    public static class MassChange
    {
        public class Candidate
        {
            public string Path;
            public DiffKind Kind;
            public List<string> Sources = new List<string>();
            public List<ElementRow> Elements = new List<ElementRow>();
            public string Preview;
            public string Action; // "skip" or source mod name
            public FieldApplier.ApplyKind ApplyKind;
            public string UnsupportedReason;
        }

        public class ApplyStats
        {
            public int Applied;
            public int SkippedAlready;
            public int SkippedNotInPatch;
            public int Failed;
            public int Unsupported;
        }

        /// <summary>
        /// Build candidate groups from conflict rows (typically the current filtered view or a selection).
        /// ExtraInWinner and Odin refs[] paths are omitted. Unsupported patterns are still listed so the UI can explain.
        /// </summary>
        public static List<Candidate> ComputeCandidates(IEnumerable<ElementRow> rows)
        {
            var byKey = new Dictionary<string, Candidate>(StringComparer.Ordinal);
            foreach (var row in rows ?? Enumerable.Empty<ElementRow>())
            {
                if (row?.Conflict == null) continue;
                var diffs = row.Conflict.Diffs;
                if (diffs == null || diffs.Count == 0) continue;

                // Nice-to-have: if only Odin refs[], try struct diffs from winner Body
                if (diffs.Count > 0 && diffs[0].Path != null && diffs[0].Path.StartsWith("refs[", StringComparison.Ordinal))
                {
                    if (row.Elements != null && row.Elements.TryGetValue(row.Winner, out var wEl) && wEl?.Body != null)
                    {
                        var losers = row.Elements.Where(kv => kv.Key != row.Winner)
                            .ToDictionary(kv => kv.Key, kv => kv.Value);
                        diffs = ConflictAnalyzer.ComputeDiffs(wEl, losers);
                    }
                }

                foreach (var d in diffs)
                {
                    if (d == null || d.Kind == DiffKind.ExtraInWinner) continue;
                    if (string.IsNullOrEmpty(d.Path) || d.Path.StartsWith("refs[", StringComparison.Ordinal))
                        continue;

                    string firstSrcVal = null;
                    var sources = new List<string>();
                    foreach (var kv in d.Values.Where(kv => kv.Key != "*winner*" && kv.Value != UnityYaml.Missing))
                    {
                        sources.Add(kv.Key);
                        firstSrcVal ??= kv.Value;
                    }
                    if (sources.Count == 0 && d.Values.TryGetValue("*winner*", out var wv) && wv != UnityYaml.Missing)
                        firstSrcVal = wv;

                    var parsed = FieldApplier.Parse(d.Path, d.Kind, firstSrcVal);
                    string key = d.Path + "|" + (int)d.Kind;
                    if (!byKey.TryGetValue(key, out var c))
                    {
                        c = new Candidate
                        {
                            Path = d.Path,
                            Kind = d.Kind,
                            Elements = new List<ElementRow>(),
                            Sources = sources.ToList(),
                            Preview = firstSrcVal,
                            Action = "skip",
                            ApplyKind = parsed.Kind,
                            UnsupportedReason = parsed.UnsupportedReason,
                        };
                        // Prefer default source = first loser with a value
                        byKey[key] = c;
                    }
                    else
                    {
                        foreach (var s in sources)
                            if (!c.Sources.Contains(s)) c.Sources.Add(s);
                    }
                    if (!c.Elements.Contains(row))
                        c.Elements.Add(row);
                }
            }

            return byKey.Values
                .OrderByDescending(c => c.Elements.Count)
                .ThenBy(c => c.Path, StringComparer.Ordinal)
                .ThenBy(c => (int)c.Kind)
                .ToList();
        }

        /// <summary>
        /// Apply selected candidates to Patch SOs only. Never imports.
        /// </summary>
        public static ApplyStats Apply(
            IEnumerable<Candidate> candidates,
            IReadOnlyDictionary<string, string> patchPathByName,
            HashSet<string> restrictToElementKeys = null,
            Func<ElementRow, string> elemKey = null)
        {
            var stats = new ApplyStats();
            elemKey ??= (r => r.Type + "|" + r.Name);

            var dirty = new HashSet<UnityEngine.Object>();
            try
            {
                foreach (var c in candidates ?? Enumerable.Empty<Candidate>())
                {
                    if (c == null || c.Action == "skip" || string.IsNullOrEmpty(c.Action)) continue;
                    if (c.ApplyKind == FieldApplier.ApplyKind.Unsupported)
                    {
                        stats.Unsupported += c.Elements.Count;
                        continue;
                    }

                    string sourceValue = ResolveSourceValue(c);
                    if (string.IsNullOrEmpty(sourceValue) || sourceValue == UnityYaml.Missing)
                    {
                        Debug.LogWarning($"[CompatPatcher] MassChange: no source value for '{c.Path}' from '{c.Action}'.");
                        stats.Failed += c.Elements.Count;
                        continue;
                    }

                    foreach (var row in c.Elements)
                    {
                        if (restrictToElementKeys != null && !restrictToElementKeys.Contains(elemKey(row)))
                            continue;

                        var patchObj = FieldApplier.FindPatchObject(row.Name, FriendlyTypeHint(row), patchPathByName);
                        if (patchObj == null)
                        {
                            stats.SkippedNotInPatch++;
                            continue;
                        }

                        // Prefer per-row value from that mod when present
                        string value = sourceValue;
                        if (row.Conflict?.Diffs != null)
                        {
                            var d = row.Conflict.Diffs.FirstOrDefault(x => x.Path == c.Path && x.Kind == c.Kind);
                            if (d != null && d.Values.TryGetValue(c.Action, out var v) && v != UnityYaml.Missing)
                                value = v;
                            else if (c.ApplyKind == FieldApplier.ApplyKind.AddRef)
                            {
                                var parsed = FieldApplier.Parse(c.Path, c.Kind, sourceValue);
                                if (!string.IsNullOrEmpty(parsed.EntryName))
                                    value = parsed.EntryName;
                            }
                        }

                        var result = FieldApplier.Apply(patchObj, c.Path, c.Kind, value);
                        if (result.Ok && result.Skipped) stats.SkippedAlready++;
                        else if (result.Ok)
                        {
                            stats.Applied++;
                            dirty.Add(patchObj);
                        }
                        else
                        {
                            stats.Failed++;
                            Debug.LogWarning($"[CompatPatcher] MassChange: {row.Name} @ {c.Path}: {result.Message}");
                        }
                    }
                }
            }
            finally
            {
                if (dirty.Count > 0)
                    UnityEditor.AssetDatabase.SaveAssets();
            }
            return stats;
        }

        static string ResolveSourceValue(Candidate c)
        {
            if (c.ApplyKind == FieldApplier.ApplyKind.AddRef)
            {
                var parsed = FieldApplier.Parse(c.Path, c.Kind, c.Preview);
                if (!string.IsNullOrEmpty(parsed.EntryName))
                    return parsed.EntryName;
            }
            // Value from first element that has this source mod on the path
            foreach (var row in c.Elements)
            {
                var d = row.Conflict?.Diffs?.FirstOrDefault(x => x.Path == c.Path && x.Kind == c.Kind);
                if (d != null && d.Values.TryGetValue(c.Action, out var v) && v != UnityYaml.Missing)
                    return v;
            }
            return c.Preview;
        }

        static string FriendlyTypeHint(ElementRow row) =>
            !string.IsNullOrEmpty(row.TypeHint) ? row.TypeHint : null;
    }
}
