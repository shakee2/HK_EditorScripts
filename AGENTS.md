# AGENTS.md — HK_EditorScripts (Humankind modding editor tools UPM package)

Orientation for a fresh agent. This is a **router**: it points to the authoritative doc/source for
each thing and only inlines facts that are always true. When a section here and a linked doc disagree,
the linked doc wins — fix this file. Verify any file/line ref before relying on it; the code moves.

## What this is
- A standalone **UPM package** (`com.shakee.hk-editorscripts`, editor-only asmdef `HK.EditorScripts`,
  no runtime references) providing Unity Editor tools for Humankind modding: database editing, custom
  unit visuals, compatibility patching, and diagnostics.
- Consumed by the mod project **`…/HK_Re-Imagined`** as a local package via its `Packages/manifest.json`
  (that repo is where you playtest/validate; this repo just holds the tool source). See that repo's
  `AGENTS.md` for the broader modding context (decompiled game source location, Mod Tools editor DLLs,
  reset-gate gotchas, etc.) — don't duplicate that here.
- Everything lives flat in `Editor/` (plus a `CompatPatcher/` subfolder). No `.sln`/`.csproj` committed;
  validated by opening the consuming project in the Unity editor and letting it assemble the scripts.
- Two git repos are involved in most changes: this one (tool source) and `HK_Re-Imagined` (where the
  package is referenced/tested). Check status in both before committing.

## Where to start (docs, in `Docs/`)
| Doc | What it is |
|---|---|
| [README.md](README.md) | Full tool inventory: every window/class, its menu path, and a one-line description. **Check here first** for "what does X do". |
| [Editor/EditorWindow-Dependencies.md](Editor/EditorWindow-Dependencies.md) | Maps each editor window to its local `.cs` dependencies — needed before exporting/packaging a subset of tools (see `ExportModEditorScriptsPackage.cs`). Update when adding a file with cross-dependencies. |
| [Docs/CompatPatcher.md](Docs/CompatPatcher.md) | Design spec for the mod Compatibility Patcher. |
| [Docs/CompatPatcher-LoadValidations.md](Docs/CompatPatcher-LoadValidations.md) | Which load-time validations can reset a retail game (only ~15; the rest are DEBUG-only) — read before changing `LoadOrderValidator.cs`. |
| [Docs/CompatPatcher-Manual.md](Docs/CompatPatcher-Manual.md) | User manual for the Compatibility Patcher. |

## Layout (`Editor/`)
All menu items live under **`Tools/…`**. Grouped by area — see the README table for full descriptions:

- **Compatibility Patcher** (`CompatPatcher/` subfolder): `CompatPatcherWindow.cs` (main window),
  `CompatCompareWindow.cs`, `ModReader.cs`, `ConflictAnalyzer.cs`, `UnityYaml.cs`, `PatchBuilder.cs`,
  `Sidecar.cs`, `LoadOrderValidator.cs`, `DiffGui.cs`.
- **Custom unit visuals**: `UnitVisualWorkflow.cs` (wizard) + `PawnFragmentAuthor.cs`,
  `Tier1MeshBaker.cs`, `BoneStructureMatcher.cs`, `FbxPrepPipeline.cs`, `ModelRequirementsChecker.cs`,
  `AnimationManagerContent.cs`.
- **Database browsing/editing**: `DatabaseBrowser.cs`, `DescriptorPropertyIndex.cs`, `TechTreeWindow.cs`
  / `TechTreeData.cs`, `DescriptorMapperPreview.cs`, `PropertyEffectDrawer.cs`, `InspectorAnalysisPanel.cs`,
  `ArchiveTranslations.cs`.
- **Vanilla bundle infrastructure**: `VanillaDatabaseMount.cs`, `VanillaAssetResolver.cs`,
  `AssetExplorer.cs`, `GuidLookup.cs`, `BundleContentProbe.cs`.
- **Build/export**: `ModBuildWindow.cs`, `ExportModEditorScriptsPackage.cs`.
- **Debug/diagnostics**: `Probing.cs`, `FormulaProbe.cs`, `FormulaAutocompleteProbe.cs`,
  `NarrativeEventDiagnostic.cs`, `InspectorDiagnostics.cs`.

## Conventions & gotchas
- **No local `.cs` dependencies unless noted** — most tools are standalone; the ones that share
  infrastructure lean on `VanillaDatabaseMount.cs` (shared vanilla bundle mount) and
  `ArchiveTranslations.cs` (shared localization mount). Check
  `Editor/EditorWindow-Dependencies.md` before assuming a file is self-contained, and update it when
  you add a new cross-file dependency.
- **`ExportModEditorScriptsPackage.cs`** exports a fixed subset of files as a `.unitypackage` for
  sharing outside this repo. If you rename/move/add a dependency to one of the exported windows
  (currently: `VanillaDatabaseMount`, `ArchiveTranslations`, `TechTreeData`, `TechTreeWindow`,
  `DatabaseBrowser`, `DescriptorPropertyIndex`, `AssetExplorer`), update the file list there too.
- **Editor-only, no engine assumptions beyond Editor APIs** — the asmdef restricts to `includePlatforms:
  ["Editor"]` with no assembly references, so avoid adding runtime-only dependencies; anything reused
  from the game's decompiled types goes through Mercury's editor/runtime assemblies already available
  in the consuming project, not through this asmdef's references list.
- **Validate in the consuming project, not here** — this repo has no compile target of its own; after
  editing, open/reload `HK_Re-Imagined` (or whichever project references this package) to confirm the
  scripts compile and the window(s) behave correctly.
- **New untracked files still need README/dependency-doc entries** — `FormulaAutocompleteProbe.cs`,
  `InspectorAnalysisPanel.cs`, and `PropertyEffectDrawer.cs` exist in the working tree but may not yet
  be reflected in `README.md` / `Editor/EditorWindow-Dependencies.md`; reconcile those docs when you
  touch this area.

## Commit conventions
- Commit/PR only when asked; branch off `main` first. Co-author trailer per repo norm.
- Because a change here is usually paired with a change in `HK_Re-Imagined` (package bump/testing),
  double check which repo's changes you're actually committing before running `git commit`.
