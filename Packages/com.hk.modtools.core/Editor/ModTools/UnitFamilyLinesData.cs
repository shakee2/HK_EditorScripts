using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Amplitude.Framework;
using Amplitude.Framework.Asset;
using Amplitude.Framework.Utility;
using UnityEditor;
using UnityEngine;
using AmpAssetDatabase = Amplitude.Framework.Asset.AssetDatabase;

/// <summary>
/// Data layer for the Unit Family Lines browser: UnitFamilyDefinition upgrade DAG
/// (SerializableNextFamilyName) plus UnitDefinitions grouped by SerializableFamily.
/// Layout columns = longest-path depth from roots. Reflection by named fields only.
/// </summary>
public static class UnitFamilyLinesData
{
    const BindingFlags ALL =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public const string DatabasesRoot = "Assets/Databases";
    public const string NewAdditionsPath = "Assets/Databases/New Additions";
    const string FamilyPrefix = "UnitFamily_";
    const string VanillaProviderName = "mercurydatabases.assetbundle";
    const string TranslationsProviderName = "mercury.modding.translations.assetbundle";

    public enum UnitDomain { Land, Naval, Air, Other }
    public enum AssetSource { Project, Vanilla, Mounted }

    public class UnitRow
    {
        public string Name;
        public int Level;
        public UnitDomain Domain;
        public string FamilyName;
        public UnityEngine.Object ProjectDef;
        public UnityEngine.Object VanillaDef;
        public UnityEngine.Object MountedDef;
        public UnityEngine.Object ActiveDef => ProjectDef ?? MountedDef ?? VanillaDef;
        public AssetSource Source =>
            ProjectDef != null ? AssetSource.Project :
            MountedDef != null ? AssetSource.Mounted : AssetSource.Vanilla;
        public bool IsModded => ProjectDef != null;
        public UnityEngine.Object Asset => ActiveDef;
        // Legacy aliases used by older call sites
        public UnityEngine.Object ModDef { get => ProjectDef; set => ProjectDef = value; }
    }

    public class FamilyNode
    {
        public string Name;
        public string Label;           // display (prefix stripped)
        public bool IsObsolete;
        public string NextName;        // empty = terminal; may point at missing family
        public bool NextMissing;       // NextName set but no family node
        public string[] PreviousNames = Array.Empty<string>();
        public UnitDomain Domain = UnitDomain.Other;

        public UnityEngine.Object ProjectDef;
        public UnityEngine.Object VanillaDef;
        public UnityEngine.Object MountedDef;
        public UnityEngine.Object ActiveDef => ProjectDef ?? MountedDef ?? VanillaDef;
        public AssetSource Source =>
            ProjectDef != null ? AssetSource.Project :
            MountedDef != null ? AssetSource.Mounted : AssetSource.Vanilla;
        public bool IsModded => ProjectDef != null;
        public bool IsBrokenStub;      // synthetic node for a missing Next target
        public UnityEngine.Object Asset => ActiveDef;
        public UnityEngine.Object ModDef { get => ProjectDef; set => ProjectDef = value; }

        public List<UnitRow> Units = new();

        // Layout: Layer = depth (top→bottom); Row = index within domain band.
        public int Layer;
        public int Row;
        public float LayoutX, LayoutY;
    }

    public class Graph
    {
        public List<FamilyNode> Families = new();
        public Dictionary<string, FamilyNode> ByName =
            new(StringComparer.OrdinalIgnoreCase);
        public string Error;
        /// <summary>Domain band extents in layout units (for section labels/separators).
        /// MinY/MaxY are precomputed here so the draw loop never rescans Families per repaint.</summary>
        public List<(UnitDomain Domain, float X0, float X1, float MinY, float MaxY)> DomainBands = new();
    }

    public static Graph Build()
    {
        var graph = new Graph();
        var familyType = FindType("Amplitude.Mercury.Data.Simulation.UnitFamilyDefinition");
        var unitType = FindType("Amplitude.Mercury.Data.Simulation.UnitDefinition");
        if (familyType == null)
        {
            graph.Error = "UnitFamilyDefinition type not found (game assemblies not loaded).";
            Debug.LogError($"[UnitFamilyLines] {graph.Error}");
            return graph;
        }
        if (unitType == null)
        {
            graph.Error = "UnitDefinition type not found (game assemblies not loaded).";
            Debug.LogError($"[UnitFamilyLines] {graph.Error}");
            return graph;
        }

        VanillaDatabaseMount.TryMount(out _);

        var dbFam = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in LoadProjectOfType(familyType))
            if (o != null && !string.IsNullOrEmpty(o.name)) dbFam[o.name] = o;

