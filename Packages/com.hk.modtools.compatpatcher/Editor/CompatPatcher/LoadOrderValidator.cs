using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using Object = UnityEngine.Object;

namespace HK.CompatPatcher
{
    public enum FindingSeverity { Error, Warning }

    /// <summary>One load-time hazard found while replaying a merged (vanilla + mods, last-wins) table.</summary>
    public class Finding
    {
        public FindingSeverity Severity;
        public string Code;      // DataController line ref, e.g. ":4734" (see Docs/CompatPatcher-LoadValidations.md)
        public string Element;   // the element the game names in the error (the unlock carrier)
        public string Detail;    // human-readable "what's wrong and how it resolved"
        public string IntroducedBy; // the mod whose load step first triggers this hazard (the reset point)

        // Key = code + carrier: coarse identity for the forward/reverse order comparison ("same hazard").
        public string Key => Code + "|" + Element;
        // DedupKey = code + carrier + exact message: a *distinct* hazard. Two different mixed unlock events on
        // one carrier, or the same event with a different offending unit, are different hazards and both show.
        public string DedupKey => Code + "|" + Element + "|" + Detail;
        public string Line => $"{(Severity == FindingSeverity.Error ? "ERROR" : "warn")} {Code}  {Element}: {Detail}"
                            + (string.IsNullOrEmpty(IntroducedBy) ? "" : $"  ← resets when loading {IntroducedBy}");
    }

