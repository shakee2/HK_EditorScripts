# Humankind load-time validation rules (DataController)

> **Read this first — which of the 392 messages actually reset the game.**
> The full list below was harvested indiscriminately. In the shipped (retail) game **almost none of
> them can fire.** Two facts from the decompiled source decide it:
>
> 1. **The reset gate.** `RuntimeState_Bootstrapper.DoRun` wraps `DataController.Initialize()` (and
>    `SimulationController.Compile()`) and, for the duration, subscribes a handler:
>    ```csharp
>    Diagnostics.MessageLogged += Diagnostics_MessageLogged;   // ...
>    private void Diagnostics_MessageLogged(LogMessage m) {
>        if (m.LogLevel >= LogLevel.Error) throw new Exception(m.Message);
>    }
>    ```
>    So **any single `LogError` logged while `Initialize()` runs is turned into a thrown exception**,
>    caught by `DoRun`, sent to `RuntimeManagerHelper.HandleCrash` → the console shows the lilac
>    critical, then **`[RuntimeManager] Try to reload with vanilla configuration`**, and the mod list
>    is cleared. `LogWarning` (below `Error`) does **not** trip it. Because it throws on the *first*
>    error, the rest of `Initialize()` never runs — only the first error you hit matters.
>
> 2. **The `Check*` methods don't run in retail.** The `DataController.Check(bool)` wrapper that calls
>    `CheckConstructibles`, `CheckTechnologyDefinitions`, `CheckCivicDefinitions`, … (≈49 of the 52
>    methods, the bulk of the 392 messages) is marked **`[Conditional("DEBUG")]`** — the compiler
>    strips every call to it from the release build. Those errors only ever appear in an editor/DEBUG
>    build. **For players they cannot cause a reset.**
>
> **Net: the only reset-capable messages are the `LogError`s reachable from `DataController.Initialize()`
> (+ the `Set*` helpers it calls) — about 15 sites, listed in "Reset-capable errors" below.** Everything
> in the per-method dump after that is retained for reference/DEBUG only.

## What the patcher checks on Compare (implemented)

`LoadOrderValidator` (`Assets/Scripts/Editor/CompatPatcher/LoadOrderValidator.cs`) runs **every time you press
Compare**, in `CompatPatcherWindow.Validate()`. It validates the **real load order — `Vanilla → Mod A → Mod
B → …`** — not just the mods against each other, because the reset-capable crashes almost always fire against
*vanilla* siblings (the confirmed ENC+VIP case is a mod renaming one unit's family while vanilla's other units
in the same unlock event keep the old one).

