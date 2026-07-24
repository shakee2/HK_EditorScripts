# HK Mod Tools

Unity Editor tools for Humankind modding — database editing, custom unit visuals, compatibility patching, and diagnostics.

As of v1.1.0 this repo is a **multi-package monorepo**: instead of one big package, it ships four independent UPM packages under [`Packages/`](Packages/), so you only install what you actually use.

## Requirements

- Unity 2021.3+
- Humankind Mod Tools installation
- Each package referenced via a UPM git URL (or local `file:` reference) in a Humankind modding project

## Packages

| Package | Contents | Depends on | Status |
|---|---|---|---|
| [`com.hk.modtools.shared`](Packages/com.hk.modtools.shared) | Vanilla database bundle mount, Mod Editor translations mount, floating-window minimize helper, the cross-package Update Checker, the Tools Options window | — | Released |
| [`com.hk.modtools.core`](Packages/com.hk.modtools.core) | Database Browser, Descriptor Property Browser, Tech Tree viewer/editor, Unit Family Lines browser, Asset Explorer, Build/Deploy window, inspector upgrades (inline localization, tooltip preview, formula autocomplete, diagnostics), debug probes | `shared` | Released |
| [`com.hk.modtools.compatpatcher`](Packages/com.hk.modtools.compatpatcher) | Compatibility Patcher | `shared` | Pre-release (`0.x`) when tagged |
| [`com.hk.modtools.unitvisuals`](Packages/com.hk.modtools.unitvisuals) | Custom unit visual pipeline wizard | `shared` | Experimental — not yet tagged |
| [`com.hk.modtools.orphanfinder`](Packages/com.hk.modtools.orphanfinder) | Orphan Resource Finder — find unreferenced images / 3D resources in a Resources folder and delete them to shrink the mod | `shared`¹ | Released |

