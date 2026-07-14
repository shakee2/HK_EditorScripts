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
| [Docs/manual.md](Docs/manual.md) | User manual for the **export package** scripts (`ModEditorScripts.unitypackage`). |

## Layout (`Editor/`)
All menu items live under **`Tools/…`**. Grouped by area — see the README table for full descriptions:

- **Compatibility Patcher** (`CompatPatcher/` subfolder): `CompatPatcherWindow.cs` (main window),
  `CompatCompareWindow.cs`, `ModReader.cs`, `ConflictAnalyzer.cs`, `UnityYaml.cs`, `PatchBuilder.cs`,
  `Sidecar.cs`, `LoadOrderValidator.cs`, `DiffGui.cs`.
- **Custom unit visuals**: `UnitVisualWorkflow.cs` (wizard) + `PawnFragmentAuthor.cs`,
  `Tier1MeshBaker.cs`, `BoneStructureMatcher.cs`, `FbxPrepPipeline.cs`, `ModelRequirementsChecker.cs`,
  `AnimationManagerContent.cs`.
- **Database browsing/editing**: `DatabaseBrowser.cs`, `DescriptorPropertyIndex.cs`, `TechTreeWindow.cs`
  / `TechTreeData.cs`, `DescriptorMapperPreview.cs`, `DescriptorMapperGenerator.cs`,
  `PropertyEffectDrawer.cs`, `InspectorAnalysisPanel.cs`, `LocalizationKeyDrawer.cs`,
  `InlineLocalizationEditor.cs`, `ArchiveTranslations.cs`.
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
  sharing outside this repo (full list: [README.md § Export package](README.md#export-package-modeditorscriptsunitypackage)).
  If you add a dependency to an exported script, update `SCRIPT_NAMES` there and the README table.
- **Editor-only, no engine assumptions beyond Editor APIs** — the asmdef restricts to `includePlatforms:
  ["Editor"]` with no assembly references, so avoid adding runtime-only dependencies; anything reused
  from the game's decompiled types goes through Mercury's editor/runtime assemblies already available
  in the consuming project, not through this asmdef's references list.
- **Validate in the consuming project, not here** — this repo has no compile target of its own; after
  editing, open/reload `HK_Re-Imagined` (or whichever project references this package) to confirm the
  scripts compile and the window(s) behave correctly.
- **Inspector IMGUI: avoid Repaint-driven work** — hooks like `Editor.finishedDefaultHeaderGUI` and
  Odin drawers run on *every* inspector GUI pass (Layout **and** Repaint, including mouse-move). Calling
  expensive logic there (`AssetDatabase.FindAssets`, `BuildKeyToTextDict`, full validation passes,
  reflection walks) on each pass will tank the whole editor, not just the inspector. Pattern used here:
  - **Rebuild only when something actually changed** — selection change (`Selection.selectionChanged`),
    a button the user clicked (e.g. Generate), `EditorApplication.projectChanged`, `Undo.undoRedoPerformed`,
    or an explicit cache-bump after an edit.
  - **On Repaint, read cache only** — replay prebuilt draw ops (`DescriptorMapperPreview`), return a
    memoized status (`DescriptorMapperGenerator.EnsureStatus`), or reuse stale results until the next
    allowed rebuild (often Layout-only for graph builds, or generation-keyed caches elsewhere).
  - **Do not use TTL polling as a substitute** — if state is stable until selection/assets change, a
    timer just hides the bug. Ask "what event invalidates this?" before adding per-frame checks.
- **Inline `%key` localization on mapper fields** — `LocalizationKeyStringDrawer` + `InlineLocalizationEditor`
  draw Import/edit translation boxes directly under `%key` string fields in the inspector (no Mod Editor
  Localization Window). Applies to **UIMapper** subclasses (**Title**, **Description**, facet titles — the
  UI labels) and **DescriptorMapper** (**LocalizedName**, **EffectLocalization**, …). Same helpers power
  the aggregate Localization foldout in `InspectorDiagnostics`. Full behaviour: [README.md § Inline
  Localization Editing](README.md#inline-localization-editing). **UIMapper auto-generation** is still
  out of scope (`DescriptorMapperGenerator` only pairs Descriptor → DescriptorMapper).
- **New untracked files still need README/dependency-doc entries** — reconcile `README.md` and
  `Editor/EditorWindow-Dependencies.md` when adding inspector hooks or cross-file dependencies.

## Commit conventions
- Commit/PR only when asked; branch off `main` first. Co-author trailer per repo norm.
- Because a change here is usually paired with a change in `HK_Re-Imagined` (package bump/testing),
  double check which repo's changes you're actually committing before running `git commit`.
