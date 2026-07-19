# Compatibility Patcher — User Manual

## What It Does

When multiple database mods touch the same elements (cultures, units, techs, etc.), Humankind uses **last-loaded-wins** for the entire element — it does not merge fields. If two mods define the same element, the earlier mod's version is silently replaced.

The Compatibility Patcher:
1. Compares N mods to find where they collide
2. Shows you exactly what differs (field-by-field)
3. Lets you choose which version wins per element
4. Builds a **patch mod** containing only your resolved elements, loaded last so it overrides

It also validates your load order against known crash-causing patterns and supports **incremental re-patching** — when a source mod updates, only the changed elements resurface for review.

---

## Opening the Window

**Tools → Compatibility Patcher**

---

## Workflow

### 1. Add Your Mods

At the top of the window, add each mod you want to compare. Supported formats:
- `.assetbundle` (best for live refs — stays mounted for the session)
- `.unitypackage`
- `.zip`
- A folder containing `Assets/Databases/`

Click **+ Add .unitypackage / .zip / .assetbundle** or **+ Add folder**.

Each mod appears as a row with a name field and path. Use the **▲ ▼** buttons to set load order — **last in the list wins** (same as in-game load order).

After Compare with assetbundles, expand **Loaded mod bundles (excl. vanilla)** to see mounts and **Force unload**. Remount only happens if a mount is missing or stale.

> **Tip:** The load order matters. If Mod A and Mod B both define `Unit_X`, and Mod B is below Mod A, then Mod B's version is the "winner" by default.

### 2. Prior Sidecar (Optional)

If you've patched these mods before, point **Prior sidecar** at `CompatPatch.sidecar.json` (or click **…** / **Load mods**). That refills the mod list (name + path, load order). Marking conflicts **resolved** (or importing them) writes this file automatically — default path `Assets/Databases/Patch/CompatPatch.sidecar.json`.

The sidecar records **explicit resolve decisions** (not every conflict). On re-Compare:
- **Unchanged sources** → still **✓resolved** (hidden under “needs review only”)
- **Sources changed** → resurface as **changed** for review
- **New collisions** → **new**

**✓resolved** = you accepted the winner/choice without needing a Patch override. **✓in patch** = you imported into `Patch/`. Both clear “needs review.”

### 3. Compare

Press **Compare** (requires at least 2 mods). The patcher:

1. Reads each mod's database assets (read-only, never imports into your project)
2. Builds an element map keyed by `(type, name)` — path-agnostic
3. Diffs every element present in multiple mods
4. Scans **`Assets/Databases/Patch/`** for **orphans** (see below)
5. Validates the load order against crash-causing patterns
6. Populates the workspace below (hazard cards + type/pattern browse)

Use **Resolve all as winner** to mark still-unreviewed conflicts in the **current filtered list** as resolved (accepts each load-order winner, writes sidecar, no import).

### Patch orphans (after Compare)

Compare also diffs **`Assets/Databases/Patch/`** against the loaded mods. A foldout under the stats line lists **orphans** — Patch/ elements that still override at load but no longer sit on a live multi-mod conflict:

| Kind | Meaning |
|---|---|
| **gone from mods** | No compared mod defines this name anymore — Patch/ alone forces it |
| **sole mod left** | Only one mod still has it — the conflict dissolved; Patch/ still overrides that mod |
| **mods agree** | ≥2 mods still have it but Compare sees them Identical — Patch/ overrides for no conflict |

**Compare** opens side-by-side (mods that still define it + Patch/). **Ping** selects the patch asset; **Remove** deletes that named element from the Patch/ collection. Keep the row if you still want a deliberate custom edit. Full dump also goes to the Console each Compare.

---

## After Compare — workspace

### Hazard cards (collapsible)

**Load-order validation** and **Patch orphans** sit above the workspace as DiffGui-style foldout cards. They auto-expand when non-empty after Compare. Expand to read findings / orphan rows (Compare / Ping / Remove).

### Filters

AND-combined scope for the type tree, pattern pane, and Compare nav:

- **Status toolbar**: Conflicts / All / New / Identical
- **Name contains**: substring filter
- **needs review only**: only conflicts still needing a decision (hides ✓resolved / ✓in patch)
- **hide winner-only**: hides conflicts where the only diffs are `ExtraInWinner`

### Left — types (expandable)

Types sorted by total diff load (`UnitDefinition`, `Descriptor`, …). Each row shows `N diffs · M els`.

- **Select** a type (or use Select) to drive the pattern pane on the right.
- **Expand** the type to list its filtered elements. **Click an element** to open side-by-side Compare on that **full** object; Compare’s nav list is the aggregated set for that type (same filter).

### Right — patterns by frequency / embedded Compare

