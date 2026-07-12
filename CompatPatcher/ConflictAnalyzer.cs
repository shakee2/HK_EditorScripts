using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HK.CompatPatcher
{
    public enum DiffKind { MissingInWinner, Changed, ExtraInWinner }
    public enum ElemStatus { New, Identical, Conflict, Root }

    public class Diff
    {
        public string Path;
        public DiffKind Kind;
        public Dictionary<string, string> Values = new Dictionary<string, string>(); // mod -> value, plus "*winner*"
        public string Sig;
        public string Fp;
        public string Recommend;   // add | pick | keep
        public string Status;      // new | changed | carried | info
        public string Choice;      // carried decision
        public string PriorChoice; // when changed
    }

    public class ElementConflict
    {
        public string Type;
        public string Name;
        public string Winner;               // mod name (load-order last)
        public List<string> Contributors = new List<string>();
        public bool Odin;
        public List<Diff> Diffs = new List<Diff>();
        public bool NeedsReview => Diffs.Any(d => d.Status == "new" || d.Status == "changed");
    }

    public class ElementRow
    {
        public string Type;
        public string TypeHint;
        public string Name;
        public List<string> Contributors = new List<string>();
        public string Winner;
        public ElemStatus Status;
        public ElementConflict Conflict; // null unless Status == Conflict
        public string Summary;           // e.g. "2 ADD, 1 PICK"
        public Dictionary<string, HkElement> Elements; // mod name -> that mod's version (for Import & Edit)
    }

    public class AnalyzeStats
    {
        public int New, Roots, Identical, OdinConflicts, Conflicts;
    }

    public class AnalyzeResult
    {
        public List<ElementRow> Rows = new List<ElementRow>();
        public List<ElementConflict> Conflicts = new List<ElementConflict>();
        public AnalyzeStats Stats = new AnalyzeStats();
        public List<string> LoadOrder = new List<string>();
    }

    public static class ConflictAnalyzer
    {
        static readonly Regex AncestorRe = new Regex(@"^(.*\])", RegexOptions.Compiled);

        public static AnalyzeResult Analyse(List<HkMod> modsInLoadOrder, Dictionary<string, Sidecar.Decision> prior)
        {
            var res = new AnalyzeResult { LoadOrder = modsInLoadOrder.Select(m => m.Name).ToList() };
            var allKeys = new HashSet<string>();
            foreach (var m in modsInLoadOrder)
                foreach (var k in m.Elements.Keys) allKeys.Add(k);

            foreach (var key in allKeys)
            {
                var present = new List<(HkMod mod, HkElement el)>();
                foreach (var m in modsInLoadOrder)
                    if (m.Elements.TryGetValue(key, out var el)) present.Add((m, el));

                var winner = present[present.Count - 1];
                var row = new ElementRow
                {
                    Type = winner.el.Type,
                    TypeHint = winner.el.TypeHint,
                    Name = winner.el.Name,
                    Winner = winner.mod.Name,
                    Contributors = present.Select(p => p.mod.Name).ToList(),
                    Elements = present.ToDictionary(p => p.mod.Name, p => p.el),
                };

                if (present.Count < 2)
                {
                    row.Status = ElemStatus.New; res.Stats.New++; res.Rows.Add(row); continue;
                }
                if (winner.el.IsRoot)
                {
                    row.Status = ElemStatus.Root; res.Stats.Roots++; res.Rows.Add(row); continue;
                }

                var losers = new Dictionary<string, HkElement>();
                for (int i = 0; i < present.Count - 1; i++) losers[present[i].mod.Name] = present[i].el;

                List<Diff> diffs;
                bool odin = winner.el.Odin || losers.Values.Any(e => e.Odin) || winner.el.Body == null;
                if (odin) diffs = RefDiff(winner.el, losers);
                else diffs = CollapseMissing(StructDiff(winner.el, losers));

                if (diffs.Count == 0)
                {
                    row.Status = ElemStatus.Identical; res.Stats.Identical++; res.Rows.Add(row); continue;
                }

                var conflict = new ElementConflict
                {
                    Type = winner.el.Type, Name = winner.el.Name, Winner = winner.mod.Name,
                    Contributors = row.Contributors, Odin = winner.el.Odin, Diffs = diffs,
                };
                foreach (var d in diffs) AssignStatus(conflict, d, prior);

                res.Stats.Conflicts++;
                if (winner.el.Odin) res.Stats.OdinConflicts++;
                int add = diffs.Count(d => d.Kind == DiffKind.MissingInWinner);
                int pick = diffs.Count(d => d.Kind == DiffKind.Changed);
                row.Summary = string.Join(", ", new[] {
                    add > 0 ? add + " ADD" : null, pick > 0 ? pick + " PICK" : null
                }.Where(x => x != null));
                row.Status = ElemStatus.Conflict;
                row.Conflict = conflict;
                res.Rows.Add(row);
                res.Conflicts.Add(conflict);
            }

            res.Rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return res;
        }

        static Dictionary<string, string> Flat(HkElement e)
        {
            if (e.Flat == null) e.Flat = UnityYaml.Flatten(e.Body);
            return e.Flat;
        }

        static List<Diff> StructDiff(HkElement winner, Dictionary<string, HkElement> losers)
        {
            var wf = Flat(winner);
            var ofs = losers.ToDictionary(kv => kv.Key, kv => Flat(kv.Value));
            var paths = new HashSet<string>(wf.Keys);
            foreach (var m in ofs.Values) foreach (var p in m.Keys) paths.Add(p);

            var outp = new List<Diff>();
            foreach (var p in paths.OrderBy(x => x, StringComparer.Ordinal))
            {
                string wv = wf.TryGetValue(p, out var w) ? w : UnityYaml.Missing;
                var vals = new Dictionary<string, string>();
                bool anyDiff = false, anyPresentNonWinner = false, anyMissing = false, allInWinnerOrMissing = true;
                foreach (var kv in ofs)
                {
                    string v = kv.Value.TryGetValue(p, out var x) ? x : UnityYaml.Missing;
                    vals[kv.Key] = v;
                    if (v != wv) anyDiff = true;
                    if (v != UnityYaml.Missing) anyPresentNonWinner = true;
                    if (v == UnityYaml.Missing) anyMissing = true;
                    if (v != wv && v != UnityYaml.Missing) allInWinnerOrMissing = false;
                }
                if (!anyDiff) continue;

                DiffKind kind;
                if (wv == UnityYaml.Missing && anyPresentNonWinner) kind = DiffKind.MissingInWinner;
                else if (wv != UnityYaml.Missing && anyMissing && allInWinnerOrMissing) kind = DiffKind.ExtraInWinner;
                else kind = DiffKind.Changed;

                vals["*winner*"] = wv;
                outp.Add(new Diff { Path = p, Kind = kind, Values = vals });
            }
            return outp;
        }

        static List<Diff> RefDiff(HkElement winner, Dictionary<string, HkElement> losers)
        {
            var loserRefs = new HashSet<string>();
            foreach (var e in losers.Values) loserRefs.UnionWith(e.Refs);
            var outp = new List<Diff>();
            // refs a loser has that the winner lacks
            foreach (var r in loserRefs.Except(winner.Refs).OrderBy(x => x, StringComparer.Ordinal))
            {
                var vals = new Dictionary<string, string> { ["*winner*"] = UnityYaml.Missing };
                foreach (var kv in losers) if (kv.Value.Refs.Contains(r)) vals[kv.Key] = r;
                outp.Add(new Diff { Path = "refs[" + r + "]", Kind = DiffKind.MissingInWinner, Values = vals });
            }
            // refs the winner uniquely has (so the reviewer sees what they'd lose by switching)
            foreach (var r in winner.Refs.Except(loserRefs).OrderBy(x => x, StringComparer.Ordinal))
            {
                var vals = new Dictionary<string, string> { ["*winner*"] = r };
                foreach (var kv in losers) vals[kv.Key] = UnityYaml.Missing;
                outp.Add(new Diff { Path = "refs[" + r + "]", Kind = DiffKind.ExtraInWinner, Values = vals });
            }
            return outp;
        }

        static List<Diff> CollapseMissing(List<Diff> diffs)
        {
            var others = diffs.Where(d => d.Kind != DiffKind.MissingInWinner).ToList();
            var missing = diffs.Where(d => d.Kind == DiffKind.MissingInWinner)
                               .OrderBy(d => d.Path, StringComparer.Ordinal);
            var seen = new HashSet<string>();
            var merged = new List<Diff>();
            foreach (var d in missing)
            {
                var m = AncestorRe.Match(d.Path);
                string pref = m.Success ? m.Groups[1].Value : d.Path;
                if (!seen.Add(pref)) continue;
                var nd = new Diff { Path = pref, Kind = d.Kind, Values = d.Values };
                merged.Add(nd);
            }
            others.AddRange(merged);
            return others;
        }

        static void AssignStatus(ElementConflict c, Diff d, Dictionary<string, Sidecar.Decision> prior)
        {
            d.Sig = c.Type + "|" + c.Name + "|" + d.Path + "|" + d.Kind;
            d.Fp = Fingerprint(d.Values);
            d.Recommend = d.Kind == DiffKind.MissingInWinner ? "add"
                        : d.Kind == DiffKind.ExtraInWinner ? "keep" : "pick";

            Sidecar.Decision pd = null;
            prior?.TryGetValue(d.Sig, out pd);
            if (d.Kind == DiffKind.ExtraInWinner) d.Status = "info";
            else if (pd != null && pd.fp == d.Fp) { d.Status = "carried"; d.Choice = pd.choice; }
            else if (pd != null) { d.Status = "changed"; d.PriorChoice = pd.choice; }
            else d.Status = "new";
        }

        public static string Fingerprint(Dictionary<string, string> values)
        {
            var sb = new StringBuilder();
            foreach (var k in values.Keys.OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(k).Append('=').Append(values[k]).Append(';');
            using var sha = SHA1.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return BitConverter.ToString(h).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }
    }
}
