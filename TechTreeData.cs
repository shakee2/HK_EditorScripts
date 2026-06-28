using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the tech-tree data layer: for every TechnologyDefinition, resolve its
/// grid position + localized title + prerequisite edges into a clean row.
///
/// Three resolvers, all by-name reflection (never field enumeration, to avoid the
/// static/recursion noise the probes showed):
///   1. tech name -> TechnologyUIMapper -> TechTreeX/Y + Title (%key)
///   2. Title (%key) -> processed localization collection -> CompactedNodes[0].TextValue
///   3. tech -> TechnologyPrerequisite.SerializableTechnologyNames -> prereq names (OR)
///
/// Run "Tools/Tech Tree/Dump Data" to verify rows before any canvas is built.
/// </summary>
public static class TechTreeData
{
    const BindingFlags ALL =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public class Node
    {
        public string Name;        // asset name, e.g. "Technology_Era1_01"
        public string TitleKey;    // raw "%...Title"
        public string DescriptionKey;  // raw "%...Description"
        public string Label;       // resolved display text (falls back to key/name)
        public string TitleText;       // resolved Title text, "" if unresolved (no key/name fallback)
        public string DescriptionText; // resolved Description text, "" if unresolved
        public string Era;         // EraReference name, for grouping/tinting
        public string Tier;        // TechnologyTier enum

        // Per-asset references. "Vanilla*" = read-only copy loaded straight from the
        // mounted MercuryDatabases asset bundle (fallback);
        // "Mod*" = the editable copy under Databases/ (null if not yet imported there).
        public UnityEngine.Object VanillaDef,    ModDef;     // TechnologyDefinition
        public UnityEngine.Object VanillaMapper, ModMapper;  // TechnologyUIMapper

        // Active = Databases copy if present, else reference copy. Reads come from these.
        public UnityEngine.Object ActiveDef    => ModDef    != null ? ModDef    : VanillaDef;
        public UnityEngine.Object ActiveMapper => ModMapper != null ? ModMapper : VanillaMapper;

        // "Modded" here means "exists under Databases/ (editable in place)".
        // Reference-only assets (ModDef/ModMapper null) get copied to New Additions on first edit.
        public bool DefModded    => ModDef    != null;
        public bool MapperModded => ModMapper != null;
        public bool AnyModded    => DefModded || MapperModded;

        // Base (saved-to-disk) values, read from the active assets at build time.
        public int    BaseX, BaseY;
        public string[] BasePrereqs;

        // Convenience: the asset to ping/select (prefer the def).
        public UnityEngine.Object Asset => ActiveDef ?? ActiveMapper;
    }

    // Default copy-on-write destination for lifting reference-only assets / new techs.
    public const string DefaultModPath = "Assets/Databases/New Additions";
    public const string DatabasesRoot  = "Assets/Databases";

    static bool Under(UnityEngine.Object o, string root)
    {
        var p = AssetDatabase.GetAssetPath(o);
        return !string.IsNullOrEmpty(p) &&
               (p == root || p.StartsWith(root + "/", StringComparison.Ordinal));
    }

    public static List<Node> Build() => Build(DefaultModPath);