        var refFam = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in VanillaDatabaseMount.LoadAllOfType(familyType))
            if (o != null && !string.IsNullOrEmpty(o.name) && !refFam.ContainsKey(o.name))
                refFam[o.name] = o;

        var mountedFam = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
        IndexOtherMountedProviders(familyType, mountedFam);

        var names = new HashSet<string>(dbFam.Keys, StringComparer.OrdinalIgnoreCase);
        names.UnionWith(refFam.Keys);
        names.UnionWith(mountedFam.Keys);

        foreach (var name in names)
        {
            var node = new FamilyNode
            {
                Name = name,
                Label = DisplayLabel(name),
                VanillaDef = refFam.TryGetValue(name, out var rv) ? rv : null,
                MountedDef = mountedFam.TryGetValue(name, out var mv) ? mv : null,
                ProjectDef = dbFam.TryGetValue(name, out var dv) ? dv : null,
            };
            var def = node.ActiveDef;
            if (def != null)
            {
                node.IsObsolete = GetBool(def, "IsObsolete");
                node.NextName = NormalizeNext(GetString(def, "SerializableNextFamilyName"));
            }
            graph.Families.Add(node);
            graph.ByName[name] = node;
        }

        // Broken Next stubs + reverse previous index (shared with incremental edits).
        RebuildLinks(graph);

        // Units by SerializableFamily
        var unitsByFamily = new Dictionary<string, List<UnitRow>>(StringComparer.OrdinalIgnoreCase);

        var dbUnits = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in LoadProjectOfType(unitType))
            if (o != null && !string.IsNullOrEmpty(o.name)) dbUnits[o.name] = o;

        var refUnits = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in VanillaDatabaseMount.LoadAllOfType(unitType))
            if (o != null && !string.IsNullOrEmpty(o.name) && !refUnits.ContainsKey(o.name))
                refUnits[o.name] = o;

        var mountedUnits = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
        IndexOtherMountedProviders(unitType, mountedUnits);

        var unitNames = new HashSet<string>(dbUnits.Keys, StringComparer.OrdinalIgnoreCase);
        unitNames.UnionWith(refUnits.Keys);
        unitNames.UnionWith(mountedUnits.Keys);
        foreach (var uname in unitNames)
        {
            var project = dbUnits.TryGetValue(uname, out var mu) ? mu : null;
            var van = refUnits.TryGetValue(uname, out var vu) ? vu : null;
            var mounted = mountedUnits.TryGetValue(uname, out var xu) ? xu : null;
            var active = project ?? mounted ?? van;
            if (active == null) continue;
            var family = GetString(active, "SerializableFamily");
            if (string.IsNullOrEmpty(family)) continue;
            // Unit namespace only — skip accidental SettlementImprovement family strings.
            if (!family.StartsWith(FamilyPrefix, StringComparison.OrdinalIgnoreCase) &&
                !graph.ByName.ContainsKey(family))
                continue;

            var row = new UnitRow
            {
                Name = uname,
                Level = GetInt(active, "Level"),
                Domain = DomainFromUnit(active),
                FamilyName = family,
                VanillaDef = van,
                MountedDef = mounted,
                ProjectDef = project,
            };
            if (!unitsByFamily.TryGetValue(family, out var list))
                unitsByFamily[family] = list = new List<UnitRow>();
            list.Add(row);
        }

        foreach (var fam in graph.Families)
        {
            if (unitsByFamily.TryGetValue(fam.Name, out var list))
            {
                fam.Units = list;
                SortUnits(fam);
            }
        }

        AssignDomains(graph);
        AssignLayout(graph);
        return graph;
    }

    /// <summary>
    /// Recomputes everything derived purely from the families' <c>NextName</c> links: the
    /// synthetic broken-Next stub nodes, each node's <c>NextMissing</c> flag, and the reverse
    /// <c>PreviousNames</c> index. Shared by <see cref="Build"/> and the incremental
    /// <see cref="SetFamilyNext"/> so both produce identical structure without an asset rescan.
    /// </summary>
    static void RebuildLinks(Graph graph)
    {
        // Stubs are derived — drop and rebuild ByName from the real families only.
        graph.Families.RemoveAll(f => f.IsBrokenStub);
        graph.ByName.Clear();
        foreach (var f in graph.Families)
        {
            f.NextMissing = false;
            f.PreviousNames = Array.Empty<string>();
            graph.ByName[f.Name] = f;
        }

        foreach (var fam in graph.Families.ToList())
        {
            if (string.IsNullOrEmpty(fam.NextName)) continue;
            if (graph.ByName.ContainsKey(fam.NextName)) continue;
            fam.NextMissing = true;
            var stub = new FamilyNode
            {
                Name = fam.NextName,
                Label = DisplayLabel(fam.NextName) + " (missing)",
                IsBrokenStub = true,
                NextMissing = true,
            };
            graph.Families.Add(stub);
            graph.ByName[stub.Name] = stub;
        }

        var prev = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fam in graph.Families)
        {
            if (fam.IsBrokenStub || string.IsNullOrEmpty(fam.NextName)) continue;
            if (!prev.TryGetValue(fam.NextName, out var list))
                prev[fam.NextName] = list = new List<string>();
            list.Add(fam.Name);
        }
        foreach (var fam in graph.Families)
        {
            if (prev.TryGetValue(fam.Name, out var list))
            {
                list.Sort(StringComparer.OrdinalIgnoreCase);
                fam.PreviousNames = list.ToArray();
            }
        }
    }

    /// <summary>
    /// Retarget a family's <c>Next</c> link entirely in-memory and reflow the DAG (links, stubs,
    /// domains, layout) — the same result a full <see cref="Build"/> would give for a Next edit,
    /// minus the AssetDatabase / asset-bundle rescans. The field write + SaveAssets happen on the
    /// caller side. No-op for broken-stub nodes.
    /// </summary>
    public static void SetFamilyNext(Graph graph, FamilyNode node, string nextName)
    {
        if (graph == null || node == null || node.IsBrokenStub) return;
        node.NextName = NormalizeNext(nextName);
        RebuildLinks(graph);
        AssignDomains(graph);
        AssignLayout(graph);
    }

    static void SortUnits(FamilyNode fam)
    {
        fam.Units.Sort((a, b) =>
        {
            int c = a.Level.CompareTo(b.Level);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Reassign a unit to another family entirely in-memory and reflow the DAG. Produces
    /// the same result a full <see cref="Build"/> would for a family edit, but skips the
    /// expensive AssetDatabase / asset-bundle rescans (the field write + SaveAssets have
    /// already happened on the caller side). Returns the target node, or null if invalid.
    /// </summary>
    public static FamilyNode MoveUnitToFamily(Graph graph, UnitRow row, string newFamilyName)
    {
        if (graph == null || row == null || string.IsNullOrEmpty(newFamilyName)) return null;
        if (!graph.ByName.TryGetValue(newFamilyName, out var target) || target.IsBrokenStub) return null;

        if (!string.IsNullOrEmpty(row.FamilyName) &&
            graph.ByName.TryGetValue(row.FamilyName, out var old))
            old.Units.Remove(row);
        else
            foreach (var f in graph.Families) f.Units.Remove(row);

        row.FamilyName = newFamilyName;
        if (!target.Units.Contains(row)) target.Units.Add(row);
        SortUnits(target);

        // Family membership can shift either family's majority domain → reflow both.
        AssignDomains(graph);
        AssignLayout(graph);
        return target;
    }

    /// <summary>
    /// Update a unit's level in-memory and re-sort its family. Level never affects the
    /// domain or layout, so no reflow (and no rescan) is needed.
    /// </summary>
    public static void UpdateUnitLevel(Graph graph, UnitRow row, int level)
    {
        if (graph == null || row == null) return;
        row.Level = level;
        if (!string.IsNullOrEmpty(row.FamilyName) &&
            graph.ByName.TryGetValue(row.FamilyName, out var node))
            SortUnits(node);
    }

    static void AssignDomains(Graph graph)
    {
        foreach (var fam in graph.Families)
        {
            if (fam.Units.Count > 0)
                fam.Domain = MajorityDomain(fam.Units.Select(u => u.Domain));
            else
                fam.Domain = GuessDomainFromName(fam.Name);
        }

        // Propagate along links so empty/stub families match their chain.
        for (int pass = 0; pass < 4; pass++)
        {
            foreach (var fam in graph.Families)
            {
                if (fam.Domain != UnitDomain.Other && fam.Units.Count > 0) continue;
                var votes = new List<UnitDomain>();
                foreach (var p in fam.PreviousNames)
                    if (graph.ByName.TryGetValue(p, out var pn) && pn.Domain != UnitDomain.Other)
                        votes.Add(pn.Domain);
                if (!string.IsNullOrEmpty(fam.NextName) &&
                    graph.ByName.TryGetValue(fam.NextName, out var next) &&
                    next.Domain != UnitDomain.Other)
                    votes.Add(next.Domain);
                if (votes.Count > 0)
                    fam.Domain = MajorityDomain(votes);
            }
        }
    }

    static UnitDomain MajorityDomain(IEnumerable<UnitDomain> domains)
    {
        var arr = domains.Where(d => d != UnitDomain.Other).ToList();
        if (arr.Count == 0) return UnitDomain.Other;
        return arr.GroupBy(d => d).OrderByDescending(g => g.Count())
            .ThenBy(g => DomainOrder(g.Key)).First().Key;
    }

    static int DomainOrder(UnitDomain d) => d switch
    {
        UnitDomain.Land => 0,
        UnitDomain.Naval => 1,
        UnitDomain.Air => 2,
        _ => 3,
    };

    // AIUnitTags flags on UnitDefinition (Ground=1, Naval=2, Air=4, AircraftCarrier=0x20000).
    static UnitDomain DomainFromUnit(object unit)
    {
        var tagsObj = unit.GetType().GetField("UnitTags", ALL)?.GetValue(unit);
        int tags = 0;
        try
        {
            if (tagsObj is Enum)
                tags = Convert.ToInt32(tagsObj);
            else if (tagsObj is int i)
                tags = i;
        }
        catch { /* ignore */ }

        const int Ground = 1, Naval = 2, Air = 4, AircraftCarrier = 0x20000;
        if ((tags & Naval) != 0 || (tags & AircraftCarrier) != 0) return UnitDomain.Naval;
        if ((tags & Air) != 0) return UnitDomain.Air;
        if ((tags & Ground) != 0) return UnitDomain.Land;

        var uref = unit.GetType().GetField("UnitClass", ALL)?.GetValue(unit);
        string className = ResolveReferenceName(uref);
        if (!string.IsNullOrEmpty(className))
        {
            var g = GuessDomainFromName(className);
            if (g != UnitDomain.Other) return g;
        }
        return GuessDomainFromName(unit is UnityEngine.Object o ? o.name : "");
    }

    static string ResolveReferenceName(object reference)
    {
        if (reference == null) return "";
        return reference.GetType().GetField("serializableElementName", ALL)?.GetValue(reference) as string ?? "";
    }

    static UnitDomain GuessDomainFromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return UnitDomain.Other;
        string n = name;
        // Land before bare "Air" so AntiAircraft stays Land.
        if (ContainsAny(n, "AntiAircraft", "AntiAir"))
            return UnitDomain.Land;
        if (ContainsAny(n, "Warship", "Naval", "Ship", "Corvette", "Carrier", "Transport"))
            return UnitDomain.Naval;
        if (ContainsAny(n, "Aircraft", "Bomber", "Fighter", "Multirole", "Missile"))
            return UnitDomain.Air;
        if (ContainsAny(n, "Infantry", "Cavalry", "Tank", "Artillery", "Gun", "Melee", "Ranged",
                "Militia", "Horde", "Siege", "AntiTank", "Animal", "Settler", "Spy", "Agent",
                "Nomad", "Officer", "StealthInfantry"))
            return UnitDomain.Land;
        return UnitDomain.Other;
    }

    static bool ContainsAny(string hay, params string[] needles)
    {
        foreach (var n in needles)
            if (hay.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    static void AssignLayout(Graph graph)
    {
        // As-late-as-possible depth shared across the whole graph (same vertical scale).
        var distToSink = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var fam in graph.Families)
            distToSink[fam.Name] = 0;

        bool changed = true;
        int guard = 0;
        while (changed && guard++ < graph.Families.Count + 2)
        {
            changed = false;
            foreach (var fam in graph.Families)
            {
                if (string.IsNullOrEmpty(fam.NextName)) continue;
                if (!distToSink.TryGetValue(fam.NextName, out var childDist))
                    childDist = 0;
                int d = childDist + 1;
                if (d > distToSink[fam.Name])
                {
                    distToSink[fam.Name] = d;
                    changed = true;
                }
            }
        }

        foreach (var fam in graph.Families)
            if (fam.IsBrokenStub) distToSink[fam.Name] = 0;

        int maxDist = 0;
        foreach (var d in distToSink.Values)
            if (d > maxDist) maxDist = d;

        foreach (var fam in graph.Families)
        {
            if (IsSingleton(fam))
                fam.Layer = -1; // packed in the singleton strip above the linked DAG
            else
                fam.Layer = maxDist - distToSink[fam.Name];
        }

        // Linked DAG top→bottom per domain (side-by-side bands). Singletons (no pre/next)
        // sit in a wrapped strip ABOVE each domain's upgrade graph.
        const float layerGap = 2.35f;
        const float rowGap = 3.55f;
        const float domainGap = 2.8f;
        const float singletonSep = 2.4f; // clear gap so the divider doesn't clip node bottoms
        const int singletonColsMax = 5;

        graph.DomainBands.Clear();
        float bandX = 0f;
        foreach (UnitDomain domain in new[] { UnitDomain.Land, UnitDomain.Naval, UnitDomain.Air, UnitDomain.Other })
        {
            var members = graph.Families.Where(f => f.Domain == domain).ToList();
            if (members.Count == 0) continue;

            var singletons = members
                .Where(IsSingleton)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var linked = members.Where(f => !IsSingleton(f)).ToList();

            void PackLinkedLayer(IGrouping<int, FamilyNode> group, IEnumerable<FamilyNode> ordered, float x0)
            {
                int i = 0;
                foreach (var fam in ordered)
                {
                    fam.Row = i;
                    fam.LayoutX = x0 + i * rowGap;
                    fam.LayoutY = group.Key * layerGap;
                    i++;
                }
            }

            if (linked.Count > 0)
            {
                var byLayer = linked.GroupBy(f => f.Layer).OrderBy(g => g.Key).ToList();
                foreach (var group in byLayer)
                {
                    PackLinkedLayer(group, group
                        .OrderBy(f => f.IsBrokenStub ? 1 : 0)
                        .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase), bandX);
                }

                for (int pass = 0; pass < 4; pass++)
                {
                    foreach (var group in byLayer)
                    {
                        float Score(FamilyNode fam)
                        {
                            float sum = 0f;
                            int n = 0;
                            foreach (var pName in fam.PreviousNames)
                            {
                                if (!graph.ByName.TryGetValue(pName, out var p)) continue;
                                if (p.Domain != domain || IsSingleton(p)) continue;
                                sum += p.LayoutX;
                                n++;
                            }
                            if (!string.IsNullOrEmpty(fam.NextName) &&
                                graph.ByName.TryGetValue(fam.NextName, out var child) &&
                                child.Domain == domain && !IsSingleton(child))
                            {
                                sum += child.LayoutX;
                                n++;
                            }
                            return n > 0 ? sum / n : fam.LayoutX;
                        }

                        PackLinkedLayer(group, group
                            .OrderBy(f => f.IsBrokenStub ? 1 : 0)
                            .ThenBy(Score)
                            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase), bandX);
                    }
                }
            }

            float linkedWidth = linked.Count > 0
                ? linked.Max(f => f.LayoutX) - bandX + rowGap
                : singletonColsMax * rowGap;
            int cols = Mathf.Clamp(
                Mathf.Max(1, Mathf.RoundToInt(linkedWidth / rowGap)),
                1, singletonColsMax);
            if (singletons.Count > 0 && linked.Count == 0)
                cols = Mathf.Min(singletonColsMax, Mathf.Max(1, singletons.Count));

            int singletonRows = singletons.Count == 0 ? 0 : (singletons.Count + cols - 1) / cols;
            for (int i = 0; i < singletons.Count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                singletons[i].Row = i;
                singletons[i].LayoutX = bandX + col * rowGap;
                singletons[i].LayoutY = -singletonSep - (singletonRows - 1 - row) * layerGap;
            }

            float maxX = members.Max(f => f.LayoutX) + rowGap;
            float minY = members.Min(f => f.LayoutY);
            float maxY = members.Max(f => f.LayoutY);
            graph.DomainBands.Add((domain, bandX, maxX, minY, maxY));
            bandX = maxX + domainGap;
        }
    }

    static bool IsSingleton(FamilyNode f) =>
        !f.IsBrokenStub && f.PreviousNames.Length == 0 && string.IsNullOrEmpty(f.NextName);

    public static string ExportJson(Graph graph)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"families\": [\n");
        var fams = graph.Families
            .Where(f => !f.IsBrokenStub)
            .OrderBy(f => DomainOrder(f.Domain))
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (int i = 0; i < fams.Count; i++)
        {
            var f = fams[i];
            sb.Append("    {\n");
            sb.Append("      \"name\": ").Append(JsonString(f.Name)).Append(",\n");
            sb.Append("      \"domain\": ").Append(JsonString(f.Domain.ToString())).Append(",\n");
            sb.Append("      \"obsolete\": ").Append(f.IsObsolete ? "true" : "false").Append(",\n");
            sb.Append("      \"previous\": [");
            for (int p = 0; p < f.PreviousNames.Length; p++)
            {
                if (p > 0) sb.Append(", ");
                sb.Append(JsonString(f.PreviousNames[p]));
            }
            sb.Append("],\n");
            sb.Append("      \"next\": ").Append(
                string.IsNullOrEmpty(f.NextName) ? "null" : JsonString(f.NextName)).Append(",\n");
            sb.Append("      \"units\": [\n");
            for (int u = 0; u < f.Units.Count; u++)
            {
                var unit = f.Units[u];
                sb.Append("        { \"name\": ").Append(JsonString(unit.Name))
                  .Append(", \"level\": ").Append(unit.Level).Append(" }");
                if (u < f.Units.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("      ]\n");
            sb.Append("    }");
            if (i < fams.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    static string JsonString(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32) sb.AppendFormat("\\u{0:x4}", (int)c);
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static IEnumerable<string> ForwardChain(Graph graph, string focusName)
    {
        if (graph == null || string.IsNullOrEmpty(focusName)) yield break;
        var guard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cur = focusName;
        while (!string.IsNullOrEmpty(cur) && guard.Add(cur))
        {
            yield return cur;
            if (!graph.ByName.TryGetValue(cur, out var n) || string.IsNullOrEmpty(n.NextName))
                yield break;
            cur = n.NextName;
        }
    }

    static string DisplayLabel(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        return name.StartsWith(FamilyPrefix, StringComparison.OrdinalIgnoreCase)
            ? name.Substring(FamilyPrefix.Length)
            : name;
    }

    static string NormalizeNext(string next)
    {
        if (string.IsNullOrEmpty(next)) return "";
        if (next.Equals("None", StringComparison.OrdinalIgnoreCase)) return "";
        return next.Trim();
    }

    static IEnumerable<UnityEngine.Object> LoadProjectOfType(Type t)
    {
        foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:ScriptableObject"))
        {
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) ||
                !(path == DatabasesRoot || path.StartsWith(DatabasesRoot + "/", StringComparison.Ordinal)))
                continue;
            foreach (var o in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && t.IsInstanceOfType(o))
                    yield return o;
        }
    }

    static void IndexOtherMountedProviders(Type type, Dictionary<string, UnityEngine.Object> map)
    {
        try
        {
            foreach (var provider in AmpAssetDatabase.AllProviders)
            {
                if (provider == null) continue;
                string pname = provider.Name ?? "";
                if (pname.Equals(VanillaProviderName, StringComparison.OrdinalIgnoreCase)) continue;
                if (pname.Equals(TranslationsProviderName, StringComparison.OrdinalIgnoreCase)) continue;
                // ProjectAssets is backed by on-disk/scene project objects; Amplitude's
                // FetchAllSubAssetsOfType runs a threaded read on it that logs "Do not use
                // ReadObjectThreaded on scene objects!". Project assets are already covered by
                // LoadProjectOfType, so skip that provider (redundant here and only warns).
                if (provider.GetType().Name == "ProjectAssets") continue;

                try
                {
                    var descriptors = new List<AssetDescriptor>();
                    provider.AddAllAssetDescriptors(descriptors, AssetProviderOption.AskForType);
                    foreach (var descriptor in descriptors)
                    {
                        var assetType = descriptor.GetAssetType();
                        if (assetType != null && type.IsAssignableFrom(assetType))
                        {
                            var obj = provider.LoadAsset<UnityEngine.Object>(descriptor);
                            if (obj != null && !string.IsNullOrEmpty(obj.name) && !map.ContainsKey(obj.name))
                                map[obj.name] = obj;
                        }
                        foreach (var sub in provider.FetchAllSubAssetsOfType(descriptor.Guid, type))
                            if (sub != null && !string.IsNullOrEmpty(sub.name) && !map.ContainsKey(sub.name))
                                map[sub.name] = sub;
                    }
                }
                catch
                {
                    // Stale / broken provider — skip.
                }
            }
        }
        catch { /* AllProviders unavailable */ }
    }

    static bool UnderDatabases(UnityEngine.Object o)
    {
        if (o == null) return false;
        var p = UnityEditor.AssetDatabase.GetAssetPath(o);
        return !string.IsNullOrEmpty(p) &&
               (p == DatabasesRoot || p.StartsWith(DatabasesRoot + "/", StringComparison.Ordinal));
    }

    // ── Write / import helpers ────────────────────────────────────────────────

    public static string ScopeLabel(AssetSource s) => s switch
    {
        AssetSource.Project => "mod",
        AssetSource.Mounted => "mounted",
        _ => "vanilla",
    };

    public static UnityEngine.Object EnsureWritableUnit(UnitRow row)
    {
        if (row == null) return null;
        if (row.ProjectDef != null && UnderDatabases(row.ProjectDef)) return row.ProjectDef;
        var source = row.ActiveDef;
        if (source == null) return null;
        var lifted = LiftElement(source);
        if (lifted != null) row.ProjectDef = lifted;
        return lifted;
    }

    public static UnityEngine.Object EnsureWritableFamily(FamilyNode node)
    {
        if (node == null || node.IsBrokenStub) return null;
        if (node.ProjectDef != null && UnderDatabases(node.ProjectDef)) return node.ProjectDef;
        var source = node.ActiveDef;
        if (source == null) return null;
        var lifted = LiftElement(source);
        if (lifted != null) node.ProjectDef = lifted;
        return lifted;
    }

    static UnityEngine.Object LiftElement(UnityEngine.Object source)
    {
        if (source == null) return null;
        if (UnderDatabases(source)) return source;

        if (VanillaDatabaseMount.IsVanillaAsset(source) ||
            VanillaDatabaseMount.TryGetOwnerDescriptor(source, out _))
        {
            var ov = VanillaDatabaseMount.OverrideVanillaElement(source);
            if (ov != null) return ov;
        }

        // Mounted (or vanilla without cached owner): duplicate into New Additions collection.
        return DuplicateIntoNewAdditions(source);
    }

    static UnityEngine.Object DuplicateIntoNewAdditions(UnityEngine.Object live)
    {
        if (live == null) return null;
        try
        {
            if (live is not IDatatableElement element)
            {
                Debug.LogError($"[UnitFamilyLines] '{live.name}' is not IDatatableElement — cannot import.");
                return null;
            }
            Type colType = null;
            DatatableElementCollectionUtility.TryGetCollectionTypeFromElementType(live.GetType(), ref colType);
            if (colType == null)
            {
                Debug.LogError($"[UnitFamilyLines] No collection type for {live.GetType().Name}.");
                return null;
            }
            EnsureFolder(NewAdditionsPath);
            var collection = DatatableElementCollectionUtility.GetOrCreateDatatableElementCollection(
                colType, NewAdditionsPath, colType.Name, startNameEditing: false);
            if (collection == null)
            {
                Debug.LogError($"[UnitFamilyLines] get-or-create '{NewAdditionsPath}/{colType.Name}' failed.");
                return null;
            }
            IDatatableElement[] dups = null;
            bool ok = DatatableElementCollectionUtility.TryDuplicateDatatableElements(
                new[] { element }, ref collection, ref dups,
                showWarningDialogThresholdCount: false, ensureUniqueName: false, reimport: false);
            if (!ok || dups == null || dups.Length == 0)
            {
                Debug.LogError($"[UnitFamilyLines] Duplicate failed for '{live.name}'.");
                return null;
            }
            dups[0].SetEditable(true);
            return dups[0] as UnityEngine.Object;
        }
        catch (Exception e)
        {
            Debug.LogError($"[UnitFamilyLines] Lift failed for '{live.name}': {e.Message}");
            return null;
        }
    }

    public static UnityEngine.Object CreateFamily(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var familyType = FindType("Amplitude.Mercury.Data.Simulation.UnitFamilyDefinition");
        if (familyType == null)
        {
            Debug.LogError("[UnitFamilyLines] UnitFamilyDefinition type not found.");
            return null;
        }
        try
        {
            Type colType = null;
            DatatableElementCollectionUtility.TryGetCollectionTypeFromElementType(familyType, ref colType);
            if (colType == null)
            {
                Debug.LogError("[UnitFamilyLines] No collection type for UnitFamilyDefinition.");
                return null;
            }
            EnsureFolder(NewAdditionsPath);
            var collection = DatatableElementCollectionUtility.GetOrCreateDatatableElementCollection(
                colType, NewAdditionsPath, colType.Name, startNameEditing: false);
            if (collection == null)
            {
                Debug.LogError($"[UnitFamilyLines] get-or-create family collection failed.");
                return null;
            }
            if (collection.CreateDatatableElement(familyType, name) is not UnityEngine.Object created)
            {
                Debug.LogError($"[UnitFamilyLines] CreateDatatableElement failed for '{name}'.");
                return null;
            }
            if (created is IDatatableElement editable) editable.SetEditable(true);
            EditorUtility.SetDirty(created);
            EditorUtility.SetDirty(collection as UnityEngine.Object);
            return created;
        }
        catch (Exception e)
        {
            Debug.LogError($"[UnitFamilyLines] CreateFamily '{name}' failed: {e.Message}");
            return null;
        }
    }

    public static bool WriteFamilyAndLevel(UnityEngine.Object unit, string familyName, int? level)
    {
        if (unit == null) return false;
        bool ok = true;
        if (familyName != null)
        {
            var f = unit.GetType().GetField("SerializableFamily", ALL);
            if (f == null) { Debug.LogError($"[UnitFamilyLines] SerializableFamily missing on {unit.name}"); ok = false; }
            else f.SetValue(unit, familyName);
        }
        if (level.HasValue)
        {
            var f = unit.GetType().GetField("Level", ALL);
            if (f == null) { Debug.LogError($"[UnitFamilyLines] Level missing on {unit.name}"); ok = false; }
            else f.SetValue(unit, level.Value);
        }
        EditorUtility.SetDirty(unit);
        return ok;
    }

    public static bool WriteNext(UnityEngine.Object family, string nextOrEmpty)
    {
        if (family == null) return false;
        var f = family.GetType().GetField("SerializableNextFamilyName", ALL);
        if (f == null)
        {
            Debug.LogError($"[UnitFamilyLines] SerializableNextFamilyName missing on {family.name}");
            return false;
        }
        f.SetValue(family, string.IsNullOrEmpty(nextOrEmpty) ? "" : nextOrEmpty);
        EditorUtility.SetDirty(family);
        return true;
    }

    public static bool WriteObsolete(UnityEngine.Object family, bool obsolete)
    {
        if (family == null) return false;
        var f = family.GetType().GetField("IsObsolete", ALL);
        if (f == null) return false;
        f.SetValue(family, obsolete);
        EditorUtility.SetDirty(family);
        return true;
    }

    static void EnsureFolder(string folderPath)
    {
        if (UnityEditor.AssetDatabase.IsValidFolder(folderPath)) return;
        string[] parts = folderPath.Split('/');
        string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = cur + "/" + parts[i];
            if (!UnityEditor.AssetDatabase.IsValidFolder(next))
                UnityEditor.AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }

    public class JsonFamily
    {
        public string Name;
        public bool? Obsolete;
        public string Next;       // null in JSON → terminal (NextSpecified && Next==null/empty)
        public bool NextSpecified;
        public List<JsonUnit> Units = new();
    }

    public class JsonUnit
    {
        public string Name;
        public int Level;
        public bool LevelSpecified;
    }

    public class ImportDiff
    {
        public List<string> FamiliesToCreate = new();
        public List<(string Name, string Next, bool? Obsolete)> FamilyWrites = new();
        public List<(UnitRow Row, string Family, int Level)> UnitWrites = new();
        public List<string> MissingUnits = new();
        public List<string> Errors = new();
        public int NeedLiftUnits;
        public int NeedLiftFamilies;
    }

    public static bool TryParseExportJson(string json, out List<JsonFamily> families, out string error)
    {
        families = new List<JsonFamily>();
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Empty JSON.";
            return false;
        }
        try
        {
            int famArr = json.IndexOf("\"families\"", StringComparison.Ordinal);
            if (famArr < 0) { error = "Missing \"families\" array."; return false; }
            int arrStart = json.IndexOf('[', famArr);
            if (arrStart < 0) { error = "Malformed families array."; return false; }
            int depth = 0;
            int i = arrStart;
            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) { i++; break; } }
            }
            string arr = json.Substring(arrStart, i - arrStart);

            foreach (var block in ExtractObjects(arr))
            {
                var fam = new JsonFamily();
                if (!TryReadStringProp(block, "name", out fam.Name) || string.IsNullOrEmpty(fam.Name))
                    continue;
                if (TryReadBoolProp(block, "obsolete", out var obs))
                    fam.Obsolete = obs;
                if (TryReadNullableStringProp(block, "next", out var next, out bool nextSpec))
                {
                    fam.NextSpecified = nextSpec;
                    fam.Next = NormalizeNext(next);
                }
                int unitsIdx = block.IndexOf("\"units\"", StringComparison.Ordinal);
                if (unitsIdx >= 0)
                {
                    int uStart = block.IndexOf('[', unitsIdx);
                    if (uStart >= 0)
                    {
                        int ud = 0, uj = uStart;
                        for (; uj < block.Length; uj++)
                        {
                            if (block[uj] == '[') ud++;
                            else if (block[uj] == ']') { ud--; if (ud == 0) { uj++; break; } }
                        }
                        string uArr = block.Substring(uStart, uj - uStart);
                        foreach (var ub in ExtractObjects(uArr))
                        {
                            if (!TryReadStringProp(ub, "name", out var uname) || string.IsNullOrEmpty(uname))
                                continue;
                            var ju = new JsonUnit { Name = uname };
                            if (TryReadIntProp(ub, "level", out var lvl))
                            {
                                ju.Level = lvl;
                                ju.LevelSpecified = true;
                            }
                            fam.Units.Add(ju);
                        }
                    }
                }
                families.Add(fam);
            }
            if (families.Count == 0)
            {
                error = "No family entries parsed.";
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    static List<string> ExtractObjects(string arrayText)
    {
        var list = new List<string>();
        int depth = 0;
        int start = -1;
        for (int i = 0; i < arrayText.Length; i++)
        {
            char c = arrayText[i];
            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    list.Add(arrayText.Substring(start, i - start + 1));
                    start = -1;
                }
            }
        }
        return list;
    }

    static bool TryReadStringProp(string obj, string key, out string value)
    {
        value = null;
        var m = Regex.Match(obj, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
        if (!m.Success) return false;
        value = UnescapeJson(m.Groups[1].Value);
        return true;
    }

    static bool TryReadNullableStringProp(string obj, string key, out string value, out bool specified)
    {
        value = null;
        specified = false;
        var m = Regex.Match(obj, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(null|\"((?:\\\\.|[^\"\\\\])*)\")");
        if (!m.Success) return false;
        specified = true;
        if (m.Groups[1].Value == "null") { value = ""; return true; }
        value = UnescapeJson(m.Groups[2].Value);
        return true;
    }

    static bool TryReadBoolProp(string obj, string key, out bool value)
    {
        value = false;
        var m = Regex.Match(obj, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)");
        if (!m.Success) return false;
        value = m.Groups[1].Value == "true";
        return true;
    }

    static bool TryReadIntProp(string obj, string key, out int value)
    {
        value = 0;
        var m = Regex.Match(obj, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
        if (!m.Success) return false;
        return int.TryParse(m.Groups[1].Value, out value);
    }

    static string UnescapeJson(string s) =>
        s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");

    public static ImportDiff DiffImport(Graph graph, List<JsonFamily> jsonFamilies)
    {
        var diff = new ImportDiff();
        if (graph == null || jsonFamilies == null) return diff;

        var unitIndex = new Dictionary<string, UnitRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in graph.Families)
            foreach (var u in f.Units)
                if (!unitIndex.ContainsKey(u.Name))
                    unitIndex[u.Name] = u;

        var jsonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var jf in jsonFamilies)
        {
            if (string.IsNullOrEmpty(jf.Name)) continue;
            jsonNames.Add(jf.Name);
            if (jf.NextSpecified && !string.IsNullOrEmpty(jf.Next))
                jsonNames.Add(jf.Next);
        }

        var createSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in jsonNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (graph.ByName.TryGetValue(name, out var existing) && !existing.IsBrokenStub)
                continue;
            createSet.Add(name);
            diff.FamiliesToCreate.Add(name);
        }

        foreach (var jf in jsonFamilies)
        {
            if (string.IsNullOrEmpty(jf.Name)) continue;
            bool willExist = (graph.ByName.TryGetValue(jf.Name, out var node) && !node.IsBrokenStub)
                             || createSet.Contains(jf.Name);
            if (!willExist) continue;

            string desiredNext = jf.NextSpecified ? (jf.Next ?? "") : null;
            bool? desiredObs = jf.Obsolete;

            if (node != null && !node.IsBrokenStub)
            {
                bool nextChange = desiredNext != null &&
                    !string.Equals(NormalizeNext(node.NextName), NormalizeNext(desiredNext), StringComparison.OrdinalIgnoreCase);
                bool obsChange = desiredObs.HasValue && desiredObs.Value != node.IsObsolete;
                if (nextChange || obsChange)
                {
                    diff.FamilyWrites.Add((jf.Name, desiredNext ?? node.NextName, desiredObs));
                    if (node.ProjectDef == null || !UnderDatabases(node.ProjectDef))
                        diff.NeedLiftFamilies++;
                }
            }
            else if (createSet.Contains(jf.Name) && (desiredNext != null || desiredObs.HasValue))
            {
                // New family — write next/obsolete after create (no lift needed).
                diff.FamilyWrites.Add((jf.Name, desiredNext ?? "", desiredObs));
            }

            foreach (var ju in jf.Units)
            {
                if (string.IsNullOrEmpty(ju.Name)) continue;
                if (!unitIndex.TryGetValue(ju.Name, out var row))
                {
                    diff.MissingUnits.Add(ju.Name);
                    continue;
                }
                int wantLevel = ju.LevelSpecified ? ju.Level : row.Level;
                bool famChange = !string.Equals(row.FamilyName, jf.Name, StringComparison.OrdinalIgnoreCase);
                bool lvlChange = ju.LevelSpecified && ju.Level != row.Level;
                if (!famChange && !lvlChange) continue;
                diff.UnitWrites.Add((row, jf.Name, wantLevel));
                if (row.ProjectDef == null || !UnderDatabases(row.ProjectDef))
                    diff.NeedLiftUnits++;
            }
        }

        return diff;
    }

    /// <summary>
    /// Step 1 create/lift, step 2 write. Returns false if aborted due to failures.
    /// </summary>
    public static bool ApplyImport(Graph graph, ImportDiff diff, out string report)
    {
        var sb = new StringBuilder();
        if (diff == null)
        {
            report = "Nothing to apply.";
            return false;
        }

        // Step 1 — create
        var created = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in diff.FamiliesToCreate)
        {
            var obj = CreateFamily(name);
            if (obj == null)
            {
                report = $"Create failed for family '{name}'. Aborted before writes.";
                return false;
            }
            created[name] = obj;
            sb.AppendLine($"created {name}");
        }

        // Step 1 — lift families
        foreach (var (name, _, _) in diff.FamilyWrites)
        {
            if (created.ContainsKey(name)) continue;
            if (!graph.ByName.TryGetValue(name, out var node) || node.IsBrokenStub)
            {
                report = $"Family '{name}' missing after create. Aborted.";
                return false;
            }
            if (EnsureWritableFamily(node) == null)
            {
                report = $"Failed to import family '{name}'. Aborted before writes.";
                return false;
            }
        }

        // Step 1 — lift units
        foreach (var (row, _, _) in diff.UnitWrites)
        {
            if (EnsureWritableUnit(row) == null)
            {
                report = $"Failed to import unit '{row.Name}'. Aborted before writes.";
                return false;
            }
        }

        // Step 2 — writes
        int famOk = 0, unitOk = 0;
        foreach (var (name, next, obsolete) in diff.FamilyWrites)
        {
            UnityEngine.Object asset = null;
            if (created.TryGetValue(name, out var c)) asset = c;
            else if (graph.ByName.TryGetValue(name, out var node)) asset = node.ProjectDef ?? node.ActiveDef;
            if (asset == null) continue;
            Undo.RecordObject(asset, "Import family next");
            if (next != null) WriteNext(asset, next);
            if (obsolete.HasValue) WriteObsolete(asset, obsolete.Value);
            famOk++;
        }

        foreach (var (row, family, level) in diff.UnitWrites)
        {
            var asset = row.ProjectDef ?? row.ActiveDef;
            if (asset == null) continue;
            Undo.RecordObject(asset, "Import unit family/level");
            if (WriteFamilyAndLevel(asset, family, level)) unitOk++;
        }

        UnityEditor.AssetDatabase.SaveAssets();
        sb.AppendLine($"wrote {famOk} family field set(s), {unitOk} unit(s)");
        report = sb.ToString();
        return true;
    }

    static int GetInt(object obj, string field)
    {
        var v = obj.GetType().GetField(field, ALL)?.GetValue(obj);
        return v is int i ? i : 0;
    }

    static bool GetBool(object obj, string field)
    {
        var v = obj.GetType().GetField(field, ALL)?.GetValue(obj);
        return v is bool b && b;
    }

    static string GetString(object obj, string field)
        => obj.GetType().GetField(field, ALL)?.GetValue(obj) as string ?? "";

    static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t != null) return t;
        }
        return null;
    }

    [MenuItem("Tools/shakee's Tools/Debug/Unit Family Lines/Dump Data", false, 110)]
    static void DumpData()
    {
        var g = Build();
        var sb = new StringBuilder();
        sb.AppendLine($"=== Unit Family Lines: {g.Families.Count} families ===");
        if (!string.IsNullOrEmpty(g.Error)) sb.AppendLine("ERROR: " + g.Error);
        int roots = g.Families.Count(f => f.PreviousNames.Length == 0 && !f.IsBrokenStub);
        int terminals = g.Families.Count(f => string.IsNullOrEmpty(f.NextName) && !f.IsBrokenStub);
        int merges = g.Families.Count(f => f.PreviousNames.Length > 1);
        sb.AppendLine($"roots={roots} terminals={terminals} multi-parent={merges}");
        sb.AppendLine();
        foreach (var f in g.Families.OrderBy(f => f.Layer).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            string next = string.IsNullOrEmpty(f.NextName) ? "(none)" :
                (f.NextMissing ? f.NextName + " MISSING" : f.NextName);
            string prev = f.PreviousNames.Length == 0 ? "(root)" : string.Join(", ", f.PreviousNames);
            sb.AppendLine($"L{f.Layer} [{f.Domain}] {f.Name}  next={next}  prev=[{prev}]  units={f.Units.Count}" +
                          (f.IsObsolete ? " OBSOLETE" : "") +
                          (f.IsBrokenStub ? " STUB" : "") +
                          $" [{ScopeLabel(f.Source)}]");
            foreach (var u in f.Units)
                sb.AppendLine($"        L{u.Level} {u.Name} [{ScopeLabel(u.Source)}]");
        }
        Debug.Log(sb.ToString());
    }
}
