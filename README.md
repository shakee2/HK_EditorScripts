# HK Editor Scripts

Unity Editor tools for Humankind modding — database editing, custom unit visuals, compatibility patching, and diagnostics.

## Requirements

- Unity 2021.3+
- Humankind Mod Tools installation
- Referenced as a local UPM package in a Humankind modding project

## Tools Overview

### Compatibility Patcher
| Tool | Menu | Description |
|------|------|-------------|
| **CompatPatcherWindow** | `Tools/shakee's Tools/Compatibility Patcher` | Pick source mods, set load order, diff conflicting elements, select per-element winners, mass-import into Patch folder |
| **CompatCompareWindow** | (opened from above) | Side-by-side inspector of one conflicting element across all mods + existing Patch version |
| **DiffGui** | — | Shared diff rendering for both windows |
| **LoadOrderValidator** | `Tools/shakee's Tools/Debug/Compat Patcher/Clear Vanilla Validation Cache` | Validates load order against vanilla; menu item drops the cached vanilla baseline so the next Compare reloads it |

### Database Browsing & Editing
| Tool | Menu | Description |
|------|------|-------------|
| **DatabaseBrowser** | `Tools/shakee's Tools/Database Browser` | Searchable list of all database elements with Unity inspector integration **📦** |
| **DescriptorPropertyIndex** | `Tools/shakee's Tools/Descriptor Property Browser` | Searchable index of Descriptor/DescriptorMapper rows with formula, scope, and duplicate detection **📦** |
| **TechTreeWindow** | `Tools/shakee's Tools/Tech Tree Viewer` | Tech tree viewer/editor with drag-to-reposition, prerequisite editing, and copy-on-write save **📦** |
| **TechTreeData** | `Tools/shakee's Tools/Debug/Tech Tree/Diagnose Mod Split`, `.../Dump Data` | Tech tree data layer backing the viewer; debug dumps for mod-split diagnosis and raw data inspection **📦** |
| **DescriptorMapperPreview** | `Tools/shakee's Tools/Debug/Descriptor Mapper Preview/Clear Name Cache` | Live tooltip-breakdown preview for Descriptor/DescriptorMapper assets, drawn via `InspectorAnalysisPanel`. Also feeds `PropertyEffectDrawer`'s inline "in-game render" and `InspectorDiagnostics`' structural checks **📦** |
| **DescriptorMapperGenerator** | — | Toolbar on Descriptor inspectors: generate or select a paired DescriptorMapper (UIMapper generation deferred — many definition-specific subclasses) **📦** |
| **LocalizationKeyStringDrawer** | — | Inline translation editor below `%key` fields on UIMapper and DescriptorMapper assets (Odin drawer) **📦** |
| **PropertyEffectDrawer** | — | Inspector drawer for `PropertyEffect` rows — see [PropertyEffect Editing](#propertyeffect-editing) below **📦** |
| **InspectorAnalysisPanel** | — | Hosts `DescriptorMapperPreview` and `InspectorDiagnostics` in one height-capped, scrollable container under the inspector header, with a shared "Analysis" toggle row (see below) **📦** |

### Custom Unit Visuals
| Tool | Menu | Description |
|------|------|-------------|
| **UnitVisualWorkflow** | `Tools/shakee's Tools/Unit Visual Workflow (Experimental)` | Step-based wizard for auto tier-detection, FBX prep, mesh baking, fragment authoring, and validation |
| **PawnFragmentAuthor** | — | Fragment authoring and GUID-fix helpers |
| **Tier1MeshBaker** | — | Mesh-bake helpers for the wizard |
| **BoneStructureMatcher** | — | Structural bone matcher for auto tier-detection |
| **FbxPrepPipeline** | — | FBX-prep pipeline (multi-submesh combine, texture atlas, scale normalization) |
| **ModelRequirementsChecker** | — | End-to-end validator for baked fragments against runtime requirements |
| **AnimationManagerContent** | — | Populates AnimationManagerContent registry from baked fragments |

### Asset & Bundle Tools
| Tool | Menu | Description |
|------|------|-------------|
| **AssetExplorer** | `Tools/shakee's Tools/Asset Explorer` | Mount vanilla `.assetbundle` files, list descriptors, preview assets, import into project **📦** |
| **GuidLookup** | `Tools/shakee's Tools/Debug/Find GUID In Vanilla Bundles` | Resolve an Amplitude GUID against all vanilla bundles |
| **BundleContentProbe** | `Tools/shakee's Tools/Debug/Inspect Built Mod Bundle` | Inspect the mod's built assetbundle to confirm what shipped |
| **VanillaAssetResolver** | — | Load any asset by GUID from vanilla bundles (used by wizard) |
| **VanillaDatabaseMount** | `Tools/shakee's Tools/Debug/Tech Tree/Vanilla Mount`, `.../Force Re-mount Vanilla Database` | Mount vanilla database bundle for browsing without copying into project **📦** |
| **ArchiveTranslations** | — | Mount Mod Editor translations bundle for vanilla localization lookup **📦** |

### Build & Export
| Tool | Menu | Description |
|------|------|-------------|
| **ModBuildWindow** | `Tools/shakee's Tools/Build And Deploy Mod` | Lightweight build + deploy to Community folder (alternative to Mod Editor) |
| **ExportModEditorScriptsPackage** | `Tools/shakee's Tools/Export Mod Editor Scripts Package` | Export the core editor scripts + [`Docs/manual.md`](Docs/manual.md) as `ModEditorScripts.unitypackage` (see [Export package](#export-package-modeditorscriptsunitypackage) below) |

#### Export package (`ModEditorScripts.unitypackage`)

`ExportModEditorScriptsPackage.cs` ships **fourteen** `.cs` files, **`Docs/manual.md`** (user manual), and their `.meta` GUIDs. Everything else in this repo stays package-local and is **not** in the `.unitypackage`:

| `.cs` file | Tool / role |
|------------|-------------|
| `VanillaDatabaseMount.cs` | Shared vanilla-database bundle mount (foundation for the browsers below) |
| `ArchiveTranslations.cs` | Mod Editor translations bundle mount + project override read/write |
| `TechTreeData.cs` | Tech tree data layer |
| `TechTreeWindow.cs` | Tech tree viewer/editor window |
| `DatabaseBrowser.cs` | Database Browser window |
| `DescriptorPropertyIndex.cs` | Descriptor Property Browser window |
| `AssetExplorer.cs` | Asset Explorer window |
| `DescriptorMapperPreview.cs` | Tooltip breakdown preview + `PropertyEffectDrawer` / diagnostics backend |
| `PropertyEffectDrawer.cs` | PropertyEffect Odin drawer (formula autocomplete + inline render) |
| `InspectorAnalysisPanel.cs` | Inspector header host (mapper toolbar + preview + diagnostics) |
| `InspectorDiagnostics.cs` | Diagnostics engine, aggregate loc foldout, Database Browser badges |
| `DescriptorMapperGenerator.cs` | Generate/select paired DescriptorMapper |
| `LocalizationKeyDrawer.cs` | Inline `%key` translation on UIMapper / DescriptorMapper |
| `InlineLocalizationEditor.cs` | Shared loc Import/edit helpers (dep of drawer + diagnostics) |
| `Docs/manual.md` | User manual: handling, limits, per-tool summary |

Not exported (examples): Compatibility Patcher, unit-visual workflow, probes. Update `ExportModEditorScriptsPackage.cs`, **`Docs/manual.md`**, and this table together when the export set changes.

The translations bundle (`Assets/Editor/Resources/Translations/…`) ships with Mod Tools, not in this package.

In the tables above, **📦** = included in `ModEditorScripts.unitypackage`.

### Debug & Diagnostics
| Tool | Menu | Description |
|------|------|-------------|
| **Probing** (`PawnFragmentProbe`) | `Tools/shakee's Tools/Debug/Pawn Probe/1. Find Presentation Assets`, `Tools/shakee's Tools/Debug/Dump Selected Element Fields`, `Tools/shakee's Tools/Debug/Pawn Probe/3. Try Clone Selected Fragment` | Three-step diagnostic for presentation-pawn assets (find, dump, clone) — step 2 lives directly under `Debug/`, not the `Pawn Probe` submenu |
| **FormulaProbe** | `Tools/shakee's Tools/Debug/Probes/Resolve Types + Test ToString` | Resolve Descriptor/Effect types and test formula/RPN serialization |
| **FormulaAutocompleteProbe** | `Tools/shakee's Tools/Debug/Probes/Formula Compiler Probe` | Tests the formula-autocomplete DATA path (entity types, `RpnTextCompiler.GetTypeFieldLabels`, Parse/Validate/Compile round-trip) independent of the inspector UI |
| **NarrativeEventDiagnostic** | `Tools/shakee's Tools/Debug/Find Bad NarrativeEventDefinition` | Find the vanilla narrative event that throws NRE from OnValidate |
| **InspectorDiagnostics** | — | Inline diagnostics panel for `IDatatableElement` assets (crash risks, broken refs, malformed RPN — reuses `DescriptorMapperPreview.CollectFindings`), plus an inline `%key` localization editor. Drawn by `InspectorAnalysisPanel` **📦** |

## Inline Localization Editing

`LocalizationKeyDrawer.cs` + `InlineLocalizationEditor.cs` add a translation box directly below any `%key` string field on **UIMapper** (and subclasses — **Title**, **Description**, facet titles: the in-game UI labels) and **DescriptorMapper** assets (LocalizedName, EffectLocalization, …). No need to open the Mod Editor Localization Window for these.

- **With Odin Inspector** (`LocalizationKeyStringDrawer : OdinValueDrawer<string>`): calls `CallNextDrawer` so the normal key field draws unchanged, then appends a compact help-box with the resolved vanilla text, an **Import for editing** button (creates a project override row via `ArchiveTranslations.EnsureOverride`, same as Tech Tree), and an editable `TextArea` once imported. Edits save immediately into `Assets/Localization/Translations/Translations.asset`.
- The aggregate **Localization** foldout in `InspectorDiagnostics` reuses the same `InlineLocalizationEditor` helpers — useful when you want every key on an element in one place, but the per-field boxes are the primary editing surface for mappers.
- Tooltip preview (`DescriptorMapperPreview`) and diagnostics invalidate when translation text changes so resolved labels refresh without a domain reload.

## PropertyEffect Editing

`PropertyEffectDrawer.cs` adds intellisense and a live in-game render preview on top of Amplitude's own `PropertyEffect` editor, without replacing it.

- **With Odin Inspector** (`PropertyEffectOdinDrawer : OdinValueDrawer<PropertyEffect>`): calls `CallNextDrawer` first so Amplitude's own formula field, operation/property dropdowns, and Source/Target browser draw exactly as normal, then layers on top:
  - **Autocomplete directly on Amplitude's own formula field** — detects when Amplitude's `FormulaTextArea` control is focused (via its recycled `TextEditor`, reflection-located since it's internal), computes `Source.`/`Target.`/`World.` and property-name completions, and shows a styled floating dropdown anchored under the field. Arrow keys / Ctrl+E / Ctrl+D / → confirm an insert, which is written straight into the active `TextEditor` and re-parsed through Amplitude's own `RpnTextCompiler` so its Compile step picks it up.
  - **A secondary "Formula (autocomplete)" foldout** — an independent `GUILayout.TextField` backed by its own `RpnTextCompiler` instance (seeded by decompiling the current RPN), with the same completion popup, an "Apply to RPN" button (only enabled once Parse+Validate succeed) and "Reload from RPN". Because it only ever writes to the serialized arrays after `Parse()==Ok && Validate()==Ok`, it's impossible to write invalid RPN through it.
  - **"In-game render" HelpBox** — resolves the row through `DescriptorMapperPreview.ResolvePropertyEffect` (names through `PropertyMapper`, constant formatted signed/percent) and shows any warnings (hidden row, `SolveRPNFormula` live-evaluated, malformed RPN, missing mapper). Cached per-element and rebuilt only when `DescriptorMapperPreview.Generation` bumps or 1s elapses. Suppressed entirely when `DescriptorMapperPreview.IsActive` is off.
  - A `SuperPriority` `FormulaSuggestionOverlayBlockerDrawer` eats pointer events under the open suggestion popup so clicks land on the popup, not the fields beneath it.
- **Without Odin** (`[CustomPropertyDrawer(typeof(PropertyEffect))]` fallback): a plain `PropertyDrawer` with the same "in-game render" HelpBox, a Target Property field with a ▼ button opening a searchable `PropertyNameDropdown` of all known `PropertyMapper` names (via `DescriptorMapperPreview.GetAvailablePropertyNames`), and an editable "Property Local Name" array foldout with +/- row controls.
- **FormulaSuggestionDropdownGui** / **FormulaSuggestionOverlaySession** — shared, Odin-inspector-safe infrastructure for painting/scrolling/picking the suggestion popup and blocking underlying controls from stealing its clicks; used by both the Amplitude-field overlay and the secondary autocomplete field.

## License

MIT License — see [LICENSE](LICENSE)
