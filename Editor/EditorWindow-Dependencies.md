# Editor Window Dependencies

This document maps each user-facing editor window (`EditorWindow` subclass or `[MenuItem]` entry that opens one) to its `.cs` file dependencies for export/packaging purposes.

## Folder layout

Files are grouped into subfolders under `Editor/` by role:

- **`Shared/`** — foundation mounts nearly everything else depends on (`VanillaDatabaseMount.cs`, `ArchiveTranslations.cs`).
- **`Upgrades/`** — hooks that augment vanilla ModTools inspectors with new capabilities (inline localization, tooltip preview, diagnostics, etc.) rather than opening their own window.
- **`ModTools/`** — standalone browsing/editing windows (database browser, tech tree, asset explorer, build window).
- **`Debug/`** — standalone diagnostic probes with `[MenuItem]` entries, no window.
- **`UnitVisualWorkflow/`** — experimental custom-unit-visual pipeline; kept isolated so it can be excluded wholesale from release branches/tags.
- **`CompatPatcher/`** — the Compatibility Patcher, also isolated so it can be excluded until stable.
- **`Editor/` (root)** — general package infrastructure not scoped to one domain (`ExportModEditorScriptsPackage.cs`; a future git-tag update checker would live here too).

---

## Existing Export Package (per `ExportModEditorScriptsPackage.cs`)

The existing `Tools/Export Mod Editor Scripts Package` exports these **14 scripts** plus **`Docs/manual.md`**:
- `Shared/VanillaDatabaseMount.cs`
- `Shared/ArchiveTranslations.cs`
- `ModTools/TechTreeData.cs`
- `ModTools/TechTreeWindow.cs`
- `ModTools/DatabaseBrowser.cs`
- `ModTools/DescriptorPropertyIndex.cs`
- `ModTools/AssetExplorer.cs`
- `Upgrades/DescriptorMapperPreview.cs`
- `Upgrades/PropertyEffectDrawer.cs`
- `Upgrades/InspectorAnalysisPanel.cs`
- `Upgrades/InspectorDiagnostics.cs`
- `Upgrades/DescriptorMapperGenerator.cs`
- `Upgrades/LocalizationKeyDrawer.cs`
- `Upgrades/InlineLocalizationEditor.cs`

This group is designed to work standalone — all mutual dependencies are included.

---

## Standalone Windows (no local `.cs` dependencies)

These windows have no dependencies on other `.cs` files in this project beyond the shared utility layer — they can be exported alone (with `Shared/VanillaDatabaseMount.cs` if needed):

| Window | Menu Path | Primary File | Notes |
|--------|-----------|------------|-------|
| **DatabaseBrowser** | `Tools/Database Browser` | `ModTools/DatabaseBrowser.cs` | Uses `Upgrades/InspectorDiagnostics.cs` for badge (which needs `Shared/ArchiveTranslations.cs`, `Upgrades/DescriptorMapperPreview.cs`) |
| **ModBuildWindow** | `Tools/Build And Deploy Mod` | `ModTools/ModBuildWindow.cs` | Uses `Shared/VanillaDatabaseMount.cs` for remount |
| **AssetExplorer** | `Tools/Asset Explorer` | `ModTools/AssetExplorer.cs` | Uses `Shared/VanillaDatabaseMount.cs` for shared MercuryDatabases mount |
| **BundleContentProbe** | `Tools/Pawn Fragment/Debug/Inspect Built Mod Bundle` | `Debug/BundleContentProbe.cs` | Standalone diagnostic tool |
| **GuidLookup** | `Tools/Pawn Fragment/Debug/Find GUID In Vanilla Bundles` | `Debug/GuidLookup.cs` | Standalone diagnostic tool |

---

## UnitVisualWorkflow Dependencies

**Primary:** `UnitVisualWorkflow/UnitVisualWorkflow.cs`