Default: recurring field diffs for the selected type (most common first), with mass-apply.

**Compare side-by-side** or clicking an element under the type **replaces this pattern pane** with full-element Compare (DiffGui above Odin columns). Use **← Patterns** to return. Click another element on the left to switch without leaving Compare. Orphan **Compare** still opens the floating Compare window.

---

## Side-by-Side Compare

**In the main window:** clicking an element (or **Compare side-by-side**) swaps the right-hand pattern list for an embedded Compare view — full element columns + collapsible DiffGui above them. Nav is the left type tree (and **← Patterns** returns to aggregated diffs).

**Floating window:** still used for Patch orphans (**Compare** on an orphan row), with its own element list.

- **Above inspectors**: collapsible **Differences for {element}**
- **Columns**: one per contributing mod + **Patch/** (editable); each is the complete datatable element

Click **Import this version into Patch/** on a source column for the primary element only. **Resolve as winner** accepts the load-order winner without importing.

---

## Resolving Conflicts

Two ways to settle a conflict:

| Action | Result |
|---|---|
| **Mark resolved** / **Resolve as winner** | Accept chosen/winner — no `Patch/` file. Sidecar remembers (`✓resolved`). |
| **Import chosen** | Copy into `Assets/Databases/Patch/` (`✓in patch`) and also mark resolved in the sidecar. |

### Per-Element Resolution

For each conflict:
1. Select the element in the list
2. Choose which mod's version wins (radio button), or leave the load-order winner
3. Either **Mark resolved (accept chosen)** or **Import chosen into Patch/**

### Mass actions

Operate on the **current filtered list** only (status / name / type / needs-review / hide-winner-only):

- **Resolve all as winner** — mark listed unresolved conflicts resolved (load-order winner each). Sidecar only; no import.
- **Import all conflicts (Winner Mod)** — import listed unresolved conflicts' load-order winner versions into `Patch/` (ignores per-row radio choice).
- **Mass Change…** — bulk field ADD/PICK on elements **already in Patch**. Groups recurring diffs by path; pick a source mod per pattern. Scope = current filters (incl. Type), or **Ctrl/Cmd+click** multi-select in the list. Never imports — import winners first. Detail diffs also offer **Apply to N in Patch…** for one pattern.

> Whole-element import is still the base. Mass Change covers recurring ref-list ADDs and simple leaf PICKs (including nested paths like `SettlementStabilityPrerequisite.PublicOrderEffects`). Complex structured list rows still need hand-edit in Compare.

### Import & Edit (New Elements)

For elements that exist in only one mod (status `new`), click **Import & Edit into Patch/** to bring it into the patch so you can modify it. This is how you add custom rebalancing or new content to the patch.

---

## Incremental Re-Patching

The time-saver: when a source mod updates, you don't re-review everything.

1. Work as usual — **Mark resolved** / Import auto-writes the sidecar (or click **Export sidecar**)
2. Next session: same mods + **Prior sidecar** path → **Compare**

Results:
- **Unchanged** → still **✓resolved** (hidden under “needs review only”)
- **Changed** → resurfaces as `changed`, pre-filled with your prior choice
- **New collisions** → status `new`

The sidecar stores a **fingerprint** of the source values per resolved decision. Match → stay resolved; differ → review again.

---

## Load-Order Validation

The validation panel runs automatically on every Compare. It checks the merged datatable (vanilla + mods in load order) against known crash-causing patterns:

| Rule | What It Catches |
|------|-----------------|
| `:4713` | Unlock references a constructible that doesn't exist |
| `:4718` | Unlock targets an illegal constructible type |
| `:4734` | Unlock event mixes constructibles from different families |
| `:4747` | Unlock references a resource that doesn't exist |
| `:4299` | PresentationPawn references a missing PresentationUnit |
| `:4369` | PresentationSecondaryPawn references a missing PresentationUnit |
| `:4139` | Emblematic unit/settlement-improvement in a common family level |
| `:4144` | Common unit/settlement-improvement in an emblematic family level |

Findings are attributed to the mod whose load step first triggers the hazard (`IntroducedBy`). The panel also compares against the reversed load order to identify **order-sensitive** hazards — those you can fix by reordering rather than editing.

> **Console log**: every Compare dumps the full validation results to the Unity console, grouped by rule code. Use this for diffing between runs.

---

## Patch Output

Resolved elements are written to **`Assets/Databases/Patch/`** as per-type collection assets (e.g. `TechnologyDefinition.asset`). These are real Unity assets you can inspect and edit in the project.

### Delivery

- **In project** (default): leave the patch under `Assets/Databases/Patch/`. Build the assetbundle via the Mod Tools' build pipeline.
- **Export as .unitypackage**: for sharing. Re-importable and re-patchable later.

The sidecar travels with both forms (companion file in the folder, or included in the exported package).

> The patcher does **not** build the assetbundle — that's the Mod Tools' job. The patcher's output is the database assets.

---

## Tips & Gotchas

### Element Identity is Path-Agnostic
The patcher keys on `(type, name)`, not file path. If Mod A splits an element across two files and Mod B puts it in one, they still collide if the name matches.

### Odin Elements
Some element types keep lists in an Odin serialization stream (e.g. `TechnologyDefinition.SimulationEventEffects`, narrative `Choices[].NarrativeEventEffects`). For **live / assetbundle** sources the patcher reflection-merges those effect trees into the compare body, so field diffs (Amount, UnlockAction enums, refs, …) show like any other StructDiff row. Pure-Odin elements such as `DeedDefinition` (Trigger / Evaluator / Score) get a second reflection pass (`ReflectionBodyMerge`) so Compare does not report false Identical when SerializedObject is empty. Remaining YAML-only carriers without a live object still fall back to **references only** — use **Compare side-by-side** for the full inspector.

### Winner-Only Diffs
If a conflict's only diffs are `ExtraInWinner` (the winner has something others don't), it's usually safe to ignore — the winner already includes those entries. Toggle **hide winner-only** to filter these out.

### Scratch Staging
- **`.assetbundle` sources:** Compare is fully in-memory. Bundles stay **mounted for the session** so inspector references resolve (like vanilla). No `_PatcherBundleStage` / `_PatcherStage` fill on Compare alone. Use the **Loaded mod bundles** foldout to see mounts (excl. vanilla) and **Force unload**.
- **Folder / zip / unitypackage:** Import, side-by-side Compare, and load-order Validate stage text into `Assets/_PatcherStage/<ModName>/…` temporarily, then delete it. Leftover empty folders are harmless — delete manually if needed.

### Loaded Mod Bundles
After Compare with `.assetbundle` mods, open **Loaded mod bundles (excl. vanilla)** under the source list. Remount only happens when a mount is missing or stale; otherwise Compare reuses the provider. Vanilla (`mercurydatabases.assetbundle`) is owned by Mod Tools and is never listed or unloaded here.

### Vanilla Cache
The validation panel caches vanilla databases for the session. If you change the Humankind folder or remount vanilla, use **Tools → Debug → Compat Patcher → Clear Vanilla Validation Cache** to force a reload.

### Mapper Elements
Import (main window or Compare) brings **only the chosen/primary element** — matching mappers (UIMapper, DescriptorMapper, etc.) are not auto-imported. If a mapper itself conflicts, resolve and import that row separately.

### Re-Patching Workflow
1. First time: add mods → Compare → Mark resolved and/or Import → sidecar auto-saves
2. Source mod updates: same mods + sidecar path → Compare → only changed/new need review
3. Repeat — resolved fingerprints keep unchanged rows out of “needs review”

---

## Keyboard Shortcuts

The window uses standard Unity IMGUI controls:
- **Click** a row to select it
- **Scroll** in any panel independently
- **Drag** the splitter in the Compare window to resize the element list

---

## Troubleshooting

### "Compare" Button Greyed Out
You need at least 2 mods added.

### "No known load-time hazards detected"
Good — your load order is clean. If you expected hazards, check that vanilla databases are mounted (Mod Editor → set Humankind folder).

### Element Shows "changed" But I Didn't Edit It
The source mod was updated since your last sidecar export. Review the diff and re-export the sidecar.

### Patch Folder Is Empty
You haven't imported anything yet. Select conflicts and click **Import chosen into Patch/**, or use **Import all conflicts (Winner Mod)**.

### Validation Panel Shows "Vanilla databases not mounted"
Set the Humankind folder in the Mod Editor (Mercury → Mod Editor → Settings), then Compare again. Without vanilla, the validator can only check mods against each other.

---

## File Reference

| File | Purpose |
|------|---------|
| `CompatPatcherWindow.cs` | Main window UI and workflow |
| `CompatCompareWindow.cs` | Side-by-side inspector |
| `CompatBundleMounts.cs` | Session mounts for mod `.assetbundle`s |
| `LiveElementBuilder.cs` | In-memory `HkElement` from live SO |
| `ModReader.cs` | Reads mods (.unitypackage, .zip, .assetbundle, folder) |
| `ConflictAnalyzer.cs` | N-way diff and classification |
| `PatchBuilder.cs` | Stages source files and imports into Patch/ |
| `Sidecar.cs` | Decision + fingerprint persistence |
| `DiffGui.cs` | Shared diff rendering |
| `LoadOrderValidator.cs` | Crash-hazard validation |
| `UnityYaml.cs` | YAML parser for Unity .asset files |
