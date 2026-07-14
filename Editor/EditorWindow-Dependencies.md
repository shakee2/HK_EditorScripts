# Editor Window Dependencies

This document maps each user-facing editor window (`EditorWindow` subclass or `[MenuItem]` entry that opens one) to its `.cs` file dependencies for export/packaging purposes.

---

## Existing Export Package (per `ExportModEditorScriptsPackage.cs`)

The existing `Tools/Export Mod Editor Scripts Package` already exports these 9 files together:
- `VanillaDatabaseMount.cs`
- `ArchiveTranslations.cs`
- `TechTreeData.cs`
- `TechTreeWindow.cs`
- `DatabaseBrowser.cs`
- `DescriptorPropertyIndex.cs`
- `AssetExplorer.cs`
- `DescriptorMapperPreview.cs`
- `PropertyEffectDrawer.cs`

This group is designed to work standalone — all mutual dependencies are included.

---

## Standalone Windows (no local `.cs` dependencies)

These windows have no dependencies on other `.cs` files in this project beyond the shared utility layer — they can be exported alone (with `VanillaDatabaseMount.cs` if needed):

| Window | Menu Path | Primary File | Notes |
|--------|-----------|------------|-------|
| **DatabaseBrowser** | `Tools/Database Browser` | `DatabaseBrowser.cs` | Uses `InspectorDiagnostics.cs` for badge (which needs `ArchiveTranslations.cs`, `DescriptorMapperPreview.cs`) |
| **ModBuildWindow** | `Tools/Build And Deploy Mod` | `ModBuildWindow.cs` | Uses `VanillaDatabaseMount.cs` for remount |
| **AssetExplorer** | `Tools/Asset Explorer` | `AssetExplorer.cs` | Uses `VanillaDatabaseMount.cs` for shared MercuryDatabases mount |
| **BundleContentProbe** | `Tools/Pawn Fragment/Debug/Inspect Built Mod Bundle` | `BundleContentProbe.cs` | Standalone diagnostic tool |
| **GuidLookup** | `Tools/Pawn Fragment/Debug/Find GUID In Vanilla Bundles` | `GuidLookup.cs` | Standalone diagnostic tool |

---

## UnitVisualWorkflow Dependencies

**Primary:** `UnitVisualWorkflow.cs`

**Local `.cs` dependencies:**
- `FbxPrepPipeline.cs` (FBX prep step) **uses no local deps**
- `BoneStructureMatcher.cs` (tier auto-detection) **uses no local deps**
- `PawnFragmentAuthor.cs` (fragment creation + GUID fix) **uses no local deps**
- `Tier1MeshBaker.cs` (`MeshViability`, `MeshCollectionBaker`) **uses no local deps**
- `ModelRequirementsChecker.cs` (`FragmentValidator`) **uses no local deps**
- `AnimationManagerContent.cs` (`AnimationContentBuilder`) **uses no local deps**
- `VanillaAssetResolver.cs` (skeleton lookup) **uses no local deps**
- `VanillaDatabaseMount.cs` (target resolution)
- `ArchiveTranslations.cs` (localization in target resolution)

**Group:** `UnitVisualWorkflow.cs` + `FbxPrepPipeline.cs`, `BoneStructureMatcher.cs`, `PawnFragmentAuthor.cs`, `Tier1MeshBaker.cs`, `ModelRequirementsChecker.cs`, `AnimationManagerContent.cs`, `VanillaAssetResolver.cs`

---

## CompatPatcher Dependencies

**Primary files:**
- `CompatPatcherWindow.cs` (main window)
- `CompatCompareWindow.cs` (side-by-side compare)

**Local `.cs` dependencies (all CompatPatcher folder):**
- `UnityYaml.cs` (YAML parsing)
- `ModReader.cs` (mod loading)
- `ConflictAnalyzer.cs` (diff analysis)
- `PatchBuilder.cs` (import staging)
- `Sidecar.cs` (decision persistence)
- `LoadOrderValidator.cs` (load-time validation)
- `VanillaDatabaseMount.cs` (shared vanilla mount)

**Group:** `CompatPatcherWindow.cs`, `CompatCompareWindow.cs`, `UnityYaml.cs`, `ModReader.cs`, `ConflictAnalyzer.cs`, `PatchBuilder.cs`, `Sidecar.cs`, `LoadOrderValidator.cs`

