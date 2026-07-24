# Editor Window Dependencies

This document maps each user-facing editor window (`EditorWindow` subclass or `[MenuItem]` entry that opens one) to its `.cs` file dependencies for export/packaging purposes.

## Package layout

As of v1.1.0, files are split across four independent UPM packages under `Packages/`, each with its own `Editor/` folder:

- **`Packages/com.hk.modtools.shared/Editor/`** — foundation mounts nearly everything else depends on (`VanillaDatabaseMount.cs`, `ArchiveTranslations.cs`), floating-window minimize (`WindowMinimize.cs`), plus cross-package infra (`UpdateChecker.cs`, `ToolsOptionsWindow.cs`, `ChangelogPopupWindow.cs`). No dependencies of its own.
- **`Packages/com.hk.modtools.core/Editor/`** — depends on `shared`. Subfolders by role:
  - `ModTools/` — standalone browsing/editing windows (database browser, tech tree, unit family lines, asset explorer, build window).
  - `Upgrades/` — hooks that augment vanilla ModTools inspectors with new capabilities (inline localization, tooltip preview, diagnostics, etc.) rather than opening their own window.
  - `Debug/` — standalone diagnostic probes with `[MenuItem]` entries, no window.
  - `Editor/` (root) — general package infrastructure not scoped to one domain (`ExportModEditorScriptsPackage.cs`).
- **`Packages/com.hk.modtools.compatpatcher/Editor/CompatPatcher/`** (pre-release `0.x`) — the Compatibility Patcher; depends on `shared` only.
- **`Packages/com.hk.modtools.unitvisuals/Editor/UnitVisualWorkflow/`** (experimental, no tag yet) — the custom-unit-visual pipeline; depends on `shared` only.

Paths below are given relative to each file's own package's `Editor/` folder (e.g. "`ModTools/DatabaseBrowser.cs`" means `Packages/com.hk.modtools.core/Editor/ModTools/DatabaseBrowser.cs`) unless a cross-package dependency is called out explicitly.

---

## Existing Export Package (per `ExportModEditorScriptsPackage.cs`, in `com.hk.modtools.core`)

The existing `Tools/shakee's Tools/Export Mod Editor Scripts Package` exports these **14 scripts** plus **`Docs/manual.md`** (from `com.hk.modtools.core`):
- `com.hk.modtools.shared: VanillaDatabaseMount.cs`
- `com.hk.modtools.shared: ArchiveTranslations.cs`
- `com.hk.modtools.shared: WindowMinimize.cs`
- `ModTools/TechTreeData.cs`
- `ModTools/TechTreeWindow.cs`
- `ModTools/DatabaseBrowser.cs`
- `ModTools/DescriptorPropertyIndex.cs`
- `Upgrades/DescriptorMapperPreview.cs`
- `Upgrades/UIPictoTint.shader`
- `Upgrades/PropertyEffectDrawer.cs`
- `Upgrades/InspectorAnalysisPanel.cs`
- `Upgrades/InspectorDiagnostics.cs`
- `Upgrades/DescriptorMapperGenerator.cs`
- `Upgrades/LocalizationKeyDrawer.cs`
- `Upgrades/InlineLocalizationEditor.cs`

This group is designed to work standalone — all mutual dependencies are included. (`ModTools/AssetExplorer.cs` is **not** part of this export set, despite living in the same package/folder — see the standalone list below.)

---

## Standalone Windows (no local `.cs` dependencies beyond `shared`)