**How the merge is built (uniform live objects, no Odin guessing):**
- **Vanilla base** = the mounted `MercuryDatabases` bundle, loaded as live objects via `VanillaDatabaseMount`
  (same source the Tech Tree / DatabaseBrowser use). Only the validation-relevant categories are loaded —
  constructibles, resources, techs, civics, presentation unit/pawn/mount — and the result is **cached for
  the whole session** (vanilla doesn't change), so only the first Compare pays the load. The cache
  self-invalidates on unmount/domain reload; clear it manually via *Tools ▸ Debug ▸ Compat Patcher ▸
  Clear Vanilla Validation Cache* after changing the Humankind folder.
- **Mod overlay** = each mod's constructibles/resources/techs/civics/presentation defs, overlaid by name
  in load order (last wins). **Assetbundle** mods reuse the session-mounted `LiveObject` directly (no
  `_PatcherStage`). **Folder / zip / unitypackage** mods still stage each source file to a live object
  (`PatchBuilder.StageSourceFile`) so Odin-serialized `SimulationEventEffects` deserialize correctly
  instead of being guessed from raw YAML. Overlay typing uses `LiveObject.GetType()` when present —
  MonoScript `guid:fileID` resolution alone is not enough for bundle-built `HkElement`s.
- Rules are then evaluated by reflection against the exact fields `DataController.Initialize` reads
  (`SimulationEventEffects` / `Choices` / `Effects` / `EffectByLevels` → `SimulationEventEffect_UnlockConstructible.
  ConstructibleReferences` / `..._UnlockResource.ResourceReferences`; `ConstructibleDefinition.SerializableFamily`).

**Implemented rules (all reset-capable → Error):**
- `:4713` — an unlock event references a **constructible** neither vanilla nor any loaded mod defines.
- `:4718` — an unlock event targets an **EmpireWideConstructionParticipationDefinition** (illegal target).
- `:4734` — constructibles in **one unlock event resolve to different families** (the confirmed ENC+VIP crash).
  The message names which constructible and where its family came from (vanilla / which mod).
- `:4747` — an unlock event references a **resource** neither vanilla nor any loaded mod defines.
- `:4299` — a **PresentationPawnDefinition** references a **PresentationUnitDefinition** that doesn't exist after merge.
- `:4369` — a **PresentationSecondaryPawnDefinition** (mount) references a **PresentationUnitDefinition** that doesn't exist after merge.
- `:4139` — an **emblematic** constructible (has faction prerequisites) placed in a **common** family level.
- `:4144` — a **common** constructible (no faction prerequisites) placed in an **emblematic** family level.

**Checked at every load step, not just the final merge.** The game applies mods incrementally and validates
as each one loads, resetting at the **first** bad step. So the validator evaluates each cumulative prefix —
`Vanilla`, then `+Mod A`, then `+Mod A+B`, … — and reports a hazard the moment a step introduces it, attributing
it to that mod (`← resets when loading Mod B`). This matters because a hazard Mod B introduces **still resets
the game even if a later Mod C would have fixed it** in the fully-merged state; a final-merge-only check would
miss it. Vanilla's own baseline is evaluated first and excluded, so mods aren't blamed for pre-existing issues.

**Forward/reverse control.** If (and only if) the current order produces findings, the validator re-evaluates
the **reversed mod order** (vanilla stays the base — it is always loaded first) as a control. The result panel:

- each finding as an error/warning row, tagged **`[order-sensitive]`** when it appears or resolves
  differently under the reversed order;
- a summary `N error(s), M warning(s) · order-sensitive: K`;
- a **"Load order matters here"** warning when reordering would change the outcome, so the user can reorder
  with ▲▼ and press Compare again to re-validate.

Findings present in **both** orders are *intrinsic* (only a patch edit fixes them); findings that change
between orders are *order-caused* (the user can influence them by reordering).

**If vanilla can't be mounted** (Humankind folder unset) the panel says so and falls back to mods-only, with a
warning that vanilla-only hazards will be missed.

**Not yet automated** (see the reset-capable list below): the single-mod-internal quality checks. The rule
set is a straight-line replay in `LoadOrderValidator.Evaluate` / `EvaluateCarrier` / `EvaluatePresentation` /
`EvaluateFamilies` — add a check there.

## Reset-capable errors (retail — reachable from `Initialize()`)

These are the only `LogError`s that can clear the mod list in a shipped game. Grouped by how likely a
**combination of independently-fine mods** triggers them (which is what the patcher cares about).

**Cross-mod combination hazards — a checker should replay these over the merged, last-wins table:**
- `:4734` **Mixed families in one unlock event** — “Constructibles within the same unlock event must
  belong to the same family.” Per tech/civic/national-project, every `SimulationEventEffect_UnlockConstructible`'s
  constructibles must share `Family`. **This is the confirmed ENC+VIP crash** (VIP renamed a unit family
  but not ENC's culture spearman → mixed families when VIP loads last). *Confirmed real.*
- `:4713` **Constructible not found in an unlock event** — a tech/civic unlocks a constructible name that,
  in the merged set, no longer resolves (another mod renamed/removed it, or it only existed in a mod not present).
- `:4747` **Resource not found in an unlock event** — same, for `SimulationEventEffect_UnlockResource`.
- `:4718` Unlock event targets an `EmpireWideConstructionParticipationDefinition` (illegal unlock target).
- `:4299` / `:4369` **Unit/mount presentation definition invalid for a pawn** — a `PresentationUnitDefinition`
  referenced by name doesn't resolve after merge (visual mapper vs definition mismatch).
- `:4144` / `:4139` **Common constructible in an emblematic family level / emblematic in a common level** —
  family-level layout broken when mods redefine family membership.

**Single-mod-internal (each mod already passes alone; combining rarely changes them, but listed for completeness):**
- `:3989` / `:3994` / `:4002` statistic reporter: empty/dangling statistic reference, or missing DeedEvaluator.
- `:4020` / `:4034` / `:4048` / `:4057` DeedEvaluator: unresolvable `StartingType`, or unknown evaluator type.
- `:4080` constructible level < 0.
- `:4189` / `:4221` / `:4256` narrative-event prerequisite: unresolvable `StartingType`.
- `:4279` / `:4350` spawn weight of 0 in a retail build (`PresentationUnit`/`Mount`).

> Note the earlier draft's #2 (`:2074`), #3 (`:328`), #4 dangling-ref list, #5 category checks all live in
> `Check*` methods → **DEBUG-only, cannot reset a player's game.** They're still useful as *quality* warnings
> a checker could surface (they catch real modding mistakes), but they will not, by themselves, clear the mod
> list at launch. Keep that distinction in the checker UI: "will crash load" (the ~15 above) vs "may be wrong"
> (the DEBUG dump).

> Source: `Amplitude.Mercury.Data/Amplitude/Mercury/Data/DataController.cs` · 392 messages across 52 methods
> (only ~15 reset-capable in retail). Reset gate: `Assembly-CSharp/Amplitude/Mercury/Runtime/RuntimeState_Bootstrapper.cs:105,136`.
> Line refs like `:1234` are into `DataController.cs`.

## Quality warnings (DEBUG-only — will NOT reset a retail game)

⚠️ **Superseded by "Reset-capable errors" above.** The rules in this section live in `Check*` methods
that are `[Conditional("DEBUG")]` and are stripped from the shipped game, so on their own they **cannot
clear a player's mod list**. They remain valuable as *quality* signals a checker could surface (they
catch genuine modding mistakes), but flag them as "may be wrong", not "will crash load". In rough priority:

1. **Mixed constructible families in one unlock event** — `SetConstructibleAsNeededToBeUnlocked :4734`.
   For each technology/NationalProject, every `SimulationEventEffect_UnlockConstructible` must have all
   its constructibles resolve to the **same `SerializableFamily`**. This is the one that crashed ENC+VIP
   (VIP renamed a unit family but didn't cover ENC's culture unit). *Confirmed real.*
2. **A family split across multiple unlock events** — `CheckSimulationEventEffectsConstructibleFamilies :2074`.
   The inverse: all constructibles of a given family should be in **one** unlock event, not several.
3. **Duplicate element keys** — `CheckDefinitionWithKeysAgainstUniqueKeys :328`. Two mods assigning the
   same key to different elements collide in the merged set.
4. **Dangling references** — the many `'{0}' not found` / `prerequisite ... doesn't exist` messages
   (e.g. `CheckTechnologyDefinitions`, `CheckConstructibles`, prerequisite checks). A mod references a
   name that only existed in another mod that isn't present, or was renamed.
5. **Same-family / category consistency** in improvements, army patterns, cultures (see the per-method
   sections). Combining mods can violate these even when each is internally consistent.

A checker only needs the merged element table the patcher already builds (name+type+fields), plus the
`SerializableFamily` of each constructible and each technology's unlock-event constructible lists.


## (top)  (14)
- `:151` Missing EmpireStabilityDefinition(s).
- `:159` The stability definition '{0}' have a min range value higher than the max range value.
- `:175` The max value of '{0}' and the min value of '{1}' don't match.
- `:180` There is no stability defintion that include 0
- `:184` There is no stability defintion that include 100
- `:192` Missing PublicOrderEffectDefinition collection.
- `:200` The public order defintion '{0}' have a min range value higher than the max range value.
- `:215` The max value of '{0}' and the min value of '{1}' don't match.
- `:220` There is no public order defintion that include 0
- `:224` There is no public order defintion that include 100
- `:233` Unable to find the database (type: ).
- `:248` Unable to find the database (type: ).
- `:259` Unable to find the database (type: ).
- `:314` Invalid unique key for element '{element.Name}' key '{elementKey}'.

## CheckNotificationsUIMappers  (1)
- `:379` Invalid Category on NotificationUIMapper ' '. Please use only one category (current: ' ')

## CheckBattleEffects  (1)
- `:404` Invalid battle effect 'AddStatus' on ability '{datatableElement2.Name}'. Status added in battle must not use CancelOnApply functionality.

## CheckBattleCondition  (1)
- `:426` Invalid battle condition: descriptor is not valid for a unit. (BattleAbility={battleAbilityName}, BattleActionIndex={battleActionIndex}, BattleAction.Note={battleActionName}, Descriptor={battleCondition_HasDescriptor.Descriptor.ElementName}, Descriptor.StartingType={datatableElement.StartingType})

## CheckStatisticsAndAchievementsNaming  (4)
- `:450` Invalid name for ' '. Achievement and statistics should not use '_'.
- `:454` Invalid name for '{achievementName}'. Achievement and statistics should be under 32 char. charCount={achievementName.Length}
- `:462` Invalid name for ' '. Achievement and statistics should not use '_'.
- `:466` Invalid name for '{statisticName}'. Achievement and statistics should be under 32 char. charCount={statisticName.Length}

## CheckPatronageDefinitions  (6)
- `:477` Patronage's group definition is empty (name: {datatableElement.Name}).
- `:487` In patronage group definition '{datatableElement.Name}' Sway bulk cap from level '{i}' is out of range.
- `:491` In patronage group definition '{0}' the level '{1}' is not in ascending order because of {2}. SwayBulkCap
- `:495` In patronage group definition '{datatableElement.Name}' Sway share cap from level '{i}' is out of range 0-100.
- `:499` In patronage group definition '{0}' the level '{1}' is not in ascending order because of {2}. SwayShareCap
- `:503` In patronage group definition '{datatableElement.Name}' there is no descriptor for the level '{i}'.

## CheckCostModifierDefinitions  (1)
- `:529` Technology cost modifier {researchCostModifierDefinition.Name} CostType should be research, currently : {researchCostModifierDefinition.CostType.ToString()}.

## CheckNarratorDefinitions  (10)
- `:540` Invalid Narrator Notification Definition. The entry list is null for DefinitionList
- `:544` One of the Narrator Notification Definitions has \ as its type. This can cause a crash
- `:553` Invalid narrator notification definition. The AudioEventHandleReference is null for
- `:561` Invalid Narrator Camera Definition. The entry list is null for DefinitionList
- `:565` One of the Narrator Camera Sequence Definitions has \ as its type. This can cause a crash
- `:572` Invalid narrator notification Camera Sequence. The AudioEventHandleReference is null for
- `:584` Invalid Narrator Map Definition. The AudioEventHandleReference is null for SimpleNarratorMapDefinition with key .
- `:591` Invalid Narrator Civic Definition. The Civic name is empty.
- `:596` Invalid Narrator Civic Definition. Choices are null
- `:603` Invalid Narrator Civic Definition. Choice n° is null

## CheckTerrainTypeDefinitions  (2)
- `:615` Invalid terrain definition. The pathfinding rules array is null for ' '.
- `:619` Invalid terrain definition. The pathfinding rules array is empty for ' '.

## CheckFactionTraits  (6)
- `:636` Invalid faction trait for faction ' '. The trait is a prehistoric trait.
- `:640` Invalid faction trait for faction ' '. The trait is an upgrade trait.
- `:654` Invalid faction trait for faction ' '. The trait is a prehistoric trait.
- `:658` Invalid faction trait for faction ' '. The trait is an upgrade trait.
- `:696` Invalid upgrade trait for era ' '. The trait is a prehistoric trait.
- `:700` Invalid upgrade trait for era ' '. The trait is not an upgrade trait.

## CheckCuriosityLoot  (1)
- `:722` Invalid loot table for curiosity definition. (CuriosityDefinition={curiosityDefinition.Name}, LootTable={curiosityDefinition.LootTableReference.ElementName}

## CheckCivicDefinitions  (12)
- `:743` Civic definition '{datatableElement4.Name}' doesn't contain any choice.
- `:750` Civic definition '{datatableElement4.Name}' have an unvalid civic choice, civic choice name '{name}' is empty.
- `:755` Civic definition '{datatableElement4.Name}' have an unvalid civic choice list, choice '{name}' already exists.
- `:768` Civic definition '{datatableElement4.Name}' have an unvalid civic prerequisite, necessary civic definition '{datatableElement4.CivicPrerequisite.NecessaryCivicReference.ElementName}' not found.
- `:776` Civic definition '{datatableElement4.Name}' have an unvalid civic prerequisite, circular dependency detected.
- `:788` Civic definition '{datatableElement4.Name}' have an unvalid civic prerequisite, valid necessary choice '{staticString}' not found.
- `:792` Civic definition '{datatableElement4.Name}' have an unvalid civic prerequisite, contains serval times the same valid necessary choice '{staticString}'.
- `:802` Civic definition '{datatableElement4.Name}' have an unvalid civic prerequisite, forbidden civic definition '{datatableElement4.CivicPrerequisite.ForbiddenCivicReference.ElementName}' not found.
- `:807` Civic definition '{datatableElement4.Name}' have an unvalid civic prerequisite, forbidden civic '{datatableElement2.Name}' also forbid a civic.
- `:815` Civic definition '{datatableElement4.Name}' have an unvalid civic prerequisite, valid forbidden choice '{staticString2}' not found.
- `:819` Civic definition '{datatableElement4.Name}' have an unvalid civic prerequisite, contains serval times the same valid parent choice '{staticString2}'.
- `:829` Civic definition '{datatableElement4.Name}' is exclusive with '{datatableElement3.Name}' but the inverse isn't true.

## CheckDescriptors  (11)
- `:842` _(warn)_ [Data] Descriptor must have a starting type. DescriptorName={0}.
- `:851` _(warn)_ [Data] Descriptor effect should have a least one property effect. DescriptorName={0}.
- `:871` _(warn)_ [Data] Invalid RPN: you have 0 operation and the constant AND the property count are 0 too. DescriptorName= .
- `:875` _(warn)_ [Data] Invalid RPN: you have 0 operation and the constant AND the property count are grether than 0. DescriptorName= .
- `:881` _(warn)_ [DATA] Invalid RPN: you should not use RPN with only 1 constant. Please remove the RPN operation. DescriptorName= .
- `:903` _(warn)_ [Data] Invalid RPN: Operation {operation} need more than 2 value on the stack (stackCount={num8}). DescriptorName={datatableElement.name}.
- `:913` _(warn)_ [Data] Invalid RPN: Property does not exist on target type . DescriptorName= .
- `:925` _(warn)_ [Data] Invalid RPN: Property does not exist on source type . DescriptorName= .
- `:945` _(warn)_ [Data] Invalid RPN: missing operation. The RPN has more constants/properties than operation. DescriptorName= .
- `:949` _(warn)_ [Data] Invalid RPN: mismatch between constant count '{num3}' and 'GetConstant' operation count '{num7}'. DescriptorName={datatableElement.name}.
- `:953` _(warn)_ [Data] Invalid RPN: mismatch between property count '{num5}' and 'GetProperty' operation count '{num6}'. DescriptorName={datatableElement.name}.

## CheckSynergies  (1)
- `:970` Descriptor {datatableElement.Name} is a Synergy Descriptor but isn't using the Descriptor category {synergyCategoryName}, category used instead : {datatableElement.Category}

## CheckConstructibles  (37)
- `:1009` [DATA] Invalid production cost definition for '{0}'. Both 'RPN' reference and a 'Constant' value have been given.
- `:1013` [DATA] Invalid money instant cost definition for '{0}'. Both 'RPN' reference and a 'Constant' value have been given.
- `:1017` [DATA] Invalid influence instant cost definition for '{0}'. Both 'RPN' reference and a 'Constant' value have been given.
- `:1026` [DATA] Invalid resource access prerequisite for '{0}'. OverallDepositsPercentage is not supported anymore. Sorry.
- `:1030` [DATA] Invalid resource access prerequisite for '{0}'. The given 'Constant' value is negative.
- `:1036` Invalid faction prerequisite operator (='{datatableElement6.FactionPrerequisite.Operator}') in constructible '{datatableElement6.FactionPrerequisite.Operator}'.
- `:1045` Invalid null descriptor at position {j} for constructible '{datatableElement6.Name.ToString()}
- `:1052` Invalid wonder prerequisite. Only district may use 'BuiltInTerritory'. Constructible '
- `:1057` Invalid wonder prerequisite. A wonder is set '{datatableElement6.ArtificialWonderBuiltPrerequisite.ArtificialWonderDefinition.ElementName}', but the operator is 'None'. Constructible '{datatableElement6.Name.ToString()}
- `:1068` Invalid OutputThresholdPerEra length (= {additionalDistrictVisualLevel.OutputThresholdPerEra.Length}, expected = {eraCount}) in district definition '{districtDefinition.Name}' for additional level '{k}'.
- `:1077` Invalid output threshold (= {additionalDistrictVisualLevel.OutputThresholdPerEra[l]}, expected 'greater than 0') in district definition '{districtDefinition.Name}' for additional level '{k}' era {l}
- `:1091` Invalid output threshold (= {additionalDistrictVisualLevel.OutputThresholdPerEra[m]}, expected 'greater than {additionalDistrictVisualLevel2.OutputThresholdPerEra[m]}') in district definition '{districtDefinition.Name}' for additional level '{k}' era {m}
- `:1117` Extension district '{datatableElement6.Name}' can be built on water and replace the terrain type, which is forbiden.
- `:1122` Invalid unicity '{extensionDistrictDefinition.Unicity}' for extension '{extensionDistrictDefinition.Name}' which provide rails. Will be forced to 'OnePerTerritory'.
- `:1137` Invalid extension definition without descriptor ' '. (Definition= )
- `:1143` Invalid airport definition '{airportDefinition.Name}' unicity '{airportDefinition.Unicity}'. Will be forced to 'OnePerTerritory'.
- `:1152` invalid unitdefintion. (Unitdef={unitDefinition.Name})
- `:1156` Unit definition '{unitDefinition.Name.ToString()}' is not valid: VisualAffinityEraIndex = {unitDefinition.VisualAffinityEraIndex}.
- `:1161` Missing or invalid Unit class (unit class: '{unitDefinition.UnitClass.ElementName}' unit definition: '{unitDefinition.Name}').
- `:1179` A vehicle unit definition should have a compatible unit class and tagged as vehicle (unit class: '{datatableElement.Name}' is tagged: '{flag5}').
- `:1186` Unit definition ' ' is not valid. Spawn type is 'air' but it has missile or nuc strike abilities.
- `:1190` Unit definition ' ' is not valid. Spawn type is 'air' but it has no interceptor nor airstrike abilities.
- `:1197` Unit definition ' ' is not valid. Spawn type is 'Missile' but it has 'AirStrike' or 'Interceptor' abilities.
- `:1201` Unit definition ' ' is not valid. Spawn type is 'Missile' but it has no 'MissileStrike' nor 'NuclearStrike' abilities.
- `:1208` Unit definition '{unitDefinition.Name.ToString()}' is not valid. Spawn type is '{unitDefinition.SpawnType}' but it has air or missile abilities.
- `:1212` [DATA] AIUnitTags.MapSlap set on a UnitDefinition without UnitTagAsAbility.Artillery (Unit={unitDefinition.Name})
- `:1221` RepeatableDefinition {repeatableDefinition.Name} : AdditionalCostRpnReference name is null or empty.
- `:1226` RepeatableDefinition {repeatableDefinition.Name} : Invalid status without default duration. (Status={datatableElement2.Name})
- `:1232` NationalProjectDefinition {nationalProjectDefinition.Name} : AdditionalCostRpnReference name is null or empty.
- `:1240` EmpireWideConstructionParticipationDefinition {empireWideConstructionParticipationDefinition.Name} : Constructible '{empireWideConstructionParticipationDefinition.EmpireWideConstructionReference.ElementName}' not found.
- `:1244` EmpireWideConstructionParticipationDefinition {empireWideConstructionParticipationDefinition.Name} : Constructible '{empireWideConstructionParticipationDefinition.EmpireWideConstructionReference.ElementName}' is not an EmpireWideConstructionDefinition.
- `:1250` ArtificialDepositDistrictDefinition '{datatableElement6.Name}' has invalid resource definition (ResourceDefinition={artificialDepositDistrictDefinition.ResourceType.ElementName})
- `:1261` Invalid artificial deposit provider with overrided descriptor. Please remove this descriptor from the definition. (Definition={datatableElement6.Name.ToString()}, Descriptor={value4.ExploitationDescriptorReferences[num7].ElementName}
- `:1267` Constructible {datatableElement6.Name} : The unicity is OnPerTerritory but it's not an extension district.
- `:1280` UIMapper {datatableElement4.Name} texture {num9} is not defined.
- `:1291` Unicity additional constructible is not an extension. (Element={datatableElement6.Name}, UnicityAdditionalConstructible={datatableElement6.UnicityAdditionalConstructible[num11].ElementName}
- `:1301` Constructible has redundant descriptors. (Element={datatableElement6.Name}, Descriptor={datatableElement6.AllDescriptors[num13].ElementName}

## CheckSettlementStartingPackDefinitions  (3)
- `:1318` SettlementStartingPackage '{datatableElement2.Name}' can't resolve reference to improvement '{datatableElement2.Improvements[i].ElementName}'.
- `:1324` SettlementStartingPackage '{datatableElement2.Name}', '{datatableElement.Name}' is not an improvement.
- `:1328` '{settlementImprovementDefinition.Name}' needs a prerequisite in SettlementStartingPackage '{datatableElement2.Name}'.

## CheckImprovementLevelPrerequisites  (3)
- `:1350` SettlementStartingPackage '{startingPackage.Name}' can't resolve reference to improvement '{startingPackage.Improvements[improvementIndex].ElementName}'.
- `:1356` SettlementStartingPackage '{startingPackage.Name}', '{datatableElement.Name}' is not an improvement.
- `:1362` Impossible to have two occurrence of the same improvement '{improvementDefinition.Name}' in SettlementStartingPackage '{startingPackage.Name}'.

## CheckSettlerUnitDefinitions  (4)
- `:1387` Settler unit '{settlerUnitDefinition.Name}' has no settler starting pack.
- `:1397` Settler unit '{settlerUnitDefinition.Name}' reference no settler ability.
- `:1415` Settler unit '{settlerUnitDefinition.Name}' has a speciality without the settler ability.
- `:1433` Unit '{unitDefinition.Name}' is referencing a settlers speciality '{datatableElement2.Name}' but it isn't a settler definition.

## CheckFactionAffinities  (8)
- `:1458` Faction definition '{datatableElement.Name}' doesn't contains any BuildingVisualAffinityDefinition.
- `:1468` Faction definition '{datatableElement.Name}' reference a BuildingVisualAffinityDefinition that doesn't exist '{buildingVisualAffinityReference.ElementName}'.
- `:1472` Faction definition '{datatableElement.Name}' contains multiple building visual affinity for the same era index'{buildingVisualAffinityPerEra.EraIndex}', only one building visual affinity per era is allowed.
- `:1479` Faction definition '{datatableElement.Name}' doesn't contains any UnitVisualAffinityDefinition.
- `:1489` Faction definition '{datatableElement.Name}' reference an UnitVisualAffinityDefinition that doesn't exist '{unitVisualAffinityReference.ElementName}'.
- `:1493` Faction definition '{datatableElement.Name}' contains multiple unit visual affinity for the same era index'{unitVisualAffinityPerEra.EraIndex}', only one unit visual affinity per era is allowed.
- `:1500` Faction definition '{datatableElement.Name}' don't contains any LandmarkNameAffinityDefinition.
- `:1511` Faction definition '{datatableElement.Name}' reference a LandmarkNameAffinityDefinition that doesn't exist '{datatableElementReference.ElementName}'.

## CheckPresentationUnit  (4)
- `:1527` PresentationUnit has no variation.
- `:1540` Presentation unit is ranged and is missing some RLUDS restriction flags. Please, choose 'All'. (Definition={presentationPawnDefinition.Name}, RLUDSRestriction={presentationPawnDefinition.RLUDSRestriction})
- `:1562` UnitDefinition does not have an associate PresentationUnitDefinition.
- `:1566` UnitDefinition {unitDefinition.name} have {num} associate presentation unit definition. But only one is required.

## CheckTechnologyDefinitions  (5)
- `:1587` Technology '{datatableElement.Name}' contains a technology prerequisite that doesn't exist (prerequisite: '{staticString}').
- `:1594` Technology '{datatableElement.Name}' target an invalid era tier '{datatableElement.TechnologyTier}' with no cost rpn definition set.
- `:1599` Technology '{datatableElement.Name}' should not override the cost with the same era cost. CostRpnName={datatableElement.OverrideTierCostRpnReference.ElementName}, Tier={datatableElement.TechnologyTier}, Era={datatableElement.EraDefinition.Name}
- `:1611` Invalid simulation event effect of type {simulationEventEffect.GetType().Name} for technology {datatableElement.Name}.
- `:1617` Invalid simulation event effect of type {simulationEventEffect.GetType().Name} for technology {datatableElement.Name}.

## CheckMinorFactionSpawnerDefinitions  (10)
- `:1633` Minor faction spawner definition '{datatableElement2.Name}' should have BaseLifeTimeInTurns greater than 0.
- `:1640` Minor faction spawner definition '{datatableElement2.Name}' should have TimeBetweenArmySpawn greater than 0.
- `:1644` Minor faction spawner definition '{datatableElement2.Name}' should have AdditionalTimeBeforeFirstArmySpawn greater than or equal to 0.
- `:1648` Minor faction spawner definition '{datatableElement2.Name}' should have MaxArmyCountPerFaction greater than 0.
- `:1652` Minor faction spawner definition '{datatableElement2.Name}' should have RoamingDurationBeforePillage greater than 0.
- `:1656` Minor faction spawner definition '{datatableElement2.Name}' should have MaxArmySize greater than MinArmySize.
- `:1666` Minor faction spawner definition '{baseHumanSpawnerDefinition.Name}' should have RoamingDurationBeforeSettle greater than 0.
- `:1670` Minor faction spawner definition '{baseHumanSpawnerDefinition.Name}' should have RoamingDurationBeforePillage greater than 0.
- `:1674` Minor faction spawner definition '{baseHumanSpawnerDefinition.Name}' have an invalid ArmyPatternFallbackReference '{baseHumanSpawnerDefinition.ArmyPatternFallbackReference.ElementName}'.
- `:1686` Non crisis minor faction spawner definition ' ' contains a family based army pattern ' '.

## CheckRevolutionDefinitions  (7)
- `:1698` Revolution definition '{datatableElement.Name}' should have RevolutionPointThreshold greater than 0.
- `:1702` Revolution definition '{datatableElement.Name}' should have RevolutionDuration greater than 0.
- `:1706` Revolution definition '{datatableElement.Name}' should have NumberOfTurnBeforeWarningRevolution greater than 0.
- `:1710` Revolution definition '{datatableElement.Name}' should have RevolutionEffectDescriptor
- `:1714` Revolution definition '{datatableElement.Name}' should have PostRevolutionDescriptor
- `:1718` Revolution definition '{datatableElement.Name}' should have PostRevolutionDuration greater than 0.
- `:1722` Revolution definition '{datatableElement.Name}' should have StabilityPointRevolutionSteps

## CheckStatusDefinitions  (6)
- `:1733` [Data] StatusDefinition '{datatableElement2.Name}' must have a category
- `:1739` [Data] StatusDefinition '{datatableElement2.Name}' must have a descriptor to apply
- `:1743` [Data] StatusDefinition '{datatableElement2.Name}' must have a starting type
- `:1747` [Data] StatusDefinition '{datatableElement2.Name}' Starting type must match the starting type of its descriptor
- `:1753` [Data] StatusDefinition '{datatableElement2.Name}' Cost modifier missing '{datatableElement2.CostModifier.ElementName}'
- `:1757` [Data] Status definition '{datatableElement2.Name}' add a cost modifier but the target is neither a 'MajorEmpire' nor a 'Settlement'.

## CheckCultureDefinitions  (9)
- `:1769` There is no cultural proximity definition found.
- `:1777` The cultural proximity definition '{culturalProximityDefinition.Name}' have a min range value smaller than 0f.
- `:1781` The cultural proximity definition '{culturalProximityDefinition.Name}' have a max range value higher than 1f.
- `:1785` The cultural proximity definition '{culturalProximityDefinition.Name}' have a min range value higher than the max range value.
- `:1795` The max value of '{culturalProximityDefinition2.Name}' and the min value of '{culturalProximityDefinition3.Name}' don't match.
- `:1800` There is no cultural proximity definition that include 0f.
- `:1804` There is no cultural proximity definition that include 1f.
- `:1814` AcceptEffect should not be null (Definition={datatableElement.Name}, Index={k})
- `:1826` DiscardEffect should not be null (Definition={datatableElement.Name}, Index={l})

## CheckIdeologicalAxisDefinitions  (17)
- `:1841` The ideological axis '{datatableElement.Name}' have a min range value higher than the max range value.
- `:1846` There is no ideological orientation in axis '{datatableElement}'.
- `:1853` The ideological orientation '{ideologicalOrientationDefinition.Name}' in axis '{datatableElement.Name}' have a min range value smaller than axis min value.
- `:1857` The ideological orientation '{ideologicalOrientationDefinition.Name}' in axis '{datatableElement.Name}' have a min range value higher than axis max value.
- `:1861` The ideological orientation '{ideologicalOrientationDefinition.Name}' in axis '{datatableElement.Name}' have a min range value higher than the max range value.
- `:1866` The ideological axis '{datatableElement.Name}' contains an orientation with no name.
- `:1870` One or several ideological axes contains the same ideological orientation name '{ideologicalOrientationDefinition.Name}'.
- `:1880` The max value of ideological orientation '{ideologicalOrientationDefinition2.Name}' and the min value of ideological orientation '{ideologicalOrientationDefinition3.Name}' in axis '{datatableElement.Name}' don't match.
- `:1885` There is no ideological orientation in axis '{datatableElement.Name}' that include axis min value.
- `:1889` There is no ideological orientation in axis '{datatableElement.Name}' that include axis max value.
- `:1898` There is no section in ideological orientation in '{ideologicalOrientationDefinition4.Name}' in axis '{datatableElement.Name}'.
- `:1905` The section '{l}' in ideological orientation in '{ideologicalOrientationDefinition4.Name}' in axis '{datatableElement.Name}' have a min range value smaller than orientation min range value.
- `:1909` The section '{l}' in ideological orientation in '{ideologicalOrientationDefinition4.Name}' in axis '{datatableElement.Name}' have a max range value higher than orientation max range value.
- `:1913` The section '{l}' in ideological orientation in '{ideologicalOrientationDefinition4.Name}' in axis '{datatableElement.Name}' have a min range value higher than the max range value.
- `:1924` The max value of section '{m}' and the min value of section '{m + 1}' in ideological orientation '{ideologicalOrientationDefinition4.Name}' in axis '{datatableElement.Name}' don't match.
- `:1929` There is no section in ideological orientation '{ideologicalOrientationDefinition4.Name}' in axis '{datatableElement.Name}' that correspond to orientation min range value.
- `:1933` There is no section in ideological orientation '{ideologicalOrientationDefinition4.Name}' in axis '{datatableElement.Name}' that correspond to orientation max range value.

## CheckNarrativeEvents  (1)
- `:2025` Useless prerequisite in event {datatableElement.Name} on choice {k} (For choices failure flags should not be 'None').

## CheckSimulationEventEffectsConstructibleFamilies  (3)
- `:2064` : ' ' not found. Element=' '.
- `:2070` Cannot find : ' ' for : ' '
- `:2074` The : ' ' for : ' ' has been found in multiple for Element: ' ' ! All constructible with the same should be in the same .

## IsSimulationEventEffectValid  (86)
- `:2115` Simulation event effect is null (Element '{element.Name}')
- `:2123` Cost Modifier definition '{simulationEventEffect_ApplyCostModifier.CostModifierReference.ElementName}' not found. Element='{element.Name}'.
- `:2133` Experience Modifier definition '{simulationEventEffect_ApplyExperienceModifier.ExperienceModifierReference.ElementName}' not found. Element='{element.Name}'.
- `:2143` Add Fame effect should have 'Amount' greather than 0. Amount = {simulationEventEffect_AddFame.Amount}, Element='{element.Name}'.
- `:2153` Add Research effect should have 'Amount' greather than 0. Amount = {simulationEventEffect_AddResearch.Amount}, Element='{element.Name}'.
- `:2158` AddResearch effect amount RPN is malformed (Element '{element.Name}')
- `:2168` Add empire BattleAbility effect should not have a null BattleAbilityDefinition.
- `:2178` Descriptor definition '{simulationEventEffect_ApplyDescriptor.Descriptor.ElementName}' not found. Element='{element.Name}'.
- `:2185` _(warn)_ Descriptor definition '{simulationEventEffect_ApplyDescriptor.Descriptor.ElementName}' is applied by at least two different narrative event, use the tool in Tools/NarrativeEvent to find which one.
- `:2196` Diplomatic descriptor definition '{simulationEventEffect_ApplyDiplomaticDescriptor.Descriptor.ElementName}'. Element='{element.Name}'.
- `:2207` Status definition '{simulationEventEffect_ApplyStatus.StatusDefinition.ElementName}' not found. Element='{element.Name}'.
- `:2212` Both the Element '{element.Name}' and the StatusDefinition '{simulationEventEffect_ApplyStatus.StatusDefinition.ElementName}' use negative duration.\nPlease either change the default duration in the status definition or define a duration on the initiator.
- `:2228` Resource definition '{datatableElementReference.ElementName}' not found. Element='{element.Name}'.
- `:2238` Technology definition '{simulationEventEffect_UnlockTechnology.TechnologyReference.ElementName}' not found. Element='{element.Name}'.
- `:2248` Invalid empty constructible list. Element='{element.Name}'.
- `:2259` Constructible definition '{datatableElementReference2.ElementName}' not found. Element='{element.Name}'.
- `:2264` Constructible definition '{datatableElementReference2.ElementName}' is obsolete. Element='{element.Name}'.
- `:2275` Constructible definition '{simulationEventEffect_UnlockSettlementStartingPackage.StartingPackageReference.ElementName}' not found. Element='{element.Name}'.
- `:2285` Invalid empty constructible list for Element='{element.Name}'.
- `:2296` Constructible definition '{datatableElementReference3.ElementName}' not found. Element='{element.Name}'.
- `:2301` Constructible definition '{datatableElementReference3.ElementName}' is obsolete. Element='{element.Name}'.
- `:2312` Resource definition '{simulationEventEffect_ProhibitResource.ResourceReference.ElementName}' not found. Element='{element.Name}'.
- `:2321` Remove settlement improvement should only be used with Narrative Events. Element='{element.Name}'.
- `:2331` Spawn army should only be used with Narrative Events or loot table. Element='{element.Name}'.
- `:2338` Spawn army should have at least one unit. Element='{element.Name}'.
- `:2347` Unit definition '{simulationEventEffect_SpawnArmy.UnitDefinitions[j].ElementName}' is not valid. Element='{element.Name}'.
- `:2352` Unit definition '{simulationEventEffect_SpawnArmy.UnitDefinitions[j].ElementName}' is not obsolete. Element='{element.Name}'.
- `:2358` Spawn army should give a name to the army. Element='{element.Name}'.
- `:2368` Spawn army should only be used with Narrative Events. Element='{element.Name}'.
- `:2373` Effect is missing a target. Element='{element.Name}'.
- `:2378` Effect is missing an owner. Element='{element.Name}'.
- `:2383` Effect Spawn army is missing unit to spawn. Element='{element.Name}'.
- `:2392` Effect Spawn army unit definition '{reference.ElementName}' not found. Element='{element.Name}'.
- `:2397` Effect Spawn naval army unit definition '{k}' not a naval unit. Element='{element.Name}'.
- `:2402` Unit definition '{simulationEventEffect_SpawnArmy.UnitDefinitions[k].ElementName}' is not obsolete. Element='{element.Name}'.
- `:2408` Spawn naval army should give a name to the army. Element='{element.Name}'.
- `:2417` Spawn settlement should only be used with Narrative Events or loot table. Element='{element.Name}'.
- `:2426` Civic definition '{simulationEventEffect_UnlockCivics.CivicReference.ElementName}' not found. Element='{element.Name}'.
- `:2436` Ideological axis definition '{simulationEventEffect_ModifyIdeologicalAxis.IdeologicalAxisReference.ElementName}' not found. Element='{element.Name}'.
- `:2441` Ideological axis delta should be different from '0'. Element='{element.Name}'.
- `:2451` Simulation event effect log message has no message to log. Element='{element.Name}'.
- `:2461` Invalid Simulation Event Effect no target. (Element '{element.Name}')
- `:2471` Invalid Simulation Event Effect no target. (Element '{element.Name}')
- `:2481` Invalid Simulation Event Effect no target. (Element '{element.Name}')
- `:2491` Invalid Simulation Event Effect no target. (Element '{element.Name}')
- `:2501` Invalid Simulation Event Effect no target. (Element '{element.Name}')
- `:2508` Add civic point is obsolete. (Element '{element.Name}')
- `:2516` Invalid Simulation Event Effect no target. (Element '{element.Name}')
- `:2526` Invalid Simulation Event Effect no target. (Element '{element.Name}')
- `:2535` Datable element '{element.Name}' of type '{element.GetType()}' contains effect of type '{typeof(SimulationEventEffect_DeactivateDistrict)}', only narrative event definitions should use it.
- `:2540` Narrative event '{element.Name}' contains a choice that is not reversible that contains effect of type '{typeof(SimulationEventEffect_DeactivateDistrict)}'.
- `:2550` Supress revolution gauge effect has no target. (Element '{element.Name}')
- `:2560` Add settlement event status effect has no target. (Element '{element.Name}')
- `:2565` Settlement event status effect has no status to apply. (Element '{element.Name}')
- `:2574` Reveal new world event effect has no target. (Element '{element.Name}')
- `:2579` Reveal new world event effect reveal no tiles. (Element '{element.Name}')
- `:2589` Invalid Simulation Event Effect no target. (Element '{element.Name}')
- `:2599` Key is empty for event effect {simulationEventEffect.GetType()} of element {element.Name}. No narrator sentences will be read.
- `:2609` Invalid Simulation Event Effect no target. (Element '{element.Name}')
- `:2614` Invalid Simulation Event Effect no effect to apply. (Element '{element.Name}')
- `:2624` Invalid target ID for effect add knowledge. Element='{element.Name}'.
- `:2634` Missing Army ID on simulation event effect. Element {element.Name}.
- `:2639` Missing Unit to kill on simulation event effect. Element {element.Name}.
- `:2649` Missing district to destroy on simulation event effect. Element {element.Name}.
- `:2659` Missing city to modify public order from. Element {element.Name}.
- `:2669` Missing empire. Element {element.Name}.
- `:2679` Missing empire. Element {element.Name}.
- `:2684` Missing empire naming reference. Element {element.Name}
- `:2693` Missing target empire. Element {element.Name}.
- `:2697` Missing pollution source. Element {element.Name}.
- `:2706` Missing target ambassy ({element.Name})
- `:2716` Missing target ambassy ({element.Name})
- `:2726` Missing target empire ({element.Name})
- `:2731` Missing district ({element.Name})
- `:2736` Missing reward evaluation rpn ({element.Name})
- `:2746` Missing Target '{element.Name}'
- `:2751` Missing Owner '{element.Name}'
- `:2756` Missing Unit familly '{element.Name}'
- `:2763` A unit familly is missing '{element.Name}'
- `:2774` Missing target ('{element.Name}')
- `:2780` One or more exotic ability flags is not supported by this effect (Element '{element.Name}', Flags '{simulationEventEffect_UnlockEmpireExoticAbilityFlags.AbilityType}', Supported Flags '{empireExoticAbilityFlags}')
- `:2790` Missing target ('{element.Name}')
- `:2795` Invalid vision radius for SimulationEventEffect_RevealRandomOceanicTiles. Value should be superior to 0. (Element '{element.Name}'
- `:2806` Invalid status definition for SimulationEventEffect_AddStatusOnSettlementCreation. (Element={element.Name}, Status={simulationEventEffect_AddStatusOnSettlementCreation.StatusDefinition.ElementName})
- `:2810` Invalid status duration for SimulationEventEffect_AddStatusOnSettlementCreation. Please fill a default value. (Element={element.Name}, Status={simulationEventEffect_AddStatusOnSettlementCreation.StatusDefinition.ElementName})
- `:2818` Invalid unknown effects. Effect type: '{simulationEventEffect.GetType().FullName}'. Element='{element.Name}'.

## CheckSimulationEventVariable  (3)
- `:2826` Variable is null in {parentName}
- `:2832` Variable as no name in {parentName}
- `:2852` Variable {narrativeEventVariable_TargetsInRange.Name} in {parentName} can't be sorted by distance and by property value at the same time!

## CheckPrerequisite  (11)
- `:2863` Prerequisite has no targetID in .
- `:2871` Prerequisite has no property to check in .
- `:2878` Prerequisite {simulationEventPrerequisite.GetType().Name} has an empty property id in {parentName} at index {i}.
- `:2889` SimulationEventPrerequisite_Empire_CivicStatuses has no civics to check in .
- `:2898` SimulationEventPrerequisite_Empire_CivicStatuses contains a civics ('{simulationEventPrerequisite_Empire_CivicStatuses.Civics[j].ElementName}') that doesn't exist in {parentName}.
- `:2903` SimulationEventPrerequisite_Empire_CivicStatuses contains a civics ('{simulationEventPrerequisite_Empire_CivicStatuses.Civics[j].ElementName}') that is obsolete in {parentName}.
- `:2909` SimulationEventPrerequisite_Empire_CivicStatuses has no status to check in .
- `:2919` SimulationEventPrerequisite_Empire_CivicDependency has no civics to check in .
- `:2925` SimulationEventPrerequisite_Empire_CivicDependency contains a civics ('{simulationEventPrerequisite_Empire_CivicDependency.CivicReference.ElementName}') that doesn't exist in {parentName}.
- `:2930` SimulationEventPrerequisite_Empire_CivicDependency contains a civics ('{simulationEventPrerequisite_Empire_CivicDependency.CivicReference.ElementName}') that is obsolete in {parentName}.
- `:2941` Invalid target property. (Element= , Starting= Path= , , Property= )

## CheckArmyPatternDefinitions  (9)
- `:2955` Invalid SelectionCondition (= {datatableElement.SelectionCondition}) for army pattern '{datatableElement.Name}'
- `:2962` Army pattern '{datatableElement.Name}' doesn't contains any unit.
- `:2970` Invalid UnitReference '{unitPattern.UnitReference.ElementName}' in army pattern '{datatableElement.Name}'.
- `:2974` Invalid unit Count (= {unitPattern.Count}) in army pattern '{datatableElement.Name}' for unit '{unitPattern.UnitReference.ElementName}', must be greater than zero.
- `:2985` Army pattern '{datatableElement.Name}' doesn't contains any unit.
- `:2993` Invalid unit family reference '{familyUnitPattern.UnitFamilyReference.ElementName}' in army pattern '{datatableElement.Name}'.
- `:2997` Invalid LevelMin (= {familyUnitPattern.LevelMin}) in army pattern '{datatableElement.Name}', must be greater or equal to zero.
- `:3001` Invalid LevelMax (= {familyUnitPattern.LevelMax}) in army pattern '{datatableElement.Name}', must be greater or equal to LevelMin (= {familyUnitPattern.LevelMin}).
- `:3005` Invalid unit Count (= {familyUnitPattern.Count}) in army pattern '{datatableElement.Name}' for unit family '{familyUnitPattern.UnitFamilyReference.ElementName}', must be greater than zero.

## CheckFactionDefinitions  (3)
- `:3025` Invalid null pattern entry. Please remove the empty entry in the array. (FactionDefinition={datatableElement2.Name}, EntryIndex={i})
- `:3029` Non crisis minor faction ' ' contains a family based army pattern ' '.
- `:3034` Minor Militia in faction definition is mandatory. Please enter a valid unit definition. (Faction={datatableElement2.Name}, MilitiaReference={datatableElement2.MinorFactionMilitiaReference.ElementName})

## CheckArmyStripDefinitions  (4)
- `:3050` Invalid strip limit definition ({datatableElement.name}) between strip {i - 1} and {i}.
- `:3055` Invalid era reference '{datatableElement.EraReference.ElementName}' for strip limit definition '{datatableElement.name}'.
- `:3059` Multiple strip limit definitions ('{datatableElement.name}' and '{dictionary[datatableElement.EraReference.ElementName]}') reference the same era '{datatableElement.EraReference.ElementName}'.
- `:3070` There is no army stripe definition that reference era '{datatableElement2.Name}'.

## CheckTenetDefinitions  (6)
- `:3084` Tenet definition '{datatableElement2.Name}' is not obsolete but reference an obsolete tenet tier definition '{datatableElement2.TierReference.ElementName}'.
- `:3093` Invalid tenet tier value (= '{datatableElement3.Tier}') in tenet tier definition '{datatableElement3.Name}' must be greater or equal to 0.
- `:3099` Thet tier '0' TenetTierDefinition (= '{datatableElement3.Name}') should not have a followers threshold rpn reference.
- `:3104` Tenet tier definition '{datatableElement3.Name}' don't have a followers threshold rpn reference.
- `:3119` Multiple tenet tier definitions ('{array[datatableElement4.Tier].Name}' and '{datatableElement4.Name}') shares the same tier value '{datatableElement4.Tier}'.
- `:3128` There is no tenet tier definition for tier value '{i}'

## CheckSettlementImprovementFamilies  (2)
- `:3147` Settlement improvement family '{datatableElement.Name.ToString()}' contains a level (= {i}) with no improvement.
- `:3151` Settlement improvement family '{datatableElement.Name.ToString()}' contains a level (= {i}) with multiple improvement.

## CheckTutorialElementDefinitions  (30)
- `:3180` No level for tutorial definition {datatableElement4.Name}.
- `:3184` No category for tutorial definition {datatableElement4.Name}.
- `:3188` No domain for tutorial definition {datatableElement4.Name}.
- `:3199` No valid Stamp for UITarget {i} of tutorial definition {datatableElement4.Name}.
- `:3205` Empty Highlight definition for UITarget {i} of tutorial definition {datatableElement4.Name}.
- `:3210` Cannot retrieve Highlight definition for UITarget {i} of tutorial definition {datatableElement4.Name}.
- `:3214` Invalid prefab in Highlight definition {datatableElement.Name} for UITarget {i} of tutorial definition {datatableElement4.Name}.
- `:3239` Invalid Text Anchor {reference.TextPrimaryAnchor}-{reference.TextSecondaryAnchor} combination for tutorial definition {datatableElement4.Name}
- `:3247` _(warn)_ No Description specified for tutorial definition {datatableElement4.Name}
- `:3252` _(warn)_ No Anchoring specified for tutorial definition {datatableElement4.Name}
- `:3261` Duplicated {tutorialCompletionActionType} action in CompletionActions array for tutorial definition {datatableElement4.Name}.
- `:3276` Empty array element reference for tutorial definition {datatableElement4.Name} (Beginner prerequisite).
- `:3280` Circular prerequisite reference for tutorial definition {datatableElement4.Name} (Beginner prerequisite).
- `:3285` Cannot retrieve reference definition for tutorial definition {datatableElement4.Name} (Beginner prerequisite).
- `:3289` Invalid prerequisite definition level for tutorial definition {datatableElement4.Name} (your Beginner prerequisites contains a reference to a definition not supporting the Beginner level).
- `:3293` Invalid prerequisite definition category for tutorial definition {datatableElement4.Name} (your Beginner prerequisites contains a reference to a definition not supporting the same category).
- `:3297` Reminder prerequisite definition for tutorial definition {datatableElement4.Name} (your Beginner prerequisites contains a reference to a Reminder, reminders cannot be prerequisites).
- `:3310` Empty array element reference for tutorial definition {datatableElement4.Name} (Advanced prerequisite).
- `:3314` Circular prerequisite reference for tutorial definition {datatableElement4.Name} (Advanced prerequisite).
- `:3319` Cannot retrieve reference definition for tutorial definition {datatableElement4.Name} (Advanced prerequisite).
- `:3323` Invalid prerequisite definition level for tutorial definition {datatableElement4.Name} (your Advanced prerequisites contains a reference to a definition not supporting the Advanced level).
- `:3327` Invalid prerequisite definition category for tutorial definition {datatableElement4.Name} (your Advanced prerequisites contains a reference to a definition not supporting the same category).
- `:3331` Reminder prerequisite definition for tutorial definition {datatableElement4.Name} (your Advanced prerequisites contains a reference to a Reminder, reminders cannot be prerequisites).
- `:3348` Empty EntityStatusPrerequisite for definition {datatableElement4.Name}.
- `:3355` Empty EntityStatusPrerequisite for definition {datatableElement4.Name}.
- `:3368` Empty array element reference for tutorial definition {datatableElement4.Name} (SlaveTutorial).
- `:3372` Cannot retrieve reference definition for tutorial definition {datatableElement4.Name} (SlaveTutorial).
- `:3378` Tutorial definition {datatableElement4.Name} has an army composition prerequisite but is not constrained to the Army domain.
- `:3382` Cannot retrieve reference definition for tutorial definition {datatableElement4.Name} (TutorialChainNextElement).
- `:3395` Tutorial definition {datatableElement4.Name} has no way to be completed.

## CheckObsoleteTutorialPrerequisitProperty  (3)
- `:3408` Tutorial {tutorialElementName} is referencing an obsolete property definition {prerequisiteDefinition.EmpirePropertyPrerequisites[i].Property}.
- `:3412` Tutorial {tutorialElementName} is referencing an obsolete property definition {prerequisiteDefinition.EmpirePropertyPrerequisites[i].Property}.
- `:3416` Tutorial {tutorialElementName} is referencing an obsolete property definition {prerequisiteDefinition.EmpirePropertyPrerequisites[i].Property}.

## CheckCalendarDefinitions  (3)
- `:3429` CalendarDefinition must have at least one Period filled. {datatableElement.Name} has none.
- `:3437` CalendarDefinition {datatableElement.Name} Period #{i}: NumberOfTurns must be greater than or equal to 1.
- `:3441` CalendarDefinition {datatableElement.Name} Period #{i}: YearsPerTurn must be greater than or equal to 1.

## CheckGameOptionDefinitions  (1)
- `:3466` Invalid key name for option ' '. Option= . Key=

## CheckGameDifficultyDefinitions  (2)
- `:3479` Missing PersonaDifficultyWeight entries for ' '.
- `:3491` Invalid PersonaDifficultyWeight total weight, weights must be superior to 0 ' '.

## CheckPollutionDefinitions  (6)
- `:3508` PollutionDefinition : Territory pollution levels are not sorted.
- `:3513` PollutionDefinition {datatableElement.name}: Pollution level {i} is missing a status to apply.
- `:3519` PollutionDefinition : Missing its terrytory pollution thersholds.
- `:3529` PollutionDefinition : Atmospheric Pollution levels are not sorted.
- `:3534` PollutionDefinition {datatableElement.name}: Pollution level {j} is missing a status to apply.
- `:3540` PollutionDefinition : Missing atmospheric pollution thersholds.

## CheckNaturalWonderDefinitions  (4)
- `:3560` NaturalWonderDefinition {datatableElement.Name} has a constrain with multiple position. Only one position is supported per constrain.
- `:3564` NaturalWonderDefinition {datatableElement.Name} has a constrain on {positionDirection} position, but this position is not contained in wonders Positions. It will be ignored.
- `:3568` NaturalWonderDefinition {datatableElement.Name} will never spawn. It has a constrain on center position with TerrainTypeNeeded not containing TerrainType_CoastalWater or TerrainType_Ocean. This wonder is Oceanic, and as such will always try to spawn with its center tile on CoastalWater or Ocean.
- `:3572` NaturalWonderDefinition {datatableElement.Name} has a constrain on {positionDirection} position, that replace terrain with forbidden TerrainType {reference.ReplaceTerrainWith}.

## CheckResourceDepositDefinitions  (2)
- `:3587` ResourceDepositDefinition Minimum Access is too low (ElementName : )
- `:3591` ResourceDepositDefinition Maximum Access is below the Minimum Access (ElementName : )

## InitializeStatisticDefinition  (3)
- `:3989` The statistic reporter '{datatableElement.Name}' should not have empty statistic reference.
- `:3994` The statistic reporter '{datatableElement.Name}' is linked to a non existing statistic '{datatableElement.StatisticReference.ElementName}'.
- `:4002` Statistic_DeedEvaluation {statisticReporterDefinition_DeedEvaluator.Name} doesn't have a DeedEvaluator

## InitializeDeedEvaluator  (8)
- `:4020` Unable to retrieve System.Type of '{0}' in Evaluator of type 'DeedEvaluator_PropertyEvaluator'.
- `:4034` Unable to retrieve System.Type of '{0}' in Evaluator of type 'DeedEvaluator_PropertyEvaluator'.
- `:4048` Unable to retrieve System.Type of '{0}' in Evaluator of type 'DeedEvaluator_ReferenceCollectionCount'.
- `:4057` Unknown deed evaluator type : ; Add it to the DataController Initialize to initialize it.
- `:4080` The constructible '{0}' must have a level greater or equal to 0.
- `:4102` _(warn)_ The constructible family '{0}', don't have any constructible.
- `:4139` Emblematic {typeof(U).Name} in common level: {typeof(U).Name}:{val2.Name} ; Level: {i}\n Elements:{PrintElementNames(list2)}
- `:4144` Common {typeof(U).Name} in emblematic level: {typeof(U).Name}:{val2.Name} ; Level: {i}.\n Elements:{PrintElementNames(list2)}

## InitializeNarrativeEvents  (6)
- `:4183` _(warn)_ Invalid null or empty 'StartingType' in narrative event definition '{0}' +Prerequisites[{1}] of type 'SimulationEventPrerequisite_Entity_Path'.
- `:4189` Unable to retrieve system type of '{2}' in narrative event definition '{0}' +Prerequisites[{1}] of type 'SimulationEventPrerequisite_Entity_Path'.
- `:4215` _(warn)_ Invalid null or empty 'StartingType' in narrative event definition '{0}' +variable[{1}] +Prerequisites[{2}] of type 'SimulationEventPrerequisite_Entity_Path'.
- `:4221` Unable to retrieve system type of '{3}' in narrative event definition '{0}' +variable[{1}] +Prerequisites[{2}] of type 'SimulationEventPrerequisite_Entity_Path'.
- `:4250` _(warn)_ Invalid null or empty 'StartingType' in narrative event definition '{0}' +Choice[{1}] +Prerequisites[{2}] of type 'SimulationEventPrerequisite_Entity_Path'.
- `:4256` Unable to retrieve system type of '{3}' in narrative event definition '{0}' +Choices[{1}] +Prerequisites[{2}] of type 'SimulationEventPrerequisite_Entity_Path'.

## InitializePresentationUnitDefinitions  (4)
- `:4279` Invalid spawn weight. Do not use 0 in retail builds. PawnDefinition={item5.Name}
- `:4299` Invalid unit presentation definition '{0}' for pawn '{1}'
- `:4350` Invalid spawn weight. Do not use 0 in retail builds. MountDefinition={item8.Name}
- `:4369` Invalid unit presentation definition '{0}' for pawn '{1}'

## InitializeGameScenariosGroupUIMappers  (2)
- `:4607` _(warn)_ Unknown GameScenarioDefinition '{0}' in GameScenariosGroupUIMapper '{1}'.
- `:4611` _(warn)_ GameScenarioDefinition '{0}' doesn't have any valid save.

## SetConstructibleAsNeededToBeUnlocked  (3)
- `:4713` Constructible '{0}' not found in {1} '{2}'.
- `:4718` {0} '{1}' try to unlock empire wide construction participation '{2}'.
- `:4734` Constructibles within the same unlock event must belong to the same family. {0} '{1}'

## SetResourcesAsNeededToBeUnlocked  (1)
- `:4747` ResourceDefinition '{0}' not found in {1} '{2}'.

## IsConstructibleAddingFameByDescriptors  (2)
- `:4760` ConstructibleDefinition parameter is null
- `:4769` Constructible descriptor is null (Definition={constructibleDefinition.Name}, Index={i})