    /// <summary>
    /// Replays the reset-capable, cross-mod load-time rules (Docs/CompatPatcher-LoadValidations.md) over the
    /// real load order — **Vanilla → Mod A → Mod B → …** — the same way DataController.Initialize does.
    ///
    /// Because whole elements are replaced last-wins, the merged datatable it checks is: every vanilla
    /// element, overridden by name by each mod in load order. Vanilla comes from the mounted MercuryDatabases
    /// bundle as live objects (VanillaDatabaseMount). Assetbundle mods reuse mounted LiveObjects; folder/zip/
    /// unitypackage mods stage to live objects (PatchBuilder) so Odin-backed lists (a tech's
    /// SimulationEventEffects) read correctly — never guessed from raw YAML. Overlay typing prefers
    /// LiveObject.GetType() over MonoScript guid:fileID resolution. Rules are then evaluated by reflection
    /// against the exact fields DataController reads.
    ///
    /// Implemented rules (all reset-capable → Error):
    ///   :4713 unlock references a constructible no vanilla-or-mod defines
    ///   :4718 unlock targets an EmpireWideConstructionParticipationDefinition (illegal)
    ///   :4734 constructibles in one unlock event resolve to different families (the confirmed ENC+VIP crash)
    ///   :4747 unlock references a resource no vanilla-or-mod defines
    ///   :4299 PresentationPawn references a PresentationUnitDefinition that doesn't exist after merge
    ///   :4369 PresentationSecondaryPawn (mount) references a PresentationUnitDefinition that doesn't exist
    ///   :4139 emblematic Unit/SettlementImprovement placed in a common family level
    ///   :4144 common Unit/SettlementImprovement placed in an emblematic family level
    ///
    /// Evaluate checks the order **incrementally at every load step** (Vanilla, +Mod A, +Mod A+B, …), because
    /// the game validates as each mod loads and resets at the first bad step — so a hazard Mod B introduces is
    /// caught even if a later mod would mask it in the final merge.
    ///
    /// Build the context once (loads vanilla), then Evaluate each order. Vanilla is cached across the whole
    /// session (it doesn't change), so only the first Compare pays the vanilla load; the cache self-invalidates
    /// on unmount/domain reload (and can be cleared manually via
    /// Tools ▸ Debug ▸ Compat Patcher ▸ Clear Vanilla Validation Cache).
    /// </summary>
    public sealed class LoadOrderValidator : IDisposable
    {
        const string NS = "Amplitude.Mercury.Data.Simulation.";
        const string NSW = "Amplitude.Mercury.Data.World.";
        const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Resolved element/effect types (null if the game assemblies aren't loaded).
        readonly Type tConstructible, tResource, tTech, tCivic, tNationalProject, tNPLeveling,
                      tEmpireWideParticipation, tUnlockConstructible, tUnlockResource,
                      tPresentationUnit, tPresentationPawn, tPresentationMount,
                      tUnitDefinition, tSettlementImprovement;

        // Vanilla base for this instance (empty until a successful mount; then pointed at the shared cache).
        Dictionary<string, Object> _vConstruct = new Dictionary<string, Object>();
        Dictionary<string, Object> _vResource = new Dictionary<string, Object>();
        Dictionary<string, Object> _vTech = new Dictionary<string, Object>();
        Dictionary<string, Object> _vCivic = new Dictionary<string, Object>();
        Dictionary<string, Object> _vPresentationUnit = new Dictionary<string, Object>();
        Dictionary<string, Object> _vPresentationPawn = new Dictionary<string, Object>();
        Dictionary<string, Object> _vPresentationMount = new Dictionary<string, Object>();

        readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

        // ---- shared vanilla cache ----------------------------------------
        // Vanilla is identical for the whole session, so it's loaded once and reused across every Compare.
        // The cache is invalidated automatically: a domain reload resets the statics, and an unmount/remount
        // destroys the loaded objects — detected by the Unity fake-null check on a sample below.
        static Dictionary<string, Object> s_vConstruct, s_vResource, s_vTech, s_vCivic,
                                            s_vPresentationUnit, s_vPresentationPawn, s_vPresentationMount;
        static Object s_cacheSample;

        static bool CacheAlive => s_vTech != null && s_cacheSample != null;

        static void EnsureVanillaCache(LoadOrderValidator v)
        {
            if (CacheAlive) return;
            s_vConstruct = new Dictionary<string, Object>();
            s_vResource = new Dictionary<string, Object>();
            s_vTech = new Dictionary<string, Object>();
            s_vCivic = new Dictionary<string, Object>();
            s_vPresentationUnit = new Dictionary<string, Object>();
            s_vPresentationPawn = new Dictionary<string, Object>();
            s_vPresentationMount = new Dictionary<string, Object>();
            LoadVanilla(s_vConstruct, v.tConstructible);
            LoadVanilla(s_vResource, v.tResource);
            LoadVanilla(s_vTech, v.tTech);
            LoadVanilla(s_vCivic, v.tCivic);
            LoadVanilla(s_vPresentationUnit, v.tPresentationUnit);
            LoadVanilla(s_vPresentationPawn, v.tPresentationPawn);
            LoadVanilla(s_vPresentationMount, v.tPresentationMount);
            s_cacheSample = s_vTech.Values.FirstOrDefault() ?? s_vConstruct.Values.FirstOrDefault()
                         ?? s_vPresentationUnit.Values.FirstOrDefault();
        }

        /// <summary>Drop the cached vanilla base so the next Compare reloads it (use after changing the Humankind folder).</summary>
        [MenuItem("Tools/shakee's Tools/Debug/Compat Patcher/Clear Vanilla Validation Cache")]
        public static void ClearVanillaCache()
        {
            s_vConstruct = s_vResource = s_vTech = s_vCivic = null;
            s_vPresentationUnit = s_vPresentationPawn = s_vPresentationMount = null;
            s_cacheSample = null;
        }

        public bool VanillaAvailable { get; private set; }
        public string Note { get; private set; }

        LoadOrderValidator()
        {
            tConstructible = FindType(NS + "ConstructibleDefinition");
            tResource = FindType(NS + "ResourceDefinition");
            tTech = FindType(NS + "TechnologyDefinition");
            tCivic = FindType(NS + "CivicDefinition");
            tNationalProject = FindType(NS + "NationalProjectDefinition");
            tNPLeveling = FindType(NS + "NationalProject_Leveling");
            tEmpireWideParticipation = FindType(NS + "EmpireWideConstructionParticipationDefinition");
            tUnlockConstructible = FindType(NS + "SimulationEventEffect_UnlockConstructible");
            tUnlockResource = FindType(NS + "SimulationEventEffect_UnlockResource");
            tPresentationUnit = FindType(NSW + "PresentationUnitDefinition");
            tPresentationPawn = FindType(NSW + "PresentationPawnDefinition");
            tPresentationMount = FindType(NSW + "PresentationSecondaryPawnDefinition");
            // InitializeFamilies only runs for these two U types — not NationalProject / districts / etc.
            tUnitDefinition = FindType(NS + "UnitDefinition");
            tSettlementImprovement = FindType(NS + "SettlementImprovementDefinition");
        }

        public static LoadOrderValidator Build()
        {
            var v = new LoadOrderValidator();
            if (v.tConstructible == null || v.tTech == null)
            {
                v.Note = "Game data types not found — validation unavailable (open the project with the mod assemblies loaded).";
                return v;
            }
            if (!VanillaDatabaseMount.TryMount(out var err))
            {
                v.Note = $"Vanilla databases not mounted ({err}). Checking mods against each other only — "
                       + "load-time hazards that only show against vanilla (e.g. a mod renaming one unit's family) will be MISSED. "
                       + "Set the Humankind folder in Mercury/Mod Editor and Compare again.";
                return v;
            }
            v.VanillaAvailable = true;
            EnsureVanillaCache(v);              // load once per session; reused on later Compares
            v._vConstruct = s_vConstruct;
            v._vResource = s_vResource;
            v._vTech = s_vTech;
            v._vCivic = s_vCivic;
            v._vPresentationUnit = s_vPresentationUnit;
            v._vPresentationPawn = s_vPresentationPawn;
            v._vPresentationMount = s_vPresentationMount;
            return v;
        }

        static void LoadVanilla(Dictionary<string, Object> into, Type t)
        {
            if (t == null) return;
            foreach (var o in VanillaDatabaseMount.LoadAllOfType(t))
                if (o != null && !string.IsNullOrEmpty(o.name)) into[o.name] = o; // last wins is irrelevant within vanilla
        }

        /// <summary>
        /// Return every load-time hazard the given load order raises, checked at **each incremental load step**
        /// — Vanilla, then +Mod A, then +Mod A+B, … — because the game validates as each mod loads and resets
        /// at the FIRST bad step. A hazard introduced when Mod B loads is therefore reported even if a later
        /// Mod C would mask it in the final merge (in-game the reset already happened at B). Each finding is
        /// attributed via <see cref="Finding.IntroducedBy"/> to the mod whose load first triggers it. Mod
        /// overrides are staged to live objects; everything is cleaned up before returning.
        /// </summary>
        public List<Finding> Evaluate(List<HkMod> modsInLoadOrder, Action<double, string> progress = null)
        {
            var results = new List<Finding>();
            if (tConstructible == null || tTech == null) return results;

            // One accumulating merge that grows as each mod loads; copies of the shared vanilla base so a run
            // never disturbs the cache or the next order.
            var construct = new Dictionary<string, Object>(_vConstruct);
            var resource = new Dictionary<string, Object>(_vResource);
            var tech = new Dictionary<string, Object>(_vTech);
            var civic = new Dictionary<string, Object>(_vCivic);
            var presentationUnit = new Dictionary<string, Object>(_vPresentationUnit);
            var presentationPawn = new Dictionary<string, Object>(_vPresentationPawn);
            var presentationMount = new Dictionary<string, Object>(_vPresentationMount);
            var source = new Dictionary<string, string>(); // element name -> "vanilla" or mod name (provenance for messages)
            foreach (var k in construct.Keys) source[k] = "vanilla";

            var stagePaths = new List<string>();
            try
            {
                // Baseline: hazards already present in vanilla alone (expected: none) — excluded so the first
                // mod isn't blamed for a pre-existing vanilla issue.
                var seen = new HashSet<string>();
                foreach (var f in RunRules(construct, resource, tech, civic, presentationUnit, presentationPawn, presentationMount, source)) seen.Add(f.DedupKey);

                // Each mod load is one step. A distinct hazard that first appears at this step is caused by this
                // mod's load; record it once (it stays reported even if a later mod's step makes it disappear).
                int total = modsInLoadOrder.Count, n = 0;
                foreach (var mod in modsInLoadOrder)
                {
                    n++;
                    if (progress != null) progress((double)n / total, "Validating " + mod.Name);
                    OverlayOneMod(mod, construct, resource, tech, civic, presentationUnit, presentationPawn, presentationMount, source, stagePaths);
                    foreach (var f in RunRules(construct, resource, tech, civic, presentationUnit, presentationPawn, presentationMount, source))
                        if (seen.Add(f.DedupKey))
                        {
                            f.IntroducedBy = mod.Name;
                            results.Add(f);
                        }
                }
            }
            finally
            {
                foreach (var p in stagePaths) PatchBuilder.CleanupStage(p);
                if (stagePaths.Count > 0) AssetDatabase.Refresh();
            }
            return results;
        }

        // Replay every rule over the current accumulated merge and return the hazards found (with duplicates
        // from repeated effects; the caller dedups by Finding.DedupKey).
        List<Finding> RunRules(Dictionary<string, Object> construct, Dictionary<string, Object> resource,
                               Dictionary<string, Object> tech, Dictionary<string, Object> civic,
                               Dictionary<string, Object> presentationUnit, Dictionary<string, Object> presentationPawn,
                               Dictionary<string, Object> presentationMount, Dictionary<string, string> source)
        {
            var findings = new List<Finding>();
            foreach (var carrier in tech.Values)
                EvaluateCarrier(carrier, ReadArray(carrier, "SimulationEventEffects"), construct, resource, source, findings);
            foreach (var c in civic.Values)
                foreach (var choice in ReadArray(c, "Choices"))
                    EvaluateCarrier(c, ReadArray(choice, "SimulationEventEffects"), construct, resource, source, findings);
            foreach (var c in construct.Values)
            {
                if (tNPLeveling != null && tNPLeveling.IsInstanceOfType(c))
                    EvaluateCarrier(c, ReadArray(c, "EffectByLevels"), construct, resource, source, findings);
                if (tNationalProject != null && tNationalProject.IsInstanceOfType(c))
                    EvaluateCarrier(c, ReadArray(c, "Effects"), construct, resource, source, findings);
            }
            EvaluatePresentation(presentationUnit, presentationPawn, presentationMount, source, findings);
            EvaluateFamilies(construct, source, findings);
            return findings;
        }

        // Overlay ONE mod's constructibles/resources/techs/civics on top of the accumulated merge (its own
        // definitions win over vanilla and earlier mods). One stage per source file.
        void OverlayOneMod(HkMod mod, Dictionary<string, Object> construct, Dictionary<string, Object> resource,
                           Dictionary<string, Object> tech, Dictionary<string, Object> civic,
                           Dictionary<string, Object> presentationUnit, Dictionary<string, Object> presentationPawn,
                           Dictionary<string, Object> presentationMount, Dictionary<string, string> source,
                           List<string> stagePaths)
        {
            // Prefer LiveObject.GetType() — assetbundle HkElements may carry a FullName Type key (or a
            // MonoScript localId that AssetDatabase.GUIDToAssetPath can't resolve), and ResolveType(el.Type)
            // then returns null → zero overlay → silent "0 findings" even for known :4734 load orders.
            var relevant = mod.Elements.Values.Where(el => !el.IsRoot && IsRelevant(ResolveElementType(el))).ToList();
            int placed = 0;
            foreach (var g in relevant.GroupBy(el => el.SourcePath))
            {
                var (objects, stage) = PatchBuilder.StageSourceFile(mod, g.Key);
                if (stage != null) stagePaths.Add(stage);
                foreach (var el in g)
                {
                    // Bundle path: LiveObject is authoritative (StageSourceFile returns the same instances).
                    var live = el.LiveObject
                               ?? objects.FirstOrDefault(o => o != null && o.name == el.Name);
                    if (live != null && Place(live, construct, resource, tech, civic, presentationUnit, presentationPawn, presentationMount))
                    {
                        source[el.Name] = mod.Name;
                        placed++;
                    }
                }
            }
            if (placed == 0 && mod.Elements.Count > 0)
            {
                int candidates = relevant.Count;
                UnityEngine.Debug.LogWarning(
                    $"[CompatPatcher] Load-order validation: mod '{mod.Name}' overlaid 0 elements "
                    + $"(relevant candidates: {candidates}, total elements: {mod.Elements.Count}). "
                    + "Family-unlock hazards against this mod will be missed.");
            }
        }

        Type ResolveElementType(HkElement el)
        {
            if (el == null) return null;
            if (el.LiveObject != null) return el.LiveObject.GetType();
            return ResolveType(el.Type);
        }

        bool IsRelevant(Type t) =>
            t != null && ((tConstructible?.IsAssignableFrom(t) ?? false) || (tResource?.IsAssignableFrom(t) ?? false)
                       || (tTech?.IsAssignableFrom(t) ?? false) || (tCivic?.IsAssignableFrom(t) ?? false)
                       || (tPresentationUnit?.IsAssignableFrom(t) ?? false) || (tPresentationPawn?.IsAssignableFrom(t) ?? false)
                       || (tPresentationMount?.IsAssignableFrom(t) ?? false));

        bool Place(Object live, Dictionary<string, Object> construct, Dictionary<string, Object> resource,
                   Dictionary<string, Object> tech, Dictionary<string, Object> civic,
                   Dictionary<string, Object> presentationUnit, Dictionary<string, Object> presentationPawn,
                   Dictionary<string, Object> presentationMount)
        {
            if (tTech != null && tTech.IsInstanceOfType(live)) { tech[live.name] = live; return true; }
            if (tCivic != null && tCivic.IsInstanceOfType(live)) { civic[live.name] = live; return true; }
            if (tResource != null && tResource.IsInstanceOfType(live)) { resource[live.name] = live; return true; }
            if (tConstructible != null && tConstructible.IsInstanceOfType(live)) { construct[live.name] = live; return true; }
            if (tPresentationUnit != null && tPresentationUnit.IsInstanceOfType(live)) { presentationUnit[live.name] = live; return true; }
            if (tPresentationPawn != null && tPresentationPawn.IsInstanceOfType(live)) { presentationPawn[live.name] = live; return true; }
            if (tPresentationMount != null && tPresentationMount.IsInstanceOfType(live)) { presentationMount[live.name] = live; return true; }
            return false;
        }

        // Replay SetConstructibleAsNeededToBeUnlocked / SetResourcesAsNeededToBeUnlocked over one carrier's
        // effects (DataController.cs :4703 / :4739).
        void EvaluateCarrier(Object carrier, IReadOnlyList<object> effects,
                             Dictionary<string, Object> construct, Dictionary<string, Object> resource,
                             Dictionary<string, string> source, List<Finding> findings)
        {
            if (carrier == null || effects == null) return;
            string carrierName = carrier.name;
            string carrierType = carrier.GetType().Name;

            foreach (var effect in effects)
            {
                if (effect == null) continue;

                if (tUnlockConstructible != null && tUnlockConstructible.IsInstanceOfType(effect))
                {
                    string baseline = null; bool haveBaseline = false;
                    foreach (var name in RefNames(effect, "ConstructibleReferences"))
                    {
                        if (!construct.TryGetValue(name, out var c))
                        {
                            findings.Add(Err(":4713", carrierName,
                                $"{carrierType} '{carrierName}' unlocks constructible '{name}', which neither vanilla nor any loaded mod defines. Game refuses to load."));
                            continue;
                        }
                        if (tEmpireWideParticipation != null && tEmpireWideParticipation.IsInstanceOfType(c))
                        {
                            findings.Add(Err(":4718", carrierName,
                                $"{carrierType} '{carrierName}' unlocks '{name}', an EmpireWideConstructionParticipationDefinition (illegal unlock target). Game refuses to load."));
                            continue;
                        }
                        string fam = GetString(c, "SerializableFamily") ?? "";
                        if (!haveBaseline) { baseline = fam; haveBaseline = true; }
                        else if (!string.Equals(fam, baseline, StringComparison.OrdinalIgnoreCase)) // StaticString equality is case-insensitive
                        {
                            string src = source.TryGetValue(name, out var s) ? s : "vanilla";
                            findings.Add(Err(":4734", carrierName,
                                $"unlock event in {carrierType} '{carrierName}' mixes families: '{name}' (from {src}) is family "
                                + $"'{Show(fam)}' but a sibling in the same event is '{Show(baseline)}'. Game refuses to load."));
                        }
                    }
                }
                else if (tUnlockResource != null && tResource != null && tUnlockResource.IsInstanceOfType(effect))
                {
                    // Guarded on tResource: if the resource type didn't resolve we have no base to check
                    // against, and flagging every resource as "not found" would be noise.
                    foreach (var name in RefNames(effect, "ResourceReferences"))
                        if (!resource.ContainsKey(name))
                            findings.Add(Err(":4747", carrierName,
                                $"{carrierType} '{carrierName}' unlocks resource '{name}', which neither vanilla nor any loaded mod defines. Game refuses to load."));
                }
            }
        }

        // Replay InitializePresentationUnitDefinitions (:4299 + :4369): each PresentationPawnDefinition
        // and PresentationSecondaryPawnDefinition must reference a PresentationUnitDefinition that exists
        // in the merged set.
        void EvaluatePresentation(Dictionary<string, Object> presentationUnit, Dictionary<string, Object> presentationPawn,
                                  Dictionary<string, Object> presentationMount, Dictionary<string, string> source,
                                  List<Finding> findings)
        {
            if (tPresentationPawn == null || tPresentationUnit == null) return;
            foreach (var kv in presentationPawn)
            {
                var pawn = kv.Value;
                var refName = GetDatatableElementRefName(pawn, "PresentationUnitDefinition");
                if (!string.IsNullOrEmpty(refName) && !presentationUnit.ContainsKey(refName))
                {
                    findings.Add(Err(":4299", refName + " / " + pawn.name,
                        $"PresentationPawnDefinition '{pawn.name}' references PresentationUnitDefinition '{refName}', "
                        + "which neither vanilla nor any loaded mod defines. Game refuses to load."));
                }
            }
            if (tPresentationMount == null) return;
            foreach (var kv in presentationMount)
            {
                var mount = kv.Value;
                var refName = GetDatatableElementRefName(mount, "PresentationUnitDefinition");
                if (!string.IsNullOrEmpty(refName) && !presentationUnit.ContainsKey(refName))
                {
                    findings.Add(Err(":4369", refName + " / " + mount.name,
                        $"PresentationSecondaryPawnDefinition '{mount.name}' references PresentationUnitDefinition '{refName}', "
                        + "which neither vanilla nor any loaded mod defines. Game refuses to load."));
                }
            }
        }

        // Replay InitializeFamilies (:4139 + :4144): for each Unit/SettlementImprovement family, within
        // each level all constructibles must share the same emblematic/common status. The first
        // constructible in a level sets the level type; subsequent ones must match.
        // Scoped to UnitDefinition + SettlementImprovementDefinition only — that's the only U pair
        // DataController.InitializeFamilies is invoked with (NationalProject families are never checked).
        void EvaluateFamilies(Dictionary<string, Object> construct, Dictionary<string, string> source,
                              List<Finding> findings)
        {
            if (tUnitDefinition == null && tSettlementImprovement == null) return;
            // Two separate family namespaces (UnitFamilyDefinition vs SettlementImprovementFamilyDefinition);
            // bucket per constructible type so a shared family *string* can't falsely mix them.
            EvaluateFamiliesOfType(construct, tUnitDefinition, findings);
            EvaluateFamiliesOfType(construct, tSettlementImprovement, findings);
        }

        static void EvaluateFamiliesOfType(Dictionary<string, Object> construct, Type elementType,
                                           List<Finding> findings)
        {
            if (elementType == null) return;
            var byFamilyLevel = new Dictionary<string, Dictionary<int, List<object>>>();
            foreach (var kv in construct)
            {
                if (!elementType.IsInstanceOfType(kv.Value)) continue;
                var family = GetString(kv.Value, "SerializableFamily");
                if (string.IsNullOrEmpty(family)) continue;
                var levelObj = kv.Value.GetType().GetField("Level", ALL)?.GetValue(kv.Value);
                if (levelObj is not int level) continue;
                if (!byFamilyLevel.TryGetValue(family, out var levels))
                {
                    levels = new Dictionary<int, List<object>>();
                    byFamilyLevel[family] = levels;
                }
                if (!levels.TryGetValue(level, out var list))
                {
                    list = new List<object>();
                    levels[level] = list;
                }
                list.Add(kv.Value);
            }
            foreach (var (family, levels) in byFamilyLevel)
            {
                foreach (var (level, list) in levels)
                {
                    if (list.Count < 2) continue;
                    bool firstIsEmblematic = HasFactionPrerequisite(list[0]);
                    for (int i = 1; i < list.Count; i++)
                    {
                        bool isEmblematic = HasFactionPrerequisite(list[i]);
                        var c = (Object)list[i];
                        if (firstIsEmblematic && !isEmblematic)
                        {
                            findings.Add(Err(":4144", c.name,
                                $"Common {c.GetType().Name} '{c.name}' in emblematic level "
                                + $"(family '{family}', level {level}). Game refuses to load."));
                        }
                        else if (!firstIsEmblematic && isEmblematic)
                        {
                            findings.Add(Err(":4139", c.name,
                                $"Emblematic {c.GetType().Name} '{c.name}' in common level "
                                + $"(family '{family}', level {level}). Game refuses to load."));
                        }
                    }
                }
            }
        }

        // Matches InitializeFamilies' FactionNames.Length check. Read the *serialized* field, not the
        // runtime FactionNames property — that property is only filled by InitializeStaticStrings(),
        // which vanilla mounts never get and bundle mods do. Using FactionNames produced mass false
        // :4139s (uninitialized vanilla emblematics looked "common", initialized mod ones "emblematic").
        static bool HasFactionPrerequisite(object constructible)
        {
            var fpField = constructible.GetType().GetField("FactionPrerequisite", ALL);
            if (fpField == null) return false;
            var fp = fpField.GetValue(constructible);
            if (fp == null) return false;
            var namesField = fp.GetType().GetField("serializableFactionNames", ALL);
            if (namesField == null) return false;
            return namesField.GetValue(fp) is Array { Length: > 0 };
        }

        // ---- reflection helpers ------------------------------------------

        static Finding Err(string code, string element, string detail) =>
            new Finding { Severity = FindingSeverity.Error, Code = code, Element = element, Detail = detail };

        // The names referenced by a DatatableElementReference[] field (each ref's private serializableElementName).
        IEnumerable<string> RefNames(object effect, string field)
        {
            foreach (var refObj in ReadArray(effect, field))
            {
                if (refObj == null) continue;
                var n = GetString(refObj, "serializableElementName");
                if (!string.IsNullOrEmpty(n)) yield return n;
            }
        }

        // Read an array/collection field by name into a materialized list (empty if absent/null).
        static IReadOnlyList<object> ReadArray(object obj, string field)
        {
            if (obj == null) return Array.Empty<object>();
            var val = obj.GetType().GetField(field, ALL)?.GetValue(obj);
            if (val is System.Collections.IEnumerable en && !(val is string))
                return en.Cast<object>().ToList();
            return Array.Empty<object>();
        }

        static string GetString(object obj, string field)
        {
            if (obj == null) return null;
            var fi = obj.GetType().GetField(field, ALL);
            return fi?.GetValue(obj) as string;
        }

        static string Show(string fam) => string.IsNullOrEmpty(fam) ? "(none)" : fam;

        // Read the ElementName from a single DatatableElementReference field on an object.
        static string GetDatatableElementRefName(object obj, string field)
        {
            if (obj == null) return null;
            var fi = obj.GetType().GetField(field, ALL);
            if (fi == null) return null;
            var refObj = fi.GetValue(obj);
            if (refObj == null) return null;
            return GetString(refObj, "serializableElementName");
        }

        // guid:fileID (mod element m_Script) -> concrete element Type, via the script's MonoScript.
        // Also accepts a CLR FullName fallback (LiveElementBuilder when MonoScript lookup fails).
        Type ResolveType(string guidFileId)
        {
            if (string.IsNullOrEmpty(guidFileId)) return null;
            if (_typeCache.TryGetValue(guidFileId, out var cached)) return cached;
            Type result = null;
            int c = guidFileId.IndexOf(':');
            if (c < 0)
            {
                // FullName / type name — not a MonoScript key.
                result = FindType(guidFileId);
            }
            else
            {
                string guid = guidFileId.Substring(0, c);
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                        if (o is MonoScript ms
                            && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ms, out var g, out long fid)
                            && (g + ":" + fid) == guidFileId)
                        { result = ms.GetClass(); break; }
            }
            _typeCache[guidFileId] = result;
            return result;
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

        public void Dispose() { /* staging is cleaned per Evaluate; vanilla is owned by the mount */ }
    }
}