These windows have no dependencies on other `.cs` files beyond `com.hk.modtools.shared` — they can be exported alone (with `shared`'s `VanillaDatabaseMount.cs` if needed):

| Window | Menu Path | Primary File | Notes |
|--------|-----------|------------|-------|
| **DatabaseBrowser** | `Tools/shakee's Tools/Database Browser` | `ModTools/DatabaseBrowser.cs` (`core`) | Uses `Upgrades/InspectorDiagnostics.cs` (`core`) for badge (which needs `shared: ArchiveTranslations.cs`, `Upgrades/DescriptorMapperPreview.cs`); `shared: WindowMinimize.cs` in Window mode |
| **ModBuildWindow** | `Tools/shakee's Tools/Build And Deploy Mod` | `ModTools/ModBuildWindow.cs` (`core`) | Uses `shared: VanillaDatabaseMount.cs` for remount |
| **AssetExplorer** | `Tools/shakee's Tools/Asset Explorer` | `ModTools/AssetExplorer.cs` (`core`) | Uses `shared: VanillaDatabaseMount.cs` for shared MercuryDatabases mount |
| **UnitFamilyLinesWindow** | `Tools/shakee's Tools/Unit Family Lines` | `ModTools/UnitFamilyLinesWindow.cs` (`core`) | Uses `ModTools/UnitFamilyLinesData.cs`; `shared: VanillaDatabaseMount.cs`, `WindowMinimize.cs` — not in export package |
| **BundleContentProbe** | `Tools/shakee's Tools/Debug/Inspect Built Mod Bundle` | `Debug/BundleContentProbe.cs` (`core`) | Standalone diagnostic tool |
| **GuidLookup** | `Tools/shakee's Tools/Debug/Find GUID In Vanilla Bundles` | `Debug/GuidLookup.cs` (`core`) | Standalone diagnostic tool |

---

## UnitVisualWorkflow Dependencies (`com.hk.modtools.unitvisuals`)

**Primary:** `UnitVisualWorkflow/UnitVisualWorkflow.cs`

**Local `.cs` dependencies (all `UnitVisualWorkflow/` folder, except the shared mounts):**
- `FbxPrepPipeline.cs` (FBX prep step) **uses no local deps**
- `BoneStructureMatcher.cs` (tier auto-detection) **uses no local deps**
- `PawnFragmentAuthor.cs` (fragment creation + GUID fix) **uses no local deps**
- `Tier1MeshBaker.cs` (`MeshViability`, `MeshCollectionBaker`) **uses no local deps**
- `ModelRequirementsChecker.cs` (`FragmentValidator`) **uses no local deps**
- `AnimationManagerContent.cs` (`AnimationContentBuilder`) **uses no local deps**
- `VanillaAssetResolver.cs` (skeleton lookup) **uses no local deps**
- `com.hk.modtools.shared: VanillaDatabaseMount.cs` (target resolution)
- `com.hk.modtools.shared: ArchiveTranslations.cs` (localization in target resolution)

**Group:** `UnitVisualWorkflow.cs` + `FbxPrepPipeline.cs`, `BoneStructureMatcher.cs`, `PawnFragmentAuthor.cs`, `Tier1MeshBaker.cs`, `ModelRequirementsChecker.cs`, `AnimationManagerContent.cs`, `VanillaAssetResolver.cs` (all under `com.hk.modtools.unitvisuals`'s `Editor/UnitVisualWorkflow/`)

**Confirmed fully decoupled from `com.hk.modtools.core`** — nothing outside this package references any of its types, and it references nothing in `core` — only `com.hk.modtools.shared`, declared as a real `package.json` dependency.

---

## CompatPatcher Dependencies (`com.hk.modtools.compatpatcher`)

**Primary files (all under `com.hk.modtools.compatpatcher`'s `Editor/CompatPatcher/`):**
- `CompatPatcherWindow.cs` / `CompatPatcherWorkspaceGui.cs` (main window + type/pattern workspace)
- `CompatPatcherManualWindow.cs` (user manual utility window)
- `CompatCompareWindow.cs` (side-by-side compare)

**Local `.cs` dependencies (all CompatPatcher folder):**
- `CompatBundleMounts.cs` (session assetbundle mounts)
- `LiveElementBuilder.cs` (in-memory HkElement from live SO)
- `ReflectionBodyMerge.cs` (Odin live fields → Body when SerializedObject is empty)
- `SimulationEventEffectFlattener.cs` (Odin SimulationEventEffect → Body/Flat)
- `AdvancedDropdownHeight.cs` (cap type-filter popup height)
- `UnityYaml.cs` (YAML parsing)
- `ModReader.cs` (mod loading)
- `ConflictAnalyzer.cs` (diff analysis)
- `PatchBuilder.cs` (import staging)
- `FieldApplier.cs` (SerializedObject field ADD/PICK for Mass Change)
- `MassChange.cs` (candidate grouping + Patch-only apply orchestration)
- `MassFieldChangeWindow.cs` (Mass Change UI)
- `Sidecar.cs` (decision persistence)
- `LoadOrderValidator.cs` (load-time validation)
- `DiffGui.cs` (shared IMGUI diff list)
- `com.hk.modtools.shared: VanillaDatabaseMount.cs` (shared vanilla mount)
- `com.hk.modtools.shared: WindowMinimize.cs` (floating minimize; Patcher + Compare paired)

**Group:** `CompatPatcherWindow.cs`, `CompatPatcherWorkspaceGui.cs`, `CompatPatcherManualWindow.cs`, `CompatCompareWindow.cs`, `CompatBundleMounts.cs`, `LiveElementBuilder.cs`, `ReflectionBodyMerge.cs`, `SimulationEventEffectFlattener.cs`, `AdvancedDropdownHeight.cs`, `UnityYaml.cs`, `ModReader.cs`, `ConflictAnalyzer.cs`, `PatchBuilder.cs`, `FieldApplier.cs`, `MassChange.cs`, `MassFieldChangeWindow.cs`, `Sidecar.cs`, `LoadOrderValidator.cs`, `DiffGui.cs` (all under `com.hk.modtools.compatpatcher`'s `Editor/CompatPatcher/`)

**Confirmed fully decoupled from `com.hk.modtools.core`** — comments in this folder mention `DatabaseBrowser`/`DescriptorPropertyIndex` for readers' orientation only; no actual code reference exists. Only `com.hk.modtools.shared` is a real dependency.

---

## TechTreeWindow Dependencies (`com.hk.modtools.core`)

**Primary:** `ModTools/TechTreeWindow.cs`

**Local `.cs` dependencies:**
- `ModTools/TechTreeData.cs` (menu items and data layer)
- `com.hk.modtools.shared: ArchiveTranslations.cs` (localization mount + override)
- `com.hk.modtools.shared: VanillaDatabaseMount.cs` (vanilla bundle mount)
- `com.hk.modtools.shared: WindowMinimize.cs` (floating minimize strip)

**Note:** Already grouped in the existing `ExportModEditorScriptsPackage.cs`.

---

## UnitFamilyLinesWindow Dependencies (`com.hk.modtools.core`)

**Primary:** `ModTools/UnitFamilyLinesWindow.cs`

**Local `.cs` dependencies:**
- `ModTools/UnitFamilyLinesData.cs` (menu dump + data/layout layer)
- `com.hk.modtools.shared: VanillaDatabaseMount.cs` (vanilla bundle mount)
- `com.hk.modtools.shared: WindowMinimize.cs` (floating minimize strip)

**Note:** Not part of `ExportModEditorScriptsPackage` (same as Asset Explorer — package-local until asked).

---

## DatabaseBrowser / DescriptorPropertyIndex (`com.hk.modtools.core`)

Both use `com.hk.modtools.shared: WindowMinimize.cs` for floating-window minimize (Database Browser: **Window** mode only). Descriptor Property Index also uses `shared: VanillaDatabaseMount.cs` (see Inspector Hooks table below).

---

## Inspector Hooks (not separate windows, all in `com.hk.modtools.core`)

These are `[InitializeOnLoad]` hooks that layer into every inspector, not separate windows. All live under `Editor/Upgrades/` (except the shared mounts). They still need their local dependencies:

| Hook Class | Files | Notes |
|------------|-------|-------|
| `DescriptorMapperPreview` | `Upgrades/DescriptorMapperPreview.cs`, `Upgrades/UIPictoTint.shader`, `shared: ArchiveTranslations.cs`, `shared: VanillaDatabaseMount.cs` | Tooltip breakdown preview in descriptor inspectors |
| `DescriptorMapperGenerator` | `Upgrades/DescriptorMapperGenerator.cs`, `Upgrades/DescriptorMapperPreview.cs`, `shared: VanillaDatabaseMount.cs` | Generate/select paired DescriptorMapper from Descriptor inspector header |
| `LocalizationKeyStringDrawer` | `Upgrades/LocalizationKeyDrawer.cs`, `Upgrades/InlineLocalizationEditor.cs`, `shared: ArchiveTranslations.cs`, `Upgrades/DescriptorMapperPreview.cs` | Inline translation editor below `%key` fields on UIMapper/DescriptorMapper |
| `PropertyEffectOdinDrawer` | `Upgrades/PropertyEffectDrawer.cs`, `Upgrades/DescriptorMapperPreview.cs` | Odin drawer on `PropertyEffect`: formula autocomplete + inline Rendered HelpBox |
| `InspectorDiagnostics` | `Upgrades/InspectorDiagnostics.cs`, `Upgrades/DescriptorMapperPreview.cs`, `Upgrades/InlineLocalizationEditor.cs`, `shared: ArchiveTranslations.cs`, `shared: VanillaDatabaseMount.cs` | Diagnostics panel in datatable element inspectors |
| `DescriptorPropertyIndex` | `ModTools/DescriptorPropertyIndex.cs`, `shared: VanillaDatabaseMount.cs`, `shared: WindowMinimize.cs` | Has its own window at `Tools/shakee's Tools/Descriptor Property Browser` (lives in `ModTools/`, not `Upgrades/`, since it's a standalone window). Does **not** depend on `CompatPatcher/UnityYaml.cs` — that's in a different package entirely now, and never was a real dependency |

---

## Debug Tools (Static Classes, No Window)

These are static methods with `[MenuItem]` entries, not `EditorWindow` subclasses. All live under `com.hk.modtools.core`'s `Editor/Debug/` except the tech-tree/vanilla-mount items, which stay with their owning file:

| Menu | File | Notes |
|------|------|-------|
| `Tools/shakee's Tools/Debug/Pawn Probe/*` | `Debug/Probing.cs` (`core`) | Static class with menu items |
| `Tools/shakee's Tools/Debug/Probes/*` | `Debug/FormulaProbe.cs`, `Debug/FormulaAutocompleteProbe.cs` (`core`) | Static classes with menu items |
| `Tools/shakee's Tools/Debug/Tech Tree/Diagnose Mod Split` | `ModTools/TechTreeData.cs` (`core`) | Static menu item |
| `Tools/shakee's Tools/Debug/Tech Tree/Dump Data` | `ModTools/TechTreeData.cs` (`core`) | Static menu item |
| `Tools/shakee's Tools/Debug/Unit Family Lines/Dump Data` | `ModTools/UnitFamilyLinesData.cs` (`core`) | Static menu item |
| `Tools/shakee's Tools/Debug/Tech Tree/Force Re-mount Vanilla Database` | `VanillaDatabaseMount.cs` (`shared`) | Static menu item |
| `Tools/shakee's Tools/Debug/Tech Tree/Vanilla Mount` | `VanillaDatabaseMount.cs` (`shared`) | Static menu item |
| `Tools/shakee's Tools/Debug/Find Bad NarrativeEventDefinition` | `Debug/NarrativeEventDiagnostic.cs` (`core`), `VanillaDatabaseMount.cs` (`shared`) | Static class with menu items |
| `Tools/shakee's Tools/Debug/Compat Patcher/Clear Vanilla Validation Cache` | `CompatPatcher/LoadOrderValidator.cs` (`compatpatcher`) | Static menu item |
| `Tools/shakee's Tools/Check For Updates` | `UpdateChecker.cs` (`shared`) | Static menu item |
| `Tools/shakee's Tools/Options` | `ToolsOptionsWindow.cs` (`shared`) | Package Install/Update/Remove catalog + settings; opens `ChangelogPopupWindow` for What's new |
| *(utility)* What's new | `ChangelogPopupWindow.cs` (`shared`) | Per-version foldout cards; opened from Options when an update is available |

---

## Quick Reference Groups for Export

1. **Existing Package Group (`ExportModEditorScriptsPackage`, spans `shared` + `core`):** `shared: VanillaDatabaseMount.cs`, `shared: ArchiveTranslations.cs`, `core: ModTools/TechTreeData.cs`, `core: ModTools/TechTreeWindow.cs`, `core: ModTools/DatabaseBrowser.cs`, `core: ModTools/DescriptorPropertyIndex.cs`, `core: Upgrades/DescriptorMapperPreview.cs`, `core: Upgrades/UIPictoTint.shader`, `core: Upgrades/PropertyEffectDrawer.cs`, `core: Upgrades/InspectorAnalysisPanel.cs`, `core: Upgrades/InspectorDiagnostics.cs`, `core: Upgrades/DescriptorMapperGenerator.cs`, `core: Upgrades/LocalizationKeyDrawer.cs`, `core: Upgrades/InlineLocalizationEditor.cs`
2. **CompatPatcher package (`com.hk.modtools.compatpatcher`):** `CompatPatcher/CompatPatcherWindow.cs`, `CompatPatcher/CompatPatcherWorkspaceGui.cs`, `CompatPatcher/CompatPatcherManualWindow.cs`, `CompatPatcher/CompatCompareWindow.cs`, `CompatPatcher/CompatBundleMounts.cs`, `CompatPatcher/LiveElementBuilder.cs`, `CompatPatcher/ReflectionBodyMerge.cs`, `CompatPatcher/SimulationEventEffectFlattener.cs`, `CompatPatcher/UnityYaml.cs`, `CompatPatcher/ModReader.cs`, `CompatPatcher/ConflictAnalyzer.cs`, `CompatPatcher/PatchBuilder.cs`, `CompatPatcher/FieldApplier.cs`, `CompatPatcher/MassChange.cs`, `CompatPatcher/MassFieldChangeWindow.cs`, `CompatPatcher/Sidecar.cs`, `CompatPatcher/LoadOrderValidator.cs`, `CompatPatcher/DiffGui.cs`
3. **UnitVisualWorkflow package (`com.hk.modtools.unitvisuals`):** `UnitVisualWorkflow/UnitVisualWorkflow.cs`, `UnitVisualWorkflow/FbxPrepPipeline.cs`, `UnitVisualWorkflow/BoneStructureMatcher.cs`, `UnitVisualWorkflow/PawnFragmentAuthor.cs`, `UnitVisualWorkflow/Tier1MeshBaker.cs`, `UnitVisualWorkflow/ModelRequirementsChecker.cs`, `UnitVisualWorkflow/AnimationManagerContent.cs`, `UnitVisualWorkflow/VanillaAssetResolver.cs`
4. **Standalone utils (`com.hk.modtools.core`, not in the export package):** `Debug/BundleContentProbe.cs`, `Debug/GuidLookup.cs`, `Debug/NarrativeEventDiagnostic.cs`, `ModTools/ModBuildWindow.cs`, `ModTools/AssetExplorer.cs`
