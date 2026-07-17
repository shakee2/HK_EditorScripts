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
| [`com.hk.modtools.shared`](Packages/com.hk.modtools.shared) | Vanilla database bundle mount, Mod Editor translations mount, the cross-package Update Checker, the Tools Options window | — | Released |
| [`com.hk.modtools.core`](Packages/com.hk.modtools.core) | Database Browser, Descriptor Property Browser, Tech Tree viewer/editor, Asset Explorer, Build/Deploy window, inspector upgrades (inline localization, tooltip preview, formula autocomplete, diagnostics), debug probes | `shared` | Released |
| [`com.hk.modtools.compatpatcher`](Packages/com.hk.modtools.compatpatcher) | Compatibility Patcher | `shared` | Experimental — not yet tagged |
| [`com.hk.modtools.unitvisuals`](Packages/com.hk.modtools.unitvisuals) | Custom unit visual pipeline wizard | `shared` | Experimental — not yet tagged |
| [`com.hk.modtools.orphanfinder`](Packages/com.hk.modtools.orphanfinder) | Orphan Resource Finder — find unreferenced images / 3D resources in a Resources folder and delete them to shrink the mod | `shared`¹ | Released |

¹ `orphanfinder` is fully self-contained (uses none of `shared`'s types); it declares `shared` only so it joins the shared Update Checker / Options ecosystem. Drop the dependency if you want it truly standalone.

**`shared` is a real, required dependency of every other package here** — install it first. Unlike Debug/UnitVisualWorkflow being release-branch-stripped in the old single-package layout, `compatpatcher`/`unitvisuals` simply ship no tag until they're stable enough to release; installing them today means pointing at `main` (unpinned, changes underneath you) — not recommended outside active development on those tools.

## Installing a Package

1. In your modding project, open **Window → Package Manager**.
2. Click the **+** button (top-left) → **Add package from git URL...**
3. **Install `com.hk.modtools.shared` first:**
   ```
   https://github.com/shakee2/HK_EditorScripts.git?path=Packages/com.hk.modtools.shared#shared/1.0.0
   ```
4. Then any tool package(s) you want, e.g. `core`:
   ```
   https://github.com/shakee2/HK_EditorScripts.git?path=Packages/com.hk.modtools.core#core/1.0.0
   ```
   (swap the version for whichever [tag](https://github.com/shakee2/HK_EditorScripts/tags) you want — always pin to a tag, never a branch name, so your project doesn't silently change behavior on a future push. Each package versions independently; check the tag list for the `<shortname>/` prefix that matches the package you're installing.)
5. Click **Add** for each. Unity clones/checks out the tagged subfolder and compiles it; menu items appear under `Tools/shakee's Tools/...` after the next domain reload.

**If you skip step 3 and install a tool package without `shared` present:** Unity's Package Manager does *not* refuse the install outright — it adds the package, then shows an error icon next to it in the package list with a "Dependency error message" in its details panel, and you'll also see compile errors in the Console for the missing shared types. It's a clear, discoverable signal, just not a pre-flight block — installing `shared` (in either order, actually) resolves it immediately.

To get a later release of an installed package, repeat step 4 with the new tag, or use the built-in [Options window](#update-checker--options-window) (`Tools/shakee's Tools/Options`, or `Tools/shakee's Tools/Check For Updates` for a quick manual check) — it checks every installed `com.hk.modtools.*` package and prompts automatically on its own schedule too.

If you're developing one of these packages yourself (not just consuming it), reference it with a local path instead — e.g. `"com.hk.modtools.core": "file:../relative/path/to/HK_EditorScripts/Packages/com.hk.modtools.core"` in `manifest.json` — so edits are picked up live without needing a tag. The Update Checker skips local references since there's nothing meaningful to compare a local checkout against.

## Migrating from the pre-1.1.0 single-package layout

If your project still points at `https://github.com/shakee2/HK_EditorScripts.git#<tag>` (no `?path=`), that's the retired single-package layout — it stopped receiving updates at tag `1.1.0`, which patched the old `UpdateChecker` to show a one-time "this package has moved" notice instead of silently going stale. To migrate:

1. Remove the old `com.shakee.hk-editorscripts` entry from `Packages/manifest.json` (or remove it via Package Manager).
2. Follow [Installing a Package](#installing-a-package) above for `shared` + whichever tool packages you were using — everything that was in the old single package is now split across `shared` + `core` (Debug/, UnitVisualWorkflow/, and CompatPatcher/ are unchanged in spirit, just now their own packages).

## Repo Layout

```
Packages/
  com.hk.modtools.shared/         VanillaDatabaseMount.cs, ArchiveTranslations.cs, UpdateChecker.cs, ToolsOptionsWindow.cs
  com.hk.modtools.core/           ModTools/, Upgrades/, Debug/, ExportModEditorScriptsPackage.cs, Docs/manual.md
  com.hk.modtools.compatpatcher/  CompatPatcher/, Docs/CompatPatcher*.md
  com.hk.modtools.unitvisuals/    UnitVisualWorkflow/
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
| **DescriptorMapperPreview** | `Tools/shakee's Tools/Debug/Descriptor Mapper Preview/Clear Name Cache` | Live tooltip-breakdown preview for Descriptor/DescriptorMapper assets, drawn via `InspectorAnalysisPanel`. Also feeds `PropertyEffectDrawer`'s inline "Rendered" preview and `InspectorDiagnostics`' structural checks |
| **DescriptorMapperGenerator** | — | Toolbar on Descriptor inspectors: generate or select a paired DescriptorMapper (UIMapper generation deferred — many definition-specific subclasses) |
| **LocalizationKeyStringDrawer** | — | Inline translation editor below `%key` fields on UIMapper and DescriptorMapper assets (Odin drawer) |
| **PropertyEffectDrawer** | — | Inspector drawer for `PropertyEffect` rows — see [PropertyEffect Editing](#propertyeffect-editing) below |
| **InspectorAnalysisPanel** | — | Hosts `DescriptorMapperPreview` and `InspectorDiagnostics` in one height-capped, scrollable container under the inspector header, with a shared "Analysis" toggle row (see below) |

#### Asset & Bundle Tools
| Tool | Menu | Description |
|------|------|-------------|
| **AssetExplorer** | `Tools/shakee's Tools/Asset Explorer` | Mount vanilla `.assetbundle` files, list descriptors, preview assets, import into project |
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

`ExportModEditorScriptsPackage.cs` ships **thirteen** `.cs` files (2 from `com.hk.modtools.shared`, 11 from `com.hk.modtools.core`), **`Docs/manual.md`** (user manual), and their `.meta` GUIDs. Everything else stays package-local and is **not** in the `.unitypackage`:

| `.cs` file | Package | Tool / role |
|------------|---------|-------------|
| `VanillaDatabaseMount.cs` | `shared` | Shared vanilla-database bundle mount (foundation for the browsers below) |
| `ArchiveTranslations.cs` | `shared` | Mod Editor translations bundle mount + project override read/write |
| `ModTools/TechTreeData.cs` | `core` | Tech tree data layer |
| `ModTools/TechTreeWindow.cs` | `core` | Tech tree viewer/editor window |
| `ModTools/DatabaseBrowser.cs` | `core` | Database Browser window |
| `ModTools/DescriptorPropertyIndex.cs` | `core` | Descriptor Property Browser window |
| `Upgrades/DescriptorMapperPreview.cs` | `core` | Tooltip breakdown preview + `PropertyEffectDrawer` / diagnostics backend |
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
| **CompatPatcherWindow** | `Tools/shakee's Tools/Compatibility Patcher` | Pick source mods, set load order, diff conflicting elements, select per-element winners, mass-import into Patch folder |
| **CompatCompareWindow** | (opened from above) | Side-by-side inspector of one conflicting element across all mods + existing Patch version |
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

`com.hk.modtools.shared` ships two pieces of cross-package infrastructure:

- **`UpdateChecker.cs`** — checks every installed `com.hk.modtools.*` package (resolved via a git URL, not a local reference) against its own namespaced tags on this repo (`<shortname>/<semver>`, e.g. `core/1.1.0`), and offers to update in place.
  - Fetches the repo's tag list **once** per check, then evaluates every package due for a check, and shows **one aggregated dialog** listing every package with an available update rather than one dialog per package.
  - Each package has its own auto-update on/off + check-interval, configurable in the Options window below (default: every 14 days).
  - Accepting an update calls `Client.Add("<repo>.git?path=Packages/<full-package-name>#<shortname>/<version>")` per package — the same call the Package Manager UI itself makes when you paste a git URL — sequentially for each accepted package.
  - "Skip This Version" is remembered per-package/tag (`EditorPrefs`) so the background check won't re-prompt for a release you've deliberately deferred, but a newer tag after that will still prompt.
- **`ToolsOptionsWindow.cs`** (`Tools/shakee's Tools/Options`) — lists every installed `com.hk.modtools.*` package with its own auto-update toggle, check-interval (in days), and a "Check Now" button, plus a **Keybinds** section. Rebinding this suite's shortcuts goes through Unity's own built-in **Edit → Shortcuts** manager (search "shakee's Tools" there) rather than a custom rebind UI — every tool here is a regular `[MenuItem]`, so Unity's shortcut editor already handles it.

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
  - **"Rendered" HelpBox** — resolves the row through `DescriptorMapperPreview.ResolvePropertyEffect` into the same substituted tooltip line the header preview shows, plus any warnings (hidden row, `SolveRPNFormula` live-evaluated, malformed RPN, missing mapper). Cached per-element and rebuilt only when `DescriptorMapperPreview.Generation` bumps or 1s elapses. Suppressed entirely when `DescriptorMapperPreview.IsActive` is off.
  - A `SuperPriority` `FormulaSuggestionOverlayBlockerDrawer` eats pointer events under the open suggestion popup so clicks land on the popup, not the fields beneath it.
- **Without Odin** (`[CustomPropertyDrawer(typeof(PropertyEffect))]` fallback): a plain `PropertyDrawer` with the same "Rendered" HelpBox, a Target Property field with a ▼ button opening a searchable `PropertyNameDropdown` of all known `PropertyMapper` names (via `DescriptorMapperPreview.GetAvailablePropertyNames`), and an editable "Property Local Name" array foldout with +/- row controls.
- **FormulaSuggestionDropdownGui** / **FormulaSuggestionOverlaySession** — shared, Odin-inspector-safe infrastructure for painting/scrolling/picking the suggestion popup and blocking underlying controls from stealing its clicks; used by both the Amplitude-field overlay and the secondary autocomplete field.

## License

MIT License — see [LICENSE](LICENSE)
