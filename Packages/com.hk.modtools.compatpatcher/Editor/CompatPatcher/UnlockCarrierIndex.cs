using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Amplitude.Framework;
using Amplitude.Framework.Asset;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HK.CompatPatcher
{
    public enum UnlockRefKind { VanillaReference, NewModReference }

    /// <summary>
    /// Reverse carrier map + vanilla/mod classification for CompatPatcher DiffGui.
    /// Covers UnlockConstructible / UnlockResource / ApplyDescriptor / ApplyCostModifier refs.
    /// Resources: vanilla tables often contain empty stubs — only “active” resources
    /// (unlocked by vanilla, or look filled) count as VanillaReference.
    /// </summary>
    public sealed class UnlockCarrierIndex
    {
        const string NS = "Amplitude.Mercury.Data.Simulation.";
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // All vanilla constructible / descriptor / cost-modifier names (+ every resource name).
        readonly HashSet<string> _vanillaNames = new HashSet<string>(StringComparer.Ordinal);
        // Resource names that are stubs in the vanilla table (present but unused/unfilled).
        readonly HashSet<string> _vanillaResourceNames = new HashSet<string>(StringComparer.Ordinal);
        // Resources that vanilla actually uses (UnlockResource on a vanilla carrier) or that look filled.
        readonly HashSet<string> _vanillaActiveResources = new HashSet<string>(StringComparer.Ordinal);

        // refName → modName → set of carrier element names
        readonly Dictionary<string, Dictionary<string, HashSet<string>>> _byUnlock =
            new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);

        Dictionary<string, string> _loc; // %key → text (vanilla archive + project overrides)
        readonly Dictionary<string, string> _elementLabelCache =
            new Dictionary<string, string>(StringComparer.Ordinal);
        // modName → (%key → text) read from that mod's own LocalizedStringElement collections.
        // Assetbundle sources only — other source types would need staging to read their loc, so
        // their %keys stay raw (unresolved) in the diff, same as before.
        readonly Dictionary<string, Dictionary<string, string>> _modLoc =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        // element base name → resolved UIMapper Title text. Built once (full scan of the mapper
        // types); without it every un-localized element — every unit, since the mappers are
        // Tech/Civic/NationalProject — re-scanned all three types, so scrolling a type list paid an
        // O(elements × assets) hit the first time each row was revealed.
        Dictionary<string, string> _uiMapperTitles;

        public bool VanillaAvailable { get; private set; }
        public string Note { get; private set; }

        public static UnlockCarrierIndex Build(IEnumerable<HkMod> mods)
        {
            var idx = new UnlockCarrierIndex();
            idx.LoadVanillaNames();
            if (mods != null)
            {
                foreach (var mod in mods)
                {
                    if (mod == null) continue;
                    idx.ScanMod(mod);
                    idx.LoadModLocalization(mod);
                }
            }
            return idx;
        }

        public bool IsVanilla(string name) =>
            Classify(name) == UnlockRefKind.VanillaReference;

        public UnlockRefKind Classify(string name)
        {
            if (string.IsNullOrEmpty(name)) return UnlockRefKind.NewModReference;
            if (!_vanillaNames.Contains(name)) return UnlockRefKind.NewModReference;
            // Resource stubs: name exists in vanilla but never unlocked / not filled → treat as mod-new
            // for DiffGui (culture packs often “activate” empty resource slots).
            if (_vanillaResourceNames.Contains(name) && !_vanillaActiveResources.Contains(name))
                return UnlockRefKind.NewModReference;
            return UnlockRefKind.VanillaReference;
        }

        public string KindTag(string name) =>
            Classify(name) == UnlockRefKind.VanillaReference ? "vanilla" : "mod";

        /// <summary>Ref names in EffectId (type|target|refs+…).</summary>
        public static List<string> ParseUnlockNamesFromEffectIdValue(string effectIdValue)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(effectIdValue)) return list;
            var parts = effectIdValue.Split('|');
            if (parts.Length < 3 || !IsClassifiableEffectId(effectIdValue)) return list;
            foreach (var piece in parts[2].Split('+'))
            {
                if (string.IsNullOrEmpty(piece)) continue;
                if (int.TryParse(piece, out _)) continue;
                list.Add(piece);
            }
            return list;
        }

        public static bool TryGetEffectIdValue(string groupKey, out string effectIdValue)
        {
            effectIdValue = null;
            if (string.IsNullOrEmpty(groupKey)) return false;
            int at = groupKey.LastIndexOf("[EffectId=", StringComparison.Ordinal);
            if (at < 0) return false;
            int rb = groupKey.IndexOf(']', at);
            if (rb <= at) return false;
            effectIdValue = groupKey.Substring(at + "[EffectId=".Length, rb - (at + "[EffectId=".Length));
            return true;
        }

        public static bool IsUnlockEffectId(string effectIdValue) =>
            IsClassifiableEffectId(effectIdValue);

        /// <summary>
        /// Effects whose EffectId refs get Vanilla/Mod split + reverse-carrier annotations.
        /// </summary>
        public static bool IsClassifiableEffectId(string effectIdValue)
        {
            if (string.IsNullOrEmpty(effectIdValue)) return false;
            int pipe = effectIdValue.IndexOf('|');
            string type = pipe < 0 ? effectIdValue : effectIdValue.Substring(0, pipe);
            return type == "UnlockConstructible"
                   || type == "UnlockResource"
                   || type == "ApplyDescriptor"
                   || type == "ApplyCostModifier";
        }

        /// <summary>
        /// All constructible/resource/descriptor/cost-modifier names referenced by classifiable
        /// EffectIds on one element's Flatten map (any unlock list shape — single or merged).
        /// </summary>
        public static HashSet<string> CollectClassifiableRefNamesFromFlat(Dictionary<string, string> flat)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (flat == null) return set;
            foreach (var path in flat.Keys)
            {
                int at = path.IndexOf("[EffectId=", StringComparison.Ordinal);
                if (at < 0) continue;
                int rb = path.IndexOf(']', at);
                if (rb < 0) continue;
                string id = path.Substring(at + "[EffectId=".Length, rb - (at + "[EffectId=".Length));
                if (!IsClassifiableEffectId(id)) continue;
                foreach (var n in ParseUnlockNamesFromEffectIdValue(id))
                    set.Add(n);
            }
            return set;
        }

        public static HashSet<string> CollectClassifiableRefNamesFromElement(HkElement el)
        {
            if (el == null) return new HashSet<string>(StringComparer.Ordinal);
            var flat = el.Flat;
            if (flat == null && el.Body != null) flat = UnityYaml.Flatten(el.Body);
            return CollectClassifiableRefNamesFromFlat(flat);
        }

        /// <summary>
        /// True when a MISSING EffectId's refs still appear on the winner's flatten of the same
        /// element (e.g. VIP had UnitA alone; winner merged UnitA+UnitB into one unlock).
        /// </summary>
        public static bool IsRedesignedMissing(
            string effectIdValue,
            HashSet<string> winnerRefsOnThisElement)
        {
            if (winnerRefsOnThisElement == null || winnerRefsOnThisElement.Count == 0) return false;
            if (!IsClassifiableEffectId(effectIdValue)) return false;
            var names = ParseUnlockNamesFromEffectIdValue(effectIdValue);
            if (names.Count == 0) return false;
            foreach (var n in names)
                if (!winnerRefsOnThisElement.Contains(n)) return false;
            return true;
        }

        public string FormatVanillaModSplit(IEnumerable<string> unlockNames)
        {
            var vanilla = new List<string>();
            var mod = new List<string>();
            foreach (var n in unlockNames ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (IsVanilla(n)) vanilla.Add(ShortName(n));
                else mod.Add(ShortName(n));
            }
            if (vanilla.Count == 0 && mod.Count == 0) return null;
            var sb = new StringBuilder();
            if (vanilla.Count > 0)
                sb.Append("Vanilla: ").Append(string.Join(", ", vanilla));
            if (mod.Count > 0)
            {
                if (sb.Length > 0) sb.Append("  ·  ");
                sb.Append("Mod: ").Append(string.Join(", ", mod));
            }
            return sb.ToString();
        }

        public List<string> FormatInterestingAnnotations(
            IEnumerable<string> unlockNames,
            string currentCarrier)
        {
            var lines = new List<string>();
            foreach (var name in unlockNames ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(name)) continue;
                string line = FormatAnnotation(name, currentCarrier);
                if (!string.IsNullOrEmpty(line)) lines.Add(line);
            }
            return lines;
        }

        public string FormatAnnotation(string unlockName, string currentCarrier)
        {
            if (string.IsNullOrEmpty(unlockName)) return null;
            if (!_byUnlock.TryGetValue(unlockName, out var byMod) || byMod.Count == 0)
                return null;

            var pairs = new List<(string mod, string carrier)>();
            var distinctCarriers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in byMod.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                foreach (var c in kv.Value.OrderBy(x => x, StringComparer.Ordinal))
                {
                    pairs.Add((kv.Key, c));
                    distinctCarriers.Add(c);
                }
            }

            string tag = KindTag(unlockName);
            string shortN = ShortName(unlockName);

            if (distinctCarriers.Count > 1)
            {
                var bits = pairs.Select(p => p.mod + " → " + FormatElementLabel(p.carrier));
                return shortN + " [" + tag + "] · on: " + string.Join(" · ", bits);
            }

            string only = distinctCarriers.First();
            if (!string.IsNullOrEmpty(currentCarrier)
                && !string.Equals(only, currentCarrier, StringComparison.Ordinal))
            {
                var bits = pairs.Select(p => p.mod + " → " + FormatElementLabel(p.carrier));
                return shortN + " [" + tag + "] · on: " + string.Join(" · ", bits);
            }

            if (!string.IsNullOrEmpty(currentCarrier)
                && string.Equals(only, currentCarrier, StringComparison.Ordinal)
                && byMod.Count == 1
                && Classify(unlockName) == UnlockRefKind.NewModReference)
            {
                string mod = byMod.Keys.First();
                return shortN + " [" + tag + "] · only on this tech in " + mod;
            }

            return null;
        }

        /// <summary>
        /// When winner shows absent on this tech's unlock entry, but still unlocks the unit
        /// elsewhere: <c>(absent) → Unlock in Technology_X (Localized Title)</c>.
        /// </summary>
        public string FormatAbsentUnlockRedirect(string diffPath, string winnerMod, string currentCarrier)
        {
            if (string.IsNullOrEmpty(winnerMod) || string.IsNullOrEmpty(diffPath)) return null;
            int at = diffPath.IndexOf("[EffectId=", StringComparison.Ordinal);
            if (at < 0) return null;
            int rb = diffPath.IndexOf(']', at);
            if (rb < 0) return null;
            string id = diffPath.Substring(at + "[EffectId=".Length, rb - (at + "[EffectId=".Length));
            if (!IsClassifiableEffectId(id)) return null;

            var targets = new List<string>();
            foreach (var unlockName in ParseUnlockNamesFromEffectIdValue(id))
            {
                if (!_byUnlock.TryGetValue(unlockName, out var byMod)) continue;
                if (!byMod.TryGetValue(winnerMod, out var carriers)) continue;
                foreach (var c in carriers.OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (!string.IsNullOrEmpty(currentCarrier)
                        && string.Equals(c, currentCarrier, StringComparison.Ordinal))
                        continue;
                    string label = FormatElementLabel(c);
                    if (!targets.Contains(label)) targets.Add(label);
                }
            }
            if (targets.Count == 0) return null;
            return DiffGuiAbsentPrefix + string.Join(", ", targets);
        }

        const string DiffGuiAbsentPrefix = "(absent) → Unlock in ";

        /// <summary><c>Technology_Era6_07 (Fire Control Systems)</c> when loc resolves.</summary>
        public string FormatElementLabel(string elementName)
        {
            if (string.IsNullOrEmpty(elementName)) return elementName ?? "";
            if (_elementLabelCache.TryGetValue(elementName, out var cached)) return cached;

            string loc = ResolveLocalizedTitle(elementName);
            string label = string.IsNullOrEmpty(loc) ? elementName : elementName + " (" + loc + ")";
            _elementLabelCache[elementName] = label;
            return label;
        }

        string ResolveLocalizedTitle(string elementName)
        {
            EnsureLoc();
            if (_loc != null)
            {
                foreach (var key in TitleKeyCandidates(elementName))
                {
                    if (_loc.TryGetValue(key, out var text) && !string.IsNullOrEmpty(text))
                        return text.Trim();
                }
            }
            return ResolveTitleFromUiMapper(elementName);
        }

        static IEnumerable<string> TitleKeyCandidates(string elementName)
        {
            yield return "%" + elementName + "Title";
            yield return "%" + elementName + "_Title";
            // Some rows use the bare element id as the localization Id.
            yield return "%" + elementName;
            yield return elementName + "Title";
        }

        string ResolveTitleFromUiMapper(string elementName)
        {
            EnsureUiMapperTitles();
            return _uiMapperTitles.TryGetValue(elementName, out var text) ? text : null;
        }

        /// <summary>
        /// Scan the UIMapper types once and build element-name → Title-text. Keyed by both the
        /// mapper's own name and its base name (minus the <c>UIMapper</c>/<c>_UIMapper</c> suffix)
        /// so a bare element id resolves — the inverse of the old per-element name comparison.
        /// </summary>
        void EnsureUiMapperTitles()
        {
            if (_uiMapperTitles != null) return;
            EnsureLoc();
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var typeName in new[]
                     {
                         "Amplitude.Mercury.UI.TechnologyUIMapper",
                         "Amplitude.Mercury.UI.CivicUIMapper",
                         "Amplitude.Mercury.UI.NationalProjectUIMapper",
                     })
            {
                var t = FindType(typeName);
                if (t == null) continue;
                foreach (var o in VanillaDatabaseMount.LoadAllOfType(t))
                {
                    if (o == null || string.IsNullOrEmpty(o.name)) continue;
                    string titleKey;
                    try
                    {
                        var f = FindField(o.GetType(), "Title");
                        titleKey = f?.GetValue(o) as string;
                    }
                    catch { continue; }
                    if (string.IsNullOrEmpty(titleKey)) continue;

                    string text;
                    if (_loc != null && _loc.TryGetValue(titleKey, out var loc) && !string.IsNullOrEmpty(loc))
                        text = loc.Trim();
                    else if (!titleKey.StartsWith("%", StringComparison.Ordinal))
                        text = titleKey.Trim(); // Title field sometimes stores plain text already.
                    else
                        continue;

                    foreach (var baseName in MapperBaseNames(o.name))
                        if (!map.ContainsKey(baseName)) map[baseName] = text;
                }
            }
            _uiMapperTitles = map;
        }

        static IEnumerable<string> MapperBaseNames(string mapperName)
        {
            yield return mapperName;
            if (mapperName.EndsWith("_UIMapper", StringComparison.Ordinal))
                yield return mapperName.Substring(0, mapperName.Length - "_UIMapper".Length);
            else if (mapperName.EndsWith("UIMapper", StringComparison.Ordinal))
                yield return mapperName.Substring(0, mapperName.Length - "UIMapper".Length);
        }

        /// <summary>
        /// Resolve a raw diff field value that is a localization key (<c>%…</c>) to display text,
        /// preferring the owning mod's own localization (assetbundle sources) and falling back to
        /// the vanilla/project dictionary. Returns null when the value isn't a <c>%key</c> or nothing
        /// resolves — the caller then shows the raw key unchanged.
        /// </summary>
        public string ResolveLocalizedValue(string modName, string rawValue)
        {
            if (string.IsNullOrEmpty(rawValue) || rawValue[0] != '%') return null;

            if (!string.IsNullOrEmpty(modName)
                && _modLoc.TryGetValue(modName, out var d)
                && d.TryGetValue(rawValue, out var t) && !string.IsNullOrEmpty(t))
                return t;

            EnsureLoc();
            if (_loc != null && _loc.TryGetValue(rawValue, out var g) && !string.IsNullOrEmpty(g))
                return g;
            return null;
        }

        /// <summary>
        /// Read a mod's own <c>LocalizedStringElement</c> collections (LineId → text) from its
        /// mounted assetbundle so mod-authored <c>%keys</c> resolve in the diff. Assetbundle sources
        /// only: other source types aren't mounted, so their loc would require staging. Best-effort —
        /// any failure leaves the mod's keys raw rather than breaking index build.
        /// </summary>
        void LoadModLocalization(HkMod mod)
        {
            if (mod == null || !mod.FromAssetBundle || string.IsNullOrEmpty(mod.Path)) return;
            if (!CompatBundleMounts.TryGetProvider(mod.Path, out var provider) || provider == null) return;

            Dictionary<string, string> dict = null;
            try
            {
                var descriptors = new List<AssetDescriptor>();
                provider.AddAllAssetDescriptors(descriptors, AssetProviderOption.AskForType);
                foreach (var desc in descriptors)
                {
                    var ct = desc.GetAssetType();
                    if (ct == null) continue;
                    // LocalizedStringElementCollection (mod runtime loc) / LocalizedStringTranslationCollection.
                    if ((ct.Name ?? "").IndexOf("LocalizedString", StringComparison.Ordinal) < 0) continue;
                    if (!typeof(DatatableElementCollection).IsAssignableFrom(ct)) continue;

                    DatatableElementCollection coll;
                    try { coll = provider.LoadAsset<DatatableElementCollection>(desc); }
                    catch { continue; }
                    if (coll == null) continue;
                    try { coll.Initialize(); } catch { }
                    var et = coll.DatatableElementType;
                    if (et == null) continue;

                    foreach (var el in provider.FetchAllSubAssetsOfType(desc.Guid, et))
                    {
                        if (el == null || ReferenceEquals(el, coll)) continue;
                        string key = GetLocKey(el);
                        if (string.IsNullOrEmpty(key)) continue;
                        string text = GetLocText(el);
                        if (string.IsNullOrEmpty(text)) continue;
                        (dict ??= new Dictionary<string, string>(StringComparer.Ordinal))[key] = text;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CompatPatcher] Reading localization from mod '{mod.Name}' failed: {e.Message}");
            }

            if (dict != null && dict.Count > 0)
                _modLoc[mod.Name] = dict;
        }

        // LocalizedStringElement.LineId (the %key). Falls back to Id / the Unity object name.
        static string GetLocKey(Object el)
        {
            string key = GetMember(el, "LineId") as string;
            if (string.IsNullOrEmpty(key)) key = GetMember(el, "Id") as string;
            if (string.IsNullOrEmpty(key) && el != null) key = el.name;
            return key;
        }

        // Concatenate CompactedNodes[].TextValue (usually one Terminal node). Falls back to a plain
        // Body/Text/Value string field for translation-shaped rows.
        static string GetLocText(Object el)
        {
            if (GetMember(el, "CompactedNodes") is System.Collections.IEnumerable nodes)
            {
                var sb = new StringBuilder();
                foreach (var n in nodes)
                {
                    if (n == null) continue;
                    if (GetMember(n, "TextValue") is string tv && tv.Length > 0) sb.Append(tv);
                }
                if (sb.Length > 0) return sb.ToString();
            }
            return (GetMember(el, "Body") as string)
                ?? (GetMember(el, "Text") as string)
                ?? (GetMember(el, "Value") as string);
        }

        static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            var f = FindField(obj.GetType(), name);
            if (f != null) { try { return f.GetValue(obj); } catch { } }
            var p = obj.GetType().GetProperty(name, All);
            if (p != null && p.CanRead) { try { return p.GetValue(obj); } catch { } }
            return null;
        }

        void EnsureLoc()
        {
            if (_loc != null) return;
            try
            {
                ArchiveTranslations.TryMount(out _);
                _loc = ArchiveTranslations.BuildKeyToTextDict()
                       ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch
            {
                _loc = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        static string ShortName(string n)
        {
            if (string.IsNullOrEmpty(n) || n.Length <= 48) return n;
            return n.Substring(0, 45) + "…";
        }

        void LoadVanillaNames()
        {
            if (!VanillaDatabaseMount.TryMount(out var err))
            {
                Note = "Vanilla not mounted (" + err + ") — all refs tagged as mod.";
                VanillaAvailable = false;
                return;
            }
            VanillaAvailable = true;

            AddVanillaNames(FindType(NS + "ConstructibleDefinition"));
            AddVanillaDescriptorNames();
            AddVanillaCostModifierNames();
            LoadVanillaResources();

            // Mark resources already unlocked by vanilla carriers as active.
            ScanVanillaUnlockResources();

            if (_vanillaNames.Count == 0)
                Note = "Vanilla mount ok but no definition names found.";
        }

        void AddVanillaNames(Type t)
        {
            if (t == null) return;
            foreach (var o in VanillaDatabaseMount.LoadAllOfType(t))
                if (o != null && !string.IsNullOrEmpty(o.name))
                    _vanillaNames.Add(o.name);
        }

        void AddVanillaDescriptorNames()
        {
            var tDesc = FindType(NS + "DescriptorDefinition");
            if (tDesc != null)
            {
                AddVanillaNames(tDesc);
                // Subclasses (EmpireDescriptor, …) may be separate ScriptableObject types.
                foreach (var t in FindTypesEndingWith("DescriptorDefinition"))
                    if (t != tDesc) AddVanillaNames(t);
            }
            else
            {
                foreach (var t in FindTypesEndingWith("DescriptorDefinition"))
                    AddVanillaNames(t);
            }
        }

        void AddVanillaCostModifierNames()
        {
            foreach (var t in FindTypesContaining("CostModifier"))
            {
                if (t == null || !typeof(ScriptableObject).IsAssignableFrom(t)) continue;
                string n = t.Name ?? "";
                if (!n.Contains("CostModifier", StringComparison.Ordinal)) continue;
                if (n.Contains("UIMapper", StringComparison.Ordinal)) continue;
                AddVanillaNames(t);
            }
            // Explicit known types (mod guide / load validations).
            AddVanillaNames(FindType(NS + "ResearchCostModifierDefinition"));
            AddVanillaNames(FindType(NS + "ConstructibleCostModifierDefinition"));
        }

        void LoadVanillaResources()
        {
            var tResource = FindType(NS + "ResourceDefinition");
            if (tResource == null) return;
            foreach (var o in VanillaDatabaseMount.LoadAllOfType(tResource))
            {
                if (o == null || string.IsNullOrEmpty(o.name)) continue;
                _vanillaNames.Add(o.name);
                _vanillaResourceNames.Add(o.name);
                if (LooksFilledResource(o))
                    _vanillaActiveResources.Add(o.name);
            }
        }

        /// <summary>
        /// Vanilla ResourceDefinition rows are often empty placeholders. Prefer rows that already
        /// carry localization / icon / nested refs over name-only stubs.
        /// </summary>
        static bool LooksFilledResource(Object o)
        {
            if (o == null) return false;
            foreach (var f in EnumerateInstanceFields(o.GetType()))
            {
                if (f.IsStatic) continue;
                string fn = f.Name ?? "";
                if (fn == "name" || fn == "Key" || fn == "m_Name" || fn == "hideFlags") continue;
                object val;
                try { val = f.GetValue(o); } catch { continue; }
                if (val == null) continue;

                if (val is string s)
                {
                    if (s.Length == 0) continue;
                    // Loc keys or any non-empty gameplay string.
                    return true;
                }
                if (val is bool) continue; // flags alone don't mean “filled”
                if (val is byte or sbyte or short or ushort or int or uint or long or ulong)
                {
                    // Non-zero numeric often means configured yield/tier.
                    try
                    {
                        if (Convert.ToInt64(val) != 0) return true;
                    }
                    catch { /* ignore */ }
                    continue;
                }
                if (val.GetType().IsEnum)
                {
                    try
                    {
                        if (Convert.ToInt64(val) != 0) return true;
                    }
                    catch { /* ignore */ }
                    continue;
                }

                string refN = SimulationEventEffectFlattener.ResolveRefName(val);
                if (!string.IsNullOrEmpty(refN)) return true;

                if (val is System.Collections.IEnumerable en && val is not string)
                {
                    foreach (var item in en)
                    {
                        if (item == null) continue;
                        if (!string.IsNullOrEmpty(SimulationEventEffectFlattener.ResolveRefName(item)))
                            return true;
                        if (item is string es && es.Length > 0) return true;
                    }
                }
            }
            return false;
        }

        void ScanVanillaUnlockResources()
        {
            foreach (var carrierType in new[]
                     {
                         FindType(NS + "TechnologyDefinition"),
                         FindType(NS + "CivicDefinition"),
                         FindType(NS + "NationalProjectDefinition"),
                         FindType(NS + "NationalProject_Leveling"),
                     })
            {
                if (carrierType == null) continue;
                foreach (var live in VanillaDatabaseMount.LoadAllOfType(carrierType))
                {
                    if (live == null) continue;
                    foreach (var effects in EnumerateEffectArrays(live))
                    {
                        if (effects == null) continue;
                        foreach (var effect in effects)
                        {
                            if (effect == null) continue;
                            if (!(effect.GetType().Name ?? "").Contains("UnlockResource")) continue;
                            var f = FindField(effect.GetType(), "ResourceReferences");
                            if (f == null) continue;
                            object raw;
                            try { raw = f.GetValue(effect); } catch { continue; }
                            if (raw is not System.Collections.IEnumerable en || raw is string) continue;
                            foreach (var item in en)
                            {
                                string n = SimulationEventEffectFlattener.ResolveRefName(item);
                                if (!string.IsNullOrEmpty(n))
                                    _vanillaActiveResources.Add(n);
                            }
                        }
                    }
                }
            }
        }

        void ScanMod(HkMod mod)
        {
            foreach (var el in mod.Elements.Values)
            {
                if (el == null || el.IsRoot || string.IsNullOrEmpty(el.Name)) continue;
                if (el.LiveObject != null)
                    ScanLiveCarrier(mod.Name, el.Name, el.LiveObject);
                else
                    ScanFlatFallback(mod.Name, el.Name, el);
            }
        }

        void ScanLiveCarrier(string modName, string carrierName, Object live)
        {
            foreach (var effects in EnumerateEffectArrays(live))
            {
                if (effects == null) continue;
                foreach (var effect in effects)
                {
                    if (effect == null) continue;
                    foreach (var n in HarvestEffectRefNames(effect))
                        Add(n, modName, carrierName);
                }
            }
        }

        static IEnumerable<string> HarvestEffectRefNames(object effect)
        {
            var t = effect.GetType();
            string typeName = t.Name ?? "";

            if (typeName.Contains("UnlockConstructible"))
            {
                foreach (var n in HarvestRefArray(effect, "ConstructibleReferences"))
                    yield return n;
                yield break;
            }
            if (typeName.Contains("UnlockResource"))
            {
                foreach (var n in HarvestRefArray(effect, "ResourceReferences"))
                    yield return n;
                yield break;
            }
            if (typeName.Contains("ApplyDescriptor"))
            {
                string n = HarvestSingleRef(effect, "Descriptor");
                if (!string.IsNullOrEmpty(n)) yield return n;
                yield break;
            }
            if (typeName.Contains("ApplyCostModifier"))
            {
                string n = HarvestSingleRef(effect, "CostModifierReference")
                           ?? HarvestSingleRef(effect, "CostModifier");
                if (!string.IsNullOrEmpty(n)) yield return n;
            }
        }

        static IEnumerable<string> HarvestRefArray(object effect, string field)
        {
            var f = FindField(effect.GetType(), field);
            if (f == null) yield break;
            object raw;
            try { raw = f.GetValue(effect); } catch { yield break; }
            if (raw is not System.Collections.IEnumerable en || raw is string) yield break;
            foreach (var item in en)
            {
                string n = SimulationEventEffectFlattener.ResolveRefName(item);
                if (!string.IsNullOrEmpty(n)) yield return n;
            }
        }

        static string HarvestSingleRef(object effect, string field)
        {
            var f = FindField(effect.GetType(), field);
            if (f == null) return null;
            object raw;
            try { raw = f.GetValue(effect); } catch { return null; }
            return SimulationEventEffectFlattener.ResolveRefName(raw);
        }

        void ScanFlatFallback(string modName, string carrierName, HkElement el)
        {
            var flat = el.Flat;
            if (flat == null && el.Body != null) flat = UnityYaml.Flatten(el.Body);
            if (flat == null) return;
            foreach (var path in flat.Keys)
            {
                int at = path.IndexOf("[EffectId=", StringComparison.Ordinal);
                if (at < 0) continue;
                int rb = path.IndexOf(']', at);
                if (rb < 0) continue;
                string id = path.Substring(at + "[EffectId=".Length, rb - (at + "[EffectId=".Length));
                if (!IsClassifiableEffectId(id)) continue;
                foreach (var n in ParseUnlockNamesFromEffectIdValue(id))
                    Add(n, modName, carrierName);
            }
        }

        void Add(string unlockName, string modName, string carrierName)
        {
            if (!_byUnlock.TryGetValue(unlockName, out var byMod))
            {
                byMod = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                _byUnlock[unlockName] = byMod;
            }
            if (!byMod.TryGetValue(modName, out var carriers))
            {
                carriers = new HashSet<string>(StringComparer.Ordinal);
                byMod[modName] = carriers;
            }
            carriers.Add(carrierName);
        }

        static IEnumerable<object[]> EnumerateEffectArrays(object element)
        {
            if (element == null) yield break;
            var type = element.GetType();
            foreach (var name in new[] { "SimulationEventEffects", "Effects", "EffectByLevels" })
            {
                var f = FindField(type, name);
                if (f == null) continue;
                object raw;
                try { raw = f.GetValue(element); } catch { continue; }
                if (raw is System.Collections.IEnumerable en && raw is not string)
                    yield return Materialize(en);
            }

            var repeating = FindField(type, "RepeatingEffect");
            if (repeating != null)
            {
                var one = repeating.GetValue(element);
                if (one != null) yield return new[] { one };
            }

            var choicesField = FindField(type, "Choices");
            if (choicesField != null && choicesField.GetValue(element) is System.Collections.IEnumerable choices)
            {
                foreach (var choice in choices)
                {
                    if (choice == null) continue;
                    var ct = choice.GetType();
                    var cf = FindField(ct, "SimulationEventEffects");
                    if (cf != null && cf.GetValue(choice) is System.Collections.IEnumerable ca)
                        yield return Materialize(ca);

                    var nef = FindField(ct, "NarrativeEventEffects");
                    if (nef != null && nef.GetValue(choice) is System.Collections.IEnumerable wrappers)
                    {
                        var unwrapped = new List<object>();
                        foreach (var w in wrappers)
                        {
                            if (w == null) continue;
                            var sef = FindField(w.GetType(), "SimulationEventEffect")?.GetValue(w);
                            if (sef != null) unwrapped.Add(sef);
                        }
                        if (unwrapped.Count > 0) yield return unwrapped.ToArray();
                    }
                }
            }

            var lootsField = FindField(type, "Loots");
            if (lootsField != null && lootsField.GetValue(element) is System.Collections.IEnumerable loots)
            {
                foreach (var loot in loots)
                {
                    if (loot == null) continue;
                    var sef = FindField(loot.GetType(), "SimulationEventEffects");
                    if (sef != null && sef.GetValue(loot) is System.Collections.IEnumerable en)
                        yield return Materialize(en);
                }
            }
        }

        static object[] Materialize(System.Collections.IEnumerable en)
        {
            var list = new List<object>();
            foreach (var x in en) if (x != null) list.Add(x);
            return list.ToArray();
        }

        static IEnumerable<FieldInfo> EnumerateInstanceFields(Type type)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(All | BindingFlags.DeclaredOnly))
                    if (!f.IsStatic) yield return f;
            }
        }

        static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return type?.GetField(name, All);
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName);
                    if (t != null) return t;
                }
                catch { /* skip */ }
            }
            return null;
        }

        static IEnumerable<Type> FindTypesEndingWith(string suffix)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                    if (t.Name.EndsWith(suffix, StringComparison.Ordinal))
                        yield return t;
                }
            }
        }

        static IEnumerable<Type> FindTypesContaining(string fragment)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t?.Name == null) continue;
                    if (t.Name.IndexOf(fragment, StringComparison.Ordinal) >= 0)
                        yield return t;
                }
            }
        }
    }
}
