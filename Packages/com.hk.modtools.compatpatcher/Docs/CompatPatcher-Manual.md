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
6. Populates the three panels below

Use **Resolve all as winner** to mark still-unreviewed conflicts in the **current filtered list** as resolved (accepts each load-order winner, writes sidecar, no import).

### Patch orphans (after Compare)

Compare also diffs **`Assets/Databases/Patch/`** against the loaded mods. A foldout under the stats line lists **orphans** — Patch/ elements that still override at load but no longer sit on a live multi-mod conflict:

| Kind | Meaning |
|---|---|
| **gone from mods** | No compared mod defines this name anymore — Patch/ alone forces it |
| **sole mod left** | Only one mod still has it — the conflict dissolved; Patch/ still overrides that mod |
| **mods agree** | ≥2 mods still have it but Compare sees them Identical — Patch/ overrides for no conflict |

**Ping** selects the patch asset; **Remove** deletes that named element from the Patch/ collection. Keep the row if you still want a deliberate custom edit. Full dump also goes to the Console each Compare.

---

## The Three Panels

### Panel 1: Load-Order Validation (top)

Shows hazards that would cause the game to reset to vanilla on load. Each finding includes:
- **Severity**: ERROR (will reset) or warning
- **Code**: the validation rule (e.g. `:4734` for mixed-family unlock events)
- **Element**: the element that triggers the hazard
- **Detail**: what's wrong and which mod introduces it
- **[order-sensitive]**: tag if reordering would change the outcome

If there are order-sensitive findings, the panel tells you how many would disappear or change under the reversed order — use **▲ ▼** to reorder and **Compare** again.

> If vanilla databases aren't mounted, the panel shows a warning and only checks mods against each other (not against vanilla). Set the Humankind folder in the Mod Editor and Compare again for full validation.

### Panel 2: Element List (middle)

A table of all elements across the loaded mods. Columns:

| Column | Meaning |
|--------|---------|
| **Element** | Element name |
| **Type** | Element class (e.g. `TechnologyDefinition`) |
| **By** | Which mods define it |
| **Winner** | Load-order winner (or your chosen mod) |
| **Status** | `new` / `changed` / `✓resolved` / `conflict` / `identical` + `✓in patch` if imported |
| **Diffs** | Summary like `2 ADD, 1 PICK` |

Click a row to select it and see details in Panel 3.

**Filters** (AND-combined):
- **Status toolbar**: Conflicts / All / New / Identical
- **Name contains**: substring filter
- **Type**: dropdown populated from loaded elements
- **needs review only**: only conflicts still needing a decision (hides ✓resolved / ✓in patch)
- **hide winner-only**: hides conflicts where the only diffs are `ExtraInWinner` (already in the winner mod, no action needed)

Click a row to select it and see details in Panel 3.

### Panel 3: Element Detail (bottom)

For the selected element:

**If it's a conflict** (present in multiple mods with differences):
- **Which mod's version wins?** — radio buttons for each contributing mod (defaults to load-order winner)
- **Import chosen into Patch/** — duplicates that mod's version into `Assets/Databases/Patch/`
- **Mark resolved (accept chosen)** — winner/choice is fine; no import. Writes the sidecar (`✓resolved`)
- **Compare side-by-side** — opens the Compare window (see below)
- **Differences table** — read-only field-by-field diff showing:
  - `MISSING in winner` — a field/entry another mod has that the winner lacks
  - `CHANGED` — present everywhere but values differ
  - `ONLY in winner` — the winner has something others don't (informational)

**If it's new** (single mod only):
- **Import & Edit into Patch/** — bring it into the patch so you can edit it
- **Compare side-by-side** — view it in the Compare window

**If it's already handled** (`✓resolved` and/or `✓in patch`):
- Resolved = accepted without Patch override; In patch = imported. Both leave “needs review.”
- Import is disabled when already in patch; Mark resolved is disabled when already resolved or in patch.
- Use **Compare side-by-side** to inspect (and edit the patch version if present).

---

## Side-by-Side Compare Window

Click **Compare side-by-side** from the detail panel to open a separate window showing:

- **Left**: searchable list of elements (from your current filtered view), with type filter and optional Group-by-type (same pattern as Database Browser)
- **Right**: one column per contributing mod + a **Patch** column (if the element is already in the patch)

Each column stacks the element and its matching mappers (UIMapper, DescriptorMapper, etc.) as separate blocks. Each block is headed with the **concrete type** and **element name** (not the collection file stem).

- **Source columns** are read-only (staged scratch copies)
- **Patch column** is editable — changes are saved on close

Click **Import this version into Patch/** on any source column to bring that mod's **primary element only** into the patch (same as the main window — attached mappers are not auto-imported). The Patch column refreshes in place so you can immediately edit it.

Click **Resolve as winner** to accept the load-order winner without importing (`✓resolved` + sidecar). List markers: **●** = in patch, **○** = resolved.

> **Tip:** Use this window when you need to see the full inspector view of each version before deciding, or when you want to import a version and hand-edit it.

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

Both operate on the **current filtered list** only (status / name / type / needs-review / hide-winner-only):

- **Resolve all as winner** — mark listed unresolved conflicts resolved (load-order winner each). Sidecar only; no import.
- **Import all conflicts (chosen)** — import listed unresolved conflicts' chosen versions into `Patch/`.

> Import is whole-element. For per-field merges, import one version then hand-edit in the inspector or the Compare window's Patch column.

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
Some element types keep lists in an Odin serialization stream (e.g. `TechnologyDefinition.SimulationEventEffects`, narrative `Choices[].NarrativeEventEffects`). For **live / assetbundle** sources the patcher reflection-merges those effect trees into the compare body, so field diffs (Amount, UnlockAction enums, refs, …) show like any other StructDiff row. Pure-Odin elements that still have no Flatten body (e.g. some YAML-only unitypackage carriers without a live object) fall back to **references only** — use **Compare side-by-side** for the full inspector.

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
You haven't imported anything yet. Select conflicts and click **Import chosen into Patch/**, or use **Import all conflicts (chosen)**.

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