    public static List<Node> Build(string modPath)
    {
        var loc        = BuildLocalizationDict();
        var techType   = FindType("Amplitude.Mercury.Data.Simulation.TechnologyDefinition");
        var mapperType = FindType("Amplitude.Mercury.UI.TechnologyUIMapper");
        if (techType == null || mapperType == null)
        { Debug.LogError("[TechTree] Tech/Mapper type not found."); return new(); }

        // Mod copies: project assets under Databases/ (shippable/editable).
        // Vanilla copies: loaded straight from the mounted MercuryDatabases asset bundle.
        var dbDef = new Dictionary<string, UnityEngine.Object>();
        foreach (var o in LoadAllOfType(techType))
            if (Under(o, DatabasesRoot)) dbDef[o.name] = o;
        var refDef = new Dictionary<string, UnityEngine.Object>();
        foreach (var o in VanillaDatabaseMount.LoadAllOfType(techType))
            refDef[o.name] = o;

        var dbMap = new Dictionary<string, UnityEngine.Object>();
        foreach (var o in LoadAllOfType(mapperType))
            if (Under(o, DatabasesRoot)) dbMap[o.name] = o;
        var refMap = new Dictionary<string, UnityEngine.Object>();
        foreach (var o in VanillaDatabaseMount.LoadAllOfType(mapperType))
            refMap[o.name] = o;

        var f_era  = techType.GetField("EraReference", ALL);
        var f_tier = techType.GetField("TechnologyTier", ALL);

        var names = new HashSet<string>(dbDef.Keys);
        names.UnionWith(refDef.Keys);

        var nodes = new List<Node>();
        foreach (var name in names)
        {
            var node = new Node
            {
                Name          = name,
                VanillaDef    = refDef.TryGetValue(name, out var rd) ? rd : null,
                ModDef        = dbDef.TryGetValue(name, out var dd) ? dd : null,
                VanillaMapper = refMap.TryGetValue(name, out var rm) ? rm : null,
                ModMapper     = dbMap.TryGetValue(name, out var dm) ? dm : null,
            };

            var mapper = node.ActiveMapper;
            if (mapper != null)
            {
                node.BaseX    = GetInt(mapper, "TechTreeX");
                node.BaseY    = GetInt(mapper, "TechTreeY");
                node.TitleKey = GetString(mapper, "Title");
                node.DescriptionKey = GetString(mapper, "Description");
            }
            node.TitleText = !string.IsNullOrEmpty(node.TitleKey) && loc.TryGetValue(node.TitleKey, out var t) ? t : "";
            node.DescriptionText = !string.IsNullOrEmpty(node.DescriptionKey) && loc.TryGetValue(node.DescriptionKey, out var d) ? d : "";
            node.Label = node.TitleText.Length > 0 ? node.TitleText : (string.IsNullOrEmpty(node.TitleKey) ? name : node.TitleKey);

            var def = node.ActiveDef;
            node.BasePrereqs = Array.Empty<string>();
            if (def != null)
            {
                var prereqObj = def.GetType().GetField("TechnologyPrerequisite", ALL)?.GetValue(def);
                if (prereqObj != null &&
                    prereqObj.GetType().GetField("SerializableTechnologyNames", ALL)?.GetValue(prereqObj) is string[] arr)
                    node.BasePrereqs = arr.Where(s => !string.IsNullOrEmpty(s)).ToArray();
                node.Era  = ResolveReferenceName(f_era?.GetValue(def));
                node.Tier = f_tier?.GetValue(def)?.ToString() ?? "";
            }
            nodes.Add(node);
        }
        return nodes;
    }

    // ── Resolver 2: build the %key -> text dictionary once ────────────────────
    // Sourced from the Mod Editor's own archive translations bundle (the same one the
    // Localization Window mounts) — NOT from a Databases-folder scan; the %key strings are
    // LocalizedStringTranslation rows (LocalizationLine.Id -> LocalizationLine.Body), not
    // anything under Assets/Databases.
    static Dictionary<string, string> BuildLocalizationDict()
    {
        var dict = new Dictionary<string, string>(ArchiveTranslations.BuildKeyToTextDict());
        if (dict.Count == 0)
            Debug.LogWarning($"[TechTree] No archive translations loaded ({ArchiveTranslations.LastError}); labels will fall back to keys.");
        return dict;
    }