---

## TechTreeWindow Dependencies

**Primary:** `TechTreeWindow.cs`

**Local `.cs` dependencies:**
- `TechTreeData.cs` (menu items and data layer)
- `ArchiveTranslations.cs` (localization mount + override)
- `VanillaDatabaseMount.cs` (vanilla bundle mount)

**Note:** Already grouped in the existing `ExportModEditorScriptsPackage.cs`.

---

## Inspector Hooks (not separate windows)

These are `[InitializeOnLoad]` hooks that layer into every inspector, not separate windows. They still need their local dependencies:

| Hook Class | Files | Notes |
|------------|-------|-------|
| `DescriptorMapperPreview` | `DescriptorMapperPreview.cs`, `ArchiveTranslations.cs`, `VanillaDatabaseMount.cs` | Tooltip breakdown preview in descriptor inspectors |
| `PropertyEffectOdinDrawer` | `PropertyEffectDrawer.cs`, `DescriptorMapperPreview.cs` | Odin drawer on `PropertyEffect`: formula autocomplete + inline in-game render HelpBox |
| `InspectorDiagnostics` | `InspectorDiagnostics.cs`, `DescriptorMapperPreview.cs`, `ArchiveTranslations.cs`, `VanillaDatabaseMount.cs` | Diagnostics panel in datatable element inspectors |
| `DescriptorPropertyIndex` | `DescriptorPropertyIndex.cs`, `VanillaDatabaseMount.cs`, `UnityYaml.cs` | Has its own window at `Tools/Descriptor Property Browser` |

---

## Debug Tools (Static Classes, No Window)

These are static methods with `[MenuItem]` entries, not `EditorWindow` subclasses:

| Menu | File | Notes |
|------|------|-------|
| `Tools/Debug/Pawn Probe/*` | `Probing.cs` | Static class with menu items |
| `Tools/Debug/Formula Probe/*` | `FormulaProbe.cs` | Static class with menu items |
| `Tools/Debug/Tech Tree/Diagnose Mod Split` | `TechTreeData.cs` | Static menu item |
| `Tools/Debug/Tech Tree/Dump Data` | `TechTreeData.cs` | Static menu item |
| `Tools/Debug/Tech Tree/Force Re-mount Vanilla Database` | `VanillaDatabaseMount.cs` | Static menu item |
| `Tools/Debug/Tech Tree/Vanilla Mount` | `VanillaDatabaseMount.cs` | Static menu item |
| `Tools/Debug/Tech Tree/Find Bad NarrativeEventDefinition` | `NarrativeEventDiagnostic.cs`, `VanillaDatabaseMount.cs` | Static class with menu items |
| `Tools/Debug/Compat Patcher/Clear Vanilla Validation Cache` | `LoadOrderValidator.cs` | Static menu item |

---

## Quick Reference Groups for Export

1. **Existing Package Group (ExportModEditorScriptsPackage):** `VanillaDatabaseMount.cs`, `ArchiveTranslations.cs`, `TechTreeData.cs`, `TechTreeWindow.cs`, `DatabaseBrowser.cs`, `DescriptorPropertyIndex.cs`, `AssetExplorer.cs`, `DescriptorMapperPreview.cs`, `PropertyEffectDrawer.cs`
2. **CompatPatcher Group:** `CompatPatcherWindow.cs`, `CompatCompareWindow.cs`, `UnityYaml.cs`, `ModReader.cs`, `ConflictAnalyzer.cs`, `PatchBuilder.cs`, `Sidecar.cs`, `LoadOrderValidator.cs`
3. **UnitVisualWorkflow Group:** `UnitVisualWorkflow.cs`, `FbxPrepPipeline.cs`, `BoneStructureMatcher.cs`, `PawnFragmentAuthor.cs`, `Tier1MeshBaker.cs`, `ModelRequirementsChecker.cs`, `AnimationManagerContent.cs`, `VanillaAssetResolver.cs`
4. **Standalone utils:** `BundleContentProbe.cs`, `GuidLookup.cs`, `NarrativeEventDiagnostic.cs`, `ModBuildWindow.cs`