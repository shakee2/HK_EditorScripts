# AGENTS.md — HK_EditorScripts (Humankind modding editor tools UPM package)

Orientation for a fresh agent. This is a **router**: it points to the authoritative doc/source for
each thing and only inlines facts that are always true. When a section here and a linked doc disagree,
the linked doc wins — fix this file. Verify any file/line ref before relying on it; the code moves.

## What this is
- A **monorepo of UPM packages** under `Packages/` (editor-only asmdefs, no runtime references)
  providing Unity Editor tools for Humankind modding: `com.hk.modtools.shared` (foundation mounts —
  everything else depends on it), `com.hk.modtools.core` (the main toolset), `com.hk.modtools.compatpatcher`,
  and `com.hk.modtools.unitvisuals`. Consumers install each via the git-URL subfolder form
  (`…HK_EditorScripts.git?path=Packages/<name>#<name>/<version>`); release tags are per package.
- Consumed by the mod project **`…/HK_Re-Imagined`** as a local package via its `Packages/manifest.json`
  (that repo is where you playtest/validate; this repo just holds the tool source). See that repo's
  `AGENTS.md` for the broader modding context (decompiled game source location, Mod Tools editor DLLs,
  reset-gate gotchas, etc.) — don't duplicate that here.
- Package layout: `shared/Editor/` holds the two foundation mounts; `core/Editor/` keeps the role-based
  subfolders (`Upgrades/` inspector hooks, `ModTools/` standalone windows, `Debug/` probes, plus general
  infra like `ExportModEditorScriptsPackage.cs` and `UpdateChecker.cs` flat at its root);
  `compatpatcher/Editor/` and `unitvisuals/Editor/` are their own packages (experimental — they simply
  don't get tagged until stable). No `.sln`/`.csproj` committed; validated by opening the consuming
  project in the Unity editor and letting it assemble the scripts.
- Two git repos are involved in most changes: this one (tool source) and `HK_Re-Imagined` (where the
  package is referenced/tested). Check status in both before committing.

## Where to start (docs, in `Docs/`)
| Doc | What it is |
|---|---|
| [README.md](README.md) | Full tool inventory: every window/class, its menu path, and a one-line description. **Check here first** for "what does X do". |
| [EditorWindow-Dependencies.md](EditorWindow-Dependencies.md) | Maps each editor window to its local `.cs` dependencies — needed before exporting/packaging a subset of tools (see `ExportModEditorScriptsPackage.cs`). Update when adding a file with cross-dependencies. |
| [CompatPatcher.md](Packages/com.hk.modtools.compatpatcher/Docs/CompatPatcher.md) | Design spec for the mod Compatibility Patcher. |
| [CompatPatcher-LoadValidations.md](Packages/com.hk.modtools.compatpatcher/Docs/CompatPatcher-LoadValidations.md) | Which load-time validations can reset a retail game (only ~15; the rest are DEBUG-only) — read before changing `LoadOrderValidator.cs`. |
| [CompatPatcher-Manual.md](Packages/com.hk.modtools.compatpatcher/Docs/CompatPatcher-Manual.md) | User manual for the Compatibility Patcher. |
| [manual.md](Packages/com.hk.modtools.core/Docs/manual.md) | User manual for the **export package** scripts (`ModEditorScripts.unitypackage`). |

## Layout (per package)
All menu items live under **`Tools/…`**. Grouped by package / physical subfolder (role-based, not
feature-based — see the README table for feature-area descriptions):

- **`shared` package** — foundation mounts nearly everything else depends on: `VanillaDatabaseMount.cs`,
  `ArchiveTranslations.cs`.
- **`core/Editor/Upgrades/`** — hooks that augment vanilla ModTools inspectors rather than open their own window:
  `DescriptorMapperPreview.cs`, `DescriptorMapperGenerator.cs`, `PropertyEffectDrawer.cs`,
  `InspectorAnalysisPanel.cs`, `InspectorDiagnostics.cs`, `LocalizationKeyDrawer.cs`,
  `InlineLocalizationEditor.cs`.
- **`core/Editor/ModTools/`** — standalone browsing/editing windows: `DatabaseBrowser.cs`,
  `DescriptorPropertyIndex.cs`, `TechTreeWindow.cs`, `TechTreeData.cs`, `AssetExplorer.cs`,
  `ModBuildWindow.cs`.
- **`core/Editor/Debug/`** — standalone diagnostic probes: `Probing.cs`, `FormulaProbe.cs`,
  `FormulaAutocompleteProbe.cs`, `NarrativeEventDiagnostic.cs`, `GuidLookup.cs`, `BundleContentProbe.cs`.
- **`unitvisuals` package** (experimental — ships only when tagged; see Conventions below):
  `UnitVisualWorkflow.cs` (wizard), `PawnFragmentAuthor.cs`, `Tier1MeshBaker.cs`,
  `BoneStructureMatcher.cs`, `FbxPrepPipeline.cs`, `ModelRequirementsChecker.cs`,
  `AnimationManagerContent.cs`, `VanillaAssetResolver.cs`.
- **`compatpatcher` package** (experimental — ships only when tagged): `CompatPatcherWindow.cs` (main
  window), `CompatCompareWindow.cs`, `ModReader.cs`, `ConflictAnalyzer.cs`, `UnityYaml.cs`,
  `PatchBuilder.cs`, `Sidecar.cs`, `LoadOrderValidator.cs`, `DiffGui.cs`.
- **`core/Editor/` root** — general package infra, not scoped to one domain: `ExportModEditorScriptsPackage.cs`,
  `UpdateChecker.cs` (checks the repo's per-package tags — `<package-name>/<version>` — for every installed
  `com.hk.modtools.*` git package and offers to update each in place via `Client.Add` with the `?path=` URL; skips packages consumed via a local `file:` reference).

## Conventions & gotchas
- **No local `.cs` dependencies unless noted** — most tools are standalone; the ones that share
  infrastructure lean on `VanillaDatabaseMount.cs` (shared vanilla bundle mount) and
  `ArchiveTranslations.cs` (shared localization mount). Check
  `EditorWindow-Dependencies.md` (repo root) before assuming a file is self-contained, and update it when
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
  `EditorWindow-Dependencies.md` (repo root) when adding inspector hooks or cross-file dependencies.
- **`unitvisuals` and `compatpatcher` are their own packages** precisely because they're experimental —
  they ship only when someone tags them (`com.hk.modtools.compatpatcher/x.y.z`), so no release-branch
  surgery is needed. Confirmed nothing outside either package references its types. Keep new WIP/unstable
  tools in their own package for the same reason rather than dropping them into `core`.

## Commit conventions
- Commit/PR only when asked; branch off `main` first. Co-author trailer per repo norm.
- Because a change here is usually paired with a change in `HK_Re-Imagined` (package bump/testing),
  double check which repo's changes you're actually committing before running `git commit`.