¹ `orphanfinder` is fully self-contained (uses none of `shared`'s types); it declares `shared` only so it joins the shared Update Checker / Options ecosystem. Drop the dependency if you want it truly standalone.

**`shared` is a real, required dependency of every other package here** — install it first. `0.x` tags are pre-release (Options shows `v0.1.0 (pre-release)`); stable starts at `1.0.0`. Untagged packages stay invisible to pinned installs — use a `file:` path or `main` only while actively developing them.

## Installing a Package

**Easiest path (after Shared is present):** open **Tools → shakee's Tools → Options** and use the Packages list — each tool has Install / Update / Remove. Installing or updating a tool also bumps required dependencies that are missing or below the minimum version (Shared itself must already be present — Options lives in Shared).

**First-time install of Shared** (bootstrap — Options lives *in* Shared, so this one still goes through Package Manager):

1. Open **Window → Package Manager**.
2. Click the **+** button (top-left) → **Add package from git URL...**
3. Install `com.hk.modtools.shared`:
   ```
   https://github.com/shakee2/HK_EditorScripts.git?path=Packages/com.hk.modtools.shared#shared/1.1.1
   ```
4. After the domain reload, open **Tools → shakee's Tools → Options** and Install the tool package(s) you want from the list (or keep using Package Manager git URLs if you prefer):
   ```
   https://github.com/shakee2/HK_EditorScripts.git?path=Packages/com.hk.modtools.core#core/1.1.0
   ```
   Always pin to a [tag](https://github.com/shakee2/HK_EditorScripts/tags), never a branch name. Each package versions independently (`<shortname>/<semver>`).

**If you install a tool package without `shared` present:** Unity's Package Manager does *not* refuse the install outright — it adds the package, then shows an error icon next to it with a dependency error, plus Console compile errors for the missing shared types. Installing `shared` resolves it. The Options window avoids that by chaining Shared ahead of the tool when needed.

To get a later release: use **Update** / **Update All** on the Options cards after **Check All** refreshes tags, or `Tools/shakee's Tools/Check For Updates` (opens Options and refreshes; also runs on a per-package auto-check schedule).

If you're developing one of these packages yourself (not just consuming it), reference it with a local path instead — e.g. `"com.hk.modtools.core": "file:../relative/path/to/HK_EditorScripts/Packages/com.hk.modtools.core"` in `manifest.json` — so edits are picked up live without needing a tag. The Update Checker skips local references since there's nothing meaningful to compare a local checkout against.

## Migrating from the pre-1.1.0 single-package layout

If your project still points at `https://github.com/shakee2/HK_EditorScripts.git#<tag>` (no `?path=`), that's the retired single-package layout — it stopped receiving updates at tag `1.1.0`, which patched the old `UpdateChecker` to show a one-time "this package has moved" notice instead of silently going stale. To migrate:

1. Remove the old `com.shakee.hk-editorscripts` entry from `Packages/manifest.json` (or remove it via Package Manager).
2. Follow [Installing a Package](#installing-a-package) above for `shared` + whichever tool packages you were using — everything that was in the old single package is now split across `shared` + `core` (Debug/, UnitVisualWorkflow/, and CompatPatcher/ are unchanged in spirit, just now their own packages).

## Repo Layout

```
Packages/
  com.hk.modtools.shared/         VanillaDatabaseMount.cs, ArchiveTranslations.cs, WindowMinimize.cs, UpdateChecker.cs, ToolsOptionsWindow.cs
  com.hk.modtools.core/           ModTools/, Upgrades/, Debug/, ExportModEditorScriptsPackage.cs, Docs/manual.md
  com.hk.modtools.compatpatcher/  CompatPatcher/, Docs/CompatPatcher*.md
  com.hk.modtools.unitvisuals/    UnitVisualWorkflow/
  com.hk.modtools.orphanfinder/   OrphanResourceFinderWindow.cs
EditorWindow-Dependencies.md      cross-file dependency map (needed before exporting/packaging a subset of tools)
AGENTS.md                         orientation doc for AI agents working on this repo
```

Within each package's `Editor/` folder, files stay grouped by role in subfolders (`ModTools/`, `Upgrades/`, `Debug/`, `CompatPatcher/`, `UnitVisualWorkflow/`) — see [`EditorWindow-Dependencies.md`](EditorWindow-Dependencies.md) for the full per-file breakdown.

## Tools Overview

### `com.hk.modtools.core`

#### Database Browsing & Editing
| Tool | Menu | Description |
|------|------|-------------|
| **DatabaseBrowser** | `Tools/shakee's Tools/Database Browser` | Searchable list of all database elements with Unity inspector integration |
| **DescriptorPropertyIndex** | `Tools/shakee's Tools/Descriptor Property Browser` | Searchable index of Descriptor/DescriptorMapper rows with formula, scope, and duplicate detection |
| **TechTreeWindow** | `Tools/shakee's Tools/Tech Tree Viewer` | Tech tree viewer/editor with drag-to-reposition, prerequisite editing, and copy-on-write save |
| **TechTreeData** | `Tools/shakee's Tools/Debug/Tech Tree/Diagnose Mod Split`, `.../Dump Data` | Tech tree data layer backing the viewer; debug dumps for mod-split diagnosis and raw data inspection |
| **UnitFamilyLinesWindow** | `Tools/shakee's Tools/Unit Family Lines` | Pan/zoom DAG of `UnitFamilyDefinition` upgrade lines; edit Next + unit family/level; Import/Export JSON (assignments + `next`, creates missing families) |
| **UnitFamilyLinesData** | `Tools/shakee's Tools/Debug/Unit Family Lines/Dump Data` | Data + layout + EnsureWritable / JSON import for Unit Family Lines |
| **DescriptorMapperPreview** | `Tools/shakee's Tools/Debug/Descriptor Mapper Preview/Clear Name Cache` | Live tooltip-breakdown preview for Descriptor/DescriptorMapper assets, drawn via `InspectorAnalysisPanel`. Also feeds `PropertyEffectDrawer`'s inline "Rendered" preview and `InspectorDiagnostics`' structural checks |
| **DescriptorMapperGenerator** | — | Toolbar on Descriptor inspectors: generate or select a paired DescriptorMapper (UIMapper generation deferred — many definition-specific subclasses) |
| **LocalizationKeyStringDrawer** | — | Inline translation editor below `%key` fields on UIMapper and DescriptorMapper assets (Odin drawer) |
| **PropertyEffectDrawer** | — | Inspector drawer for `PropertyEffect` rows — see [PropertyEffect Editing](#propertyeffect-editing) below |
| **InspectorAnalysisPanel** | — | Hosts `DescriptorMapperPreview` and `InspectorDiagnostics` in one height-capped, scrollable container under the inspector header, with a shared "Analysis" toggle row (see below) |

#### Asset & Bundle Tools
| Tool | Menu | Description |
|------|------|-------------|
| **AssetExplorer** | `Tools/shakee's Tools/Asset Explorer` | Browse vanilla / mounted / Open… `.assetbundle`s, list descriptors, preview, import into project |
| **GuidLookup** | `Tools/shakee's Tools/Debug/Find GUID In Vanilla Bundles` | Resolve an Amplitude GUID against all vanilla bundles |
| **BundleContentProbe** | `Tools/shakee's Tools/Debug/Inspect Built Mod Bundle` | Inspect the mod's built assetbundle to confirm what shipped |

#### Build & Export
| Tool | Menu | Description |
|------|------|-------------|
| **ModBuildWindow** | `Tools/shakee's Tools/Build And Deploy Mod` | Lightweight build + deploy to Community folder (alternative to Mod Editor) |
| **ExportModEditorScriptsPackage** | `Tools/shakee's Tools/Export Mod Editor Scripts Package` | Export the core interconnected editor scripts + [`Docs/manual.md`](Packages/com.hk.modtools.core/Docs/manual.md) as `ModEditorScripts.unitypackage` (see [Export package](#export-package-modeditorscriptsunitypackage) below) |

#### Debug & Diagnostics
| Tool | Menu | Description |
|------|------|-------------|
| **Probing** (`PawnFragmentProbe`) | `Tools/shakee's Tools/Debug/Pawn Probe/1. Find Presentation Assets`, `Tools/shakee's Tools/Debug/Dump Selected Element Fields`, `Tools/shakee's Tools/Debug/Pawn Probe/3. Try Clone Selected Fragment` | Three-step diagnostic for presentation-pawn assets (find, dump, clone) — step 2 lives directly under `Debug/`, not the `Pawn Probe` submenu |
| **FormulaProbe** | `Tools/shakee's Tools/Debug/Probes/Resolve Types + Test ToString` | Resolve Descriptor/Effect types and test formula/RPN serialization |
| **FormulaAutocompleteProbe** | `Tools/shakee's Tools/Debug/Probes/Formula Compiler Probe` | Tests the formula-autocomplete DATA path (entity types, `RpnTextCompiler.GetTypeFieldLabels`, Parse/Validate/Compile round-trip) independent of the inspector UI |
| **NarrativeEventDiagnostic** | `Tools/shakee's Tools/Debug/Find Bad NarrativeEventDefinition` | Find the vanilla narrative event that throws NRE from OnValidate |
| **InspectorDiagnostics** | — | Inline diagnostics panel for `IDatatableElement` assets (crash risks, broken refs, malformed RPN — reuses `DescriptorMapperPreview.CollectFindings`), plus an inline `%key` localization editor. Drawn by `InspectorAnalysisPanel` |

#### Export package (`ModEditorScripts.unitypackage`)

`ExportModEditorScriptsPackage.cs` ships **fourteen** `.cs` files (3 from `com.hk.modtools.shared`, 11 from `com.hk.modtools.core`), **`Upgrades/UIPictoTint.shader`**, **`Docs/manual.md`** (user manual), and their `.meta` GUIDs. Everything else stays package-local and is **not** in the `.unitypackage`:

| `.cs` file | Package | Tool / role |
|------------|---------|-------------|
| `VanillaDatabaseMount.cs` | `shared` | Shared vanilla-database bundle mount (foundation for the browsers below) |
| `ArchiveTranslations.cs` | `shared` | Mod Editor translations bundle mount + project override read/write |
| `WindowMinimize.cs` | `shared` | Floating-window minimize (lower-right strip stack) |
| `ModTools/TechTreeData.cs` | `core` | Tech tree data layer |
| `ModTools/TechTreeWindow.cs` | `core` | Tech tree viewer/editor window |
| `ModTools/UnitFamilyLinesData.cs` | `core` | Unit Family Lines data layer (not in export package) |
| `ModTools/UnitFamilyLinesWindow.cs` | `core` | Unit Family Lines browser (not in export package) |
| `ModTools/DatabaseBrowser.cs` | `core` | Database Browser window |
| `ModTools/DescriptorPropertyIndex.cs` | `core` | Descriptor Property Browser window |
| `Upgrades/DescriptorMapperPreview.cs` | `core` | Tooltip breakdown preview + `PropertyEffectDrawer` / diagnostics backend |
| `Upgrades/UIPictoTint.shader` | `core` | Alpha-tint shader for preview pictos (dep of DescriptorMapperPreview) |
| `Upgrades/PropertyEffectDrawer.cs` | `core` | PropertyEffect Odin drawer (formula autocomplete + inline render) |
| `Upgrades/InspectorAnalysisPanel.cs` | `core` | Inspector header host (mapper toolbar + preview + diagnostics) |
| `Upgrades/InspectorDiagnostics.cs` | `core` | Diagnostics engine, aggregate loc foldout, Database Browser badges |
| `Upgrades/DescriptorMapperGenerator.cs` | `core` | Generate/select paired DescriptorMapper |
| `Upgrades/LocalizationKeyDrawer.cs` | `core` | Inline `%key` translation on UIMapper / DescriptorMapper |
| `Upgrades/InlineLocalizationEditor.cs` | `core` | Shared loc Import/edit helpers (dep of drawer + diagnostics) |
| `Docs/manual.md` | `core` | User manual: handling, limits, per-tool summary |

Not exported: `AssetExplorer.cs`, `ModBuildWindow.cs`, the Debug probes, Compatibility Patcher, unit-visual workflow. Update `ExportModEditorScriptsPackage.cs`, **`Docs/manual.md`**, and this table together when the export set changes.

The translations bundle (`Assets/Editor/Resources/Translations/…`) ships with Mod Tools, not in this package.

### `com.hk.modtools.compatpatcher` (experimental)
| Tool | Menu | Description |
|------|------|-------------|
| **CompatPatcherWindow** | `Tools/shakee's Tools/Compatibility Patcher` | Pick source mods, set load order, diff conflicting elements, select per-element winners, mass-import / Mass Change into Patch folder |
| **CompatCompareWindow** | (opened from above) | Side-by-side inspector of one conflicting element across all mods + existing Patch version |
| **MassFieldChangeWindow** | (opened from Compat Patcher) | Bulk field ADD/PICK on already-imported Patch elements (filter or multi-select scope) |
| **DiffGui** | — | Shared diff rendering for both windows |
| **LoadOrderValidator** | `Tools/shakee's Tools/Debug/Compat Patcher/Clear Vanilla Validation Cache` | Validates load order against vanilla; menu item drops the cached vanilla baseline so the next Compare reloads it |

### `com.hk.modtools.unitvisuals` (experimental)
| Tool | Menu | Description |
|------|------|-------------|
| **UnitVisualWorkflow** | `Tools/shakee's Tools/Unit Visual Workflow (Experimental)` | Step-based wizard for auto tier-detection, FBX prep, mesh baking, fragment authoring, and validation |
| **PawnFragmentAuthor** | — | Fragment authoring and GUID-fix helpers |
| **Tier1MeshBaker** | — | Mesh-bake helpers for the wizard |
| **BoneStructureMatcher** | — | Structural bone matcher for auto tier-detection |
| **FbxPrepPipeline** | — | FBX-prep pipeline (multi-submesh combine, texture atlas, scale normalization) |
| **ModelRequirementsChecker** | — | End-to-end validator for baked fragments against runtime requirements |
| **AnimationManagerContent** | — | Populates AnimationManagerContent registry from baked fragments |
| **VanillaAssetResolver** | — | Load any asset by GUID from vanilla bundles (used by the wizard) |

### `com.hk.modtools.orphanfinder`
| Tool | Menu | Description |
|------|------|-------------|
| **OrphanResourceFinderWindow** | `Tools/shakee's Tools/Orphan Resource Finder` | Scan a Resources folder for **unreferenced** assets and delete them (to Trash) to shrink the built mod. **Kind** selector: *Images* (default), *3D / meshes*, or *All*. Auto-calibrates Amplitude's `{a,b,c,d}` GUID encoding each scan; in 3D/All mode it also reads Unity hex references and JSON `int[4]` GUID arrays (model registries) so baked meshes/skeletons/atlases referenced from JSON aren't false-flagged. Reference detection is intentionally generous (a false "referenced" only wastes space; a false "orphan" could delete a used asset) — review before deleting, and keep version control. |

## Update Checker + Options Window

`com.hk.modtools.shared` ships cross-package infrastructure:

- **`WindowMinimize.cs`** — fake minimize for floating tool windows (Tech Tree, Unit Family Lines, Database Browser in Window mode, Descriptor Property Browser, Compat Patcher + Compare as one paired slot). Parks a restore strip in the main editor's lower-right and stacks additional strips horizontally leftward.
- **`UpdateChecker.cs`** — discovers the Options catalog from namespaced git tags (`<shortname>/<semver>` — tagged packages only; experimentals stay invisible until tagged), fetches release tags, and drives `Client.Add` / `Client.Remove`. Available updates appear on the package cards — no "update available" dialog.
  - **Check All** (Options) / **Check For Updates** (menu) / opening Options / background auto-check: fetch the repo tag list once and refresh the cards in place.
  - **Update** / **Update All**: `Client.Add("<repo>.git?path=Packages/<full-package-name>#<shortname>/<version>")`. Display name / description / author / dependencies are read from each package's own `package.json` — `PackageInfo` when installed, or the manifest fetched at the target's latest tag when not. Deps that are missing (except Shared bootstrap — add Shared via Package Manager first) or below the declared minimum are bumped first; resumes across domain reload.
  - **What's new**: when a newer tag exists, Options shows a button that opens `ChangelogPopupWindow` — one collapsible card per version between installed and latest (from that package's `CHANGELOG.md` at the latest tag; lazy-fetched).
  - Each installed git package has its own auto-check on/off + interval (default: every 14 days).
  - Local `file:` references are listed (Remove available) but skipped for update comparison.
  - Not-installed cards show a **Requires:** line built from the fetched manifest's declared dependencies. New tagged packages appear automatically after the next tag-list refresh — no catalog edit in Shared.
- **`ToolsOptionsWindow.cs`** (`Tools/shakee's Tools/Options`) — **Packages** cards with Install / Update (only when a newer tag exists) / What's new / Remove, **Check All** + **Update All**, auto-check controls, **Keybinds**, and Formula Autocomplete toggles when Core is installed.
- **`ChangelogPopupWindow.cs`** — utility popup for release notes (per-version foldout cards).

### Changelog convention

Each releasable package ships `Packages/<full-package-name>/CHANGELOG.md` (Keep a Changelog). Headings must be `## [x.y.z]` (optional ` - YYYY-MM-DD`) matching the namespaced tag `shortname/x.y.z`. Put in-progress notes under `## [Unreleased]` and move them into a version section when you cut the tag. The Options What's new UI fetches this file from the latest tag and aggregates every intervening version into one popup.

## Inline Localization Editing

`LocalizationKeyDrawer.cs` + `InlineLocalizationEditor.cs` (both in `com.hk.modtools.core`) add a translation box directly below any `%key` string field on **UIMapper** (and subclasses — **Title**, **Description**, facet titles: the in-game UI labels) and **DescriptorMapper** assets (LocalizedName, EffectLocalization, …). No need to open the Mod Editor Localization Window for these.

- **With Odin Inspector** (`LocalizationKeyStringDrawer : OdinValueDrawer<string>`): calls `CallNextDrawer` so the normal key field draws unchanged, then appends a compact help-box with the resolved vanilla text, an **Import for editing** button (creates a project override row via `ArchiveTranslations.EnsureOverride`, same as Tech Tree), and an editable `TextArea` once imported. Edits save immediately into `Assets/Localization/Translations/Translations.asset`.
- The aggregate **Localization** foldout in `InspectorDiagnostics` reuses the same `InlineLocalizationEditor` helpers — useful when you want every key on an element in one place, but the per-field boxes are the primary editing surface for mappers.
- Tooltip preview (`DescriptorMapperPreview`) and diagnostics invalidate when translation text changes so resolved labels refresh without a domain reload.

## PropertyEffect Editing

`PropertyEffectDrawer.cs` (in `com.hk.modtools.core`) adds intellisense and a live Rendered tooltip preview on top of Amplitude's own `PropertyEffect` editor, without replacing it.

- **With Odin Inspector** (`PropertyEffectOdinDrawer : OdinValueDrawer<PropertyEffect>`): calls `CallNextDrawer` first so Amplitude's own formula field, operation/property dropdowns, and Source/Target browser draw exactly as normal, then layers on top:
  - **Autocomplete directly on Amplitude's own formula field** — detects when Amplitude's `FormulaTextArea` control is focused (via its recycled `TextEditor`, reflection-located since it's internal), computes `Source.`/`Target.`/`World.` and property-name completions, and shows a styled floating dropdown anchored under the field. Arrow keys / Ctrl+E / Ctrl+D / → confirm an insert, which is written straight into the active `TextEditor` and re-parsed through Amplitude's own `RpnTextCompiler` so its Compile step picks it up.
  - **A secondary "Formula (autocomplete)" foldout** — an independent `GUILayout.TextField` backed by its own `RpnTextCompiler` instance (seeded by decompiling the current RPN), with the same completion popup, an "Apply to RPN" button (only enabled once Parse+Validate succeed) and "Reload from RPN". Because it only ever writes to the serialized arrays after `Parse()==Ok && Validate()==Ok`, it's impossible to write invalid RPN through it.
  - **"Rendered" preview** — resolves the row through `DescriptorMapperPreview.ResolvePropertyEffect` into the same substituted tooltip line the header preview shows, plus any warnings (hidden row, `SolveRPNFormula` live-evaluated, malformed RPN, missing mapper). Bracket icon tags (`[ScienceColored]`, …) draw the matching UIMapper Picto when available. Cached per-element and rebuilt only when `DescriptorMapperPreview.Generation` bumps or 1s elapses. Suppressed entirely when `DescriptorMapperPreview.IsActive` is off.
  - A `SuperPriority` `FormulaSuggestionOverlayBlockerDrawer` eats pointer events under the open suggestion popup so clicks land on the popup, not the fields beneath it.
- **Without Odin** (`[CustomPropertyDrawer(typeof(PropertyEffect))]` fallback): a plain `PropertyDrawer` with the same "Rendered" HelpBox, a Target Property field with a ▼ button opening a searchable `PropertyNameDropdown` of all known `PropertyMapper` names (via `DescriptorMapperPreview.GetAvailablePropertyNames`), and an editable "Property Local Name" array foldout with +/- row controls.
- **FormulaSuggestionDropdownGui** / **FormulaSuggestionOverlaySession** — shared, Odin-inspector-safe infrastructure for painting/scrolling/picking the suggestion popup and blocking underlying controls from stealing its clicks; used by both the Amplitude-field overlay and the secondary autocomplete field.

## License

MIT License — see [LICENSE](LICENSE)