**Local `.cs` dependencies (all `UnitVisualWorkflow/` folder, except the shared mounts):**
- `FbxPrepPipeline.cs` (FBX prep step) **uses no local deps**
- `BoneStructureMatcher.cs` (tier auto-detection) **uses no local deps**
- `PawnFragmentAuthor.cs` (fragment creation + GUID fix) **uses no local deps**
- `Tier1MeshBaker.cs` (`MeshViability`, `MeshCollectionBaker`) **uses no local deps**
- `ModelRequirementsChecker.cs` (`FragmentValidator`) **uses no local deps**
- `AnimationManagerContent.cs` (`AnimationContentBuilder`) **uses no local deps**
- `VanillaAssetResolver.cs` (skeleton lookup) **uses no local deps**
- `Shared/VanillaDatabaseMount.cs` (target resolution)
- `Shared/ArchiveTranslations.cs` (localization in target resolution)

**Group:** `UnitVisualWorkflow.cs` + `FbxPrepPipeline.cs`, `BoneStructureMatcher.cs`, `PawnFragmentAuthor.cs`, `Tier1MeshBaker.cs`, `ModelRequirementsChecker.cs`, `AnimationManagerContent.cs`, `VanillaAssetResolver.cs` (all under `Editor/UnitVisualWorkflow/`)

**Confirmed fully decoupled** — nothing outside this folder references any of its types, so it can be excluded wholesale (e.g. `git rm -r Editor/UnitVisualWorkflow`) from a release branch without breaking anything else.

---

## CompatPatcher Dependencies

**Primary files (all under `Editor/CompatPatcher/`):**
- `CompatPatcherWindow.cs` (main window)
- `CompatCompareWindow.cs` (side-by-side compare)

**Local `.cs` dependencies (all CompatPatcher folder):**
- `UnityYaml.cs` (YAML parsing)
- `ModReader.cs` (mod loading)
- `ConflictAnalyzer.cs` (diff analysis)
- `PatchBuilder.cs` (import staging)
- `Sidecar.cs` (decision persistence)
- `LoadOrderValidator.cs` (load-time validation)
- `Shared/VanillaDatabaseMount.cs` (shared vanilla mount)

**Group:** `CompatPatcherWindow.cs`, `CompatCompareWindow.cs`, `UnityYaml.cs`, `ModReader.cs`, `ConflictAnalyzer.cs`, `PatchBuilder.cs`, `Sidecar.cs`, `LoadOrderValidator.cs` (all under `Editor/CompatPatcher/`)

---

## TechTreeWindow Dependencies

**Primary:** `ModTools/TechTreeWindow.cs`

**Local `.cs` dependencies:**
- `ModTools/TechTreeData.cs` (menu items and data layer)
- `Shared/ArchiveTranslations.cs` (localization mount + override)
- `Shared/VanillaDatabaseMount.cs` (vanilla bundle mount)

**Note:** Already grouped in the existing `ExportModEditorScriptsPackage.cs`.

---

## Inspector Hooks (not separate windows)

These are `[InitializeOnLoad]` hooks that layer into every inspector, not separate windows. All live under `Editor/Upgrades/` (except the shared mounts). They still need their local dependencies:

| Hook Class | Files | Notes |
|------------|-------|-------|
| `DescriptorMapperPreview` | `Upgrades/DescriptorMapperPreview.cs`, `Shared/ArchiveTranslations.cs`, `Shared/VanillaDatabaseMount.cs` | Tooltip breakdown preview in descriptor inspectors |
| `DescriptorMapperGenerator` | `Upgrades/DescriptorMapperGenerator.cs`, `Upgrades/DescriptorMapperPreview.cs`, `Shared/VanillaDatabaseMount.cs` | Generate/select paired DescriptorMapper from Descriptor inspector header |
| `LocalizationKeyStringDrawer` | `Upgrades/LocalizationKeyDrawer.cs`, `Upgrades/InlineLocalizationEditor.cs`, `Shared/ArchiveTranslations.cs`, `Upgrades/DescriptorMapperPreview.cs` | Inline translation editor below `%key` fields on UIMapper/DescriptorMapper |
| `PropertyEffectOdinDrawer` | `Upgrades/PropertyEffectDrawer.cs`, `Upgrades/DescriptorMapperPreview.cs` | Odin drawer on `PropertyEffect`: formula autocomplete + inline in-game render HelpBox |
| `InspectorDiagnostics` | `Upgrades/InspectorDiagnostics.cs`, `Upgrades/DescriptorMapperPreview.cs`, `Upgrades/InlineLocalizationEditor.cs`, `Shared/ArchiveTranslations.cs`, `Shared/VanillaDatabaseMount.cs` | Diagnostics panel in datatable element inspectors |
| `DescriptorPropertyIndex` | `ModTools/DescriptorPropertyIndex.cs`, `Shared/VanillaDatabaseMount.cs` | Has its own window at `Tools/Descriptor Property Browser` (lives in `ModTools/`, not `Upgrades/`, since it's a standalone window). Does **not** depend on `CompatPatcher/UnityYaml.cs` (stale claim removed — verified via grep, `UnityYaml` is only referenced within `CompatPatcher/` itself) |

---

## Debug Tools (Static Classes, No Window)

These are static methods with `[MenuItem]` entries, not `EditorWindow` subclasses. All live under `Editor/Debug/` except the tech-tree/vanilla-mount items, which stay with their owning file:

| Menu | File | Notes |
|------|------|-------|
| `Tools/Debug/Pawn Probe/*` | `Debug/Probing.cs` | Static class with menu items |
| `Tools/Debug/Formula Probe/*` | `Debug/FormulaProbe.cs` | Static class with menu items |
| `Tools/Debug/Tech Tree/Diagnose Mod Split` | `ModTools/TechTreeData.cs` | Static menu item |
| `Tools/Debug/Tech Tree/Dump Data` | `ModTools/TechTreeData.cs` | Static menu item |
| `Tools/Debug/Tech Tree/Force Re-mount Vanilla Database` | `Shared/VanillaDatabaseMount.cs` | Static menu item |
| `Tools/Debug/Tech Tree/Vanilla Mount` | `Shared/VanillaDatabaseMount.cs` | Static menu item |
| `Tools/Debug/Tech Tree/Find Bad NarrativeEventDefinition` | `Debug/NarrativeEventDiagnostic.cs`, `Shared/VanillaDatabaseMount.cs` | Static class with menu items |
| `Tools/Debug/Compat Patcher/Clear Vanilla Validation Cache` | `CompatPatcher/LoadOrderValidator.cs` | Static menu item |

---

## Quick Reference Groups for Export

1. **Existing Package Group (ExportModEditorScriptsPackage):** `Shared/VanillaDatabaseMount.cs`, `Shared/ArchiveTranslations.cs`, `ModTools/TechTreeData.cs`, `ModTools/TechTreeWindow.cs`, `ModTools/DatabaseBrowser.cs`, `ModTools/DescriptorPropertyIndex.cs`, `ModTools/AssetExplorer.cs`, `Upgrades/DescriptorMapperPreview.cs`, `Upgrades/PropertyEffectDrawer.cs`, `Upgrades/InspectorAnalysisPanel.cs`, `Upgrades/InspectorDiagnostics.cs`, `Upgrades/DescriptorMapperGenerator.cs`, `Upgrades/LocalizationKeyDrawer.cs`, `Upgrades/InlineLocalizationEditor.cs`
2. **CompatPatcher Group:** `CompatPatcher/CompatPatcherWindow.cs`, `CompatPatcher/CompatCompareWindow.cs`, `CompatPatcher/UnityYaml.cs`, `CompatPatcher/ModReader.cs`, `CompatPatcher/ConflictAnalyzer.cs`, `CompatPatcher/PatchBuilder.cs`, `CompatPatcher/Sidecar.cs`, `CompatPatcher/LoadOrderValidator.cs`
3. **UnitVisualWorkflow Group:** `UnitVisualWorkflow/UnitVisualWorkflow.cs`, `UnitVisualWorkflow/FbxPrepPipeline.cs`, `UnitVisualWorkflow/BoneStructureMatcher.cs`, `UnitVisualWorkflow/PawnFragmentAuthor.cs`, `UnitVisualWorkflow/Tier1MeshBaker.cs`, `UnitVisualWorkflow/ModelRequirementsChecker.cs`, `UnitVisualWorkflow/AnimationManagerContent.cs`, `UnitVisualWorkflow/VanillaAssetResolver.cs`
4. **Standalone utils:** `Debug/BundleContentProbe.cs`, `Debug/GuidLookup.cs`, `Debug/NarrativeEventDiagnostic.cs`, `ModTools/ModBuildWindow.cs`