    // ── Asset loading helpers ─────────────────────────────────────────────────
    static IEnumerable<UnityEngine.Object> LoadAllOfType(Type t)
    {
        foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && t.IsInstanceOfType(o))
                    yield return o;
        }
    }

    // ── Field read helpers (by name, never enumeration) ───────────────────────
    static int GetInt(object obj, string field)
    {
        var v = obj.GetType().GetField(field, ALL)?.GetValue(obj);
        return v is int i ? i : 0;
    }
    static string GetString(object obj, string field)
        => obj.GetType().GetField(field, ALL)?.GetValue(obj) as string ?? "";

    // DatatableElementReference -> its serializableElementName
    static string ResolveReferenceName(object reference)
    {
        if (reference == null) return "";
        return reference.GetType().GetField("serializableElementName", ALL)?.GetValue(reference) as string ?? "";
    }

    static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t != null) return t;
        }
        return null;
    }

    // ── Write path: ensure a writable asset, then field writers ───────────────
    // Returns the asset to write into. Active asset already under Databases/ -> edit
    // in place. Reference-only -> lift the vanilla element into an override collection at
    // vanilla's own mirrored path (exactly what the Mod Editor's "Override from Archives"
    // does) and return the writable duplicate. Null if nothing to write.
    public static UnityEngine.Object EnsureWritable(Node node, bool isMapper, string modPath)
    {
        var active = isMapper ? node.ActiveMapper : node.ActiveDef;
        if (active == null)
        {
            Debug.LogWarning($"[TechTree] {node.Name}: no {(isMapper ? "mapper" : "def")} to write — new-tech creation is v2.");
            return null;
        }
        if (Under(active, DatabasesRoot)) return active;     // edit in place
        return VanillaDatabaseMount.OverrideVanillaElement(active);  // lift vanilla element -> mirrored Databases path
    }

    public static void WritePosition(UnityEngine.Object mapper, int x, int y)
    {
        var t = mapper.GetType();
        t.GetField("TechTreeX", ALL)?.SetValue(mapper, x);
        t.GetField("TechTreeY", ALL)?.SetValue(mapper, y);
    }

    public static bool WritePrereqs(UnityEngine.Object def, string[] names)
    {
        var f_prereq = def.GetType().GetField("TechnologyPrerequisite", ALL);
        var prereqObj = f_prereq?.GetValue(def);
        if (prereqObj == null) return false;
        var f_names = prereqObj.GetType().GetField("SerializableTechnologyNames", ALL);
        if (f_names == null) return false;
        f_names.SetValue(prereqObj, names);
        if (prereqObj.GetType().IsValueType) f_prereq.SetValue(def, prereqObj);  // struct write-back
        return true;
        // The runtime-resolved TechnologyNames (StaticString[]) is rebuilt from
        // SerializableTechnologyNames on load, so only the serializable form is written.
    }

    // ── Verification dump ─────────────────────────────────────────────────────
    [MenuItem("Tools/Debug/Tech Tree/Diagnose Mod Split", false, 100)]
    static void DiagnoseModSplit()
    {
        DiagnoseModSplit(DefaultModPath);
    }

    public static void DiagnoseModSplit(string modPath)
    {
        var techType   = FindType("Amplitude.Mercury.Data.Simulation.TechnologyDefinition");
        var mapperType = FindType("Amplitude.Mercury.UI.TechnologyUIMapper");
        var sb = new StringBuilder();
        sb.AppendLine("=== Mod-split diagnostic ===");
        VanillaDatabaseMount.TryMount(out var mountError);
        sb.AppendLine($"vanilla bundle         = \"{VanillaDatabaseMount.BundlePath}\" ({(VanillaDatabaseMount.IsMounted ? "mounted" : "NOT MOUNTED: " + mountError)})");
        sb.AppendLine($"databases (shippable)  = \"{DatabasesRoot}\"");
        sb.AppendLine($"copy-on-write target   = \"{modPath}\"\n");

        void Report(string label, Type t)
        {
            if (t == null) { sb.AppendLine($"{label}: TYPE NOT FOUND\n"); return; }
            int inDb = 0, elsewhere = 0;
            var dbFolders = new Dictionary<string, int>();
            foreach (var o in LoadAllOfType(t))
            {
                var p = AssetDatabase.GetAssetPath(o);
                if (Under(o, DatabasesRoot))
                {
                    inDb++;
                    var dir = System.IO.Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "";
                    dbFolders[dir] = dbFolders.TryGetValue(dir, out var c) ? c + 1 : 1;
                }
                else elsewhere++;
            }
            int inVanilla = VanillaDatabaseMount.LoadAllOfType(t).Count();
            sb.AppendLine($"{label}: {inDb} in Databases, {inVanilla} vanilla (mounted bundle), {elsewhere} elsewhere(ignored)");
            foreach (var kv in dbFolders.OrderByDescending(k => k.Value).Take(12))
                sb.AppendLine($"      [{kv.Value,4}]  {kv.Key}");
            if (inDb == 0)
                sb.AppendLine("      (NONE in Databases — if techs should be editable, check that they were imported there)");
            sb.AppendLine();
        }

        Report("TechnologyDefinition", techType);
        Report("TechnologyUIMapper", mapperType);
        Debug.Log(sb.ToString());
    }

    [MenuItem("Tools/Debug/Tech Tree/Dump Data", false, 101)]
    static void DumpData()
    {
        var nodes = Build();
        var sb = new StringBuilder();
        sb.AppendLine($"=== Tech Tree: {nodes.Count} technologies ===\n");

        int noPos = nodes.Count(n => n.BaseX == 0 && n.BaseY == 0);
        int noLabel = nodes.Count(n => n.Label == n.TitleKey || n.Label == n.Name);
        int modded = nodes.Count(n => n.AnyModded);
        sb.AppendLine($"warnings: {noPos} no position, {noLabel} unresolved label · {modded} modded\n");

        foreach (var n in nodes.OrderBy(n => n.Era).ThenBy(n => n.BaseX).ThenBy(n => n.BaseY))
        {
            string mod = n.AnyModded ? $" [{(n.DefModded ? "def" : "")}{(n.DefModded && n.MapperModded ? "+" : "")}{(n.MapperModded ? "map" : "")}]" : "";
            sb.AppendLine($"({n.BaseX,3},{n.BaseY,2}) [{n.Era}/{n.Tier}]{mod} {n.Name}");
            sb.AppendLine($"        \"{n.Label}\"");
            if (n.BasePrereqs.Length > 0)
                sb.AppendLine($"        <= {string.Join("  OR  ", n.BasePrereqs)}");
        }
        Debug.Log(sb.ToString());
    }
}