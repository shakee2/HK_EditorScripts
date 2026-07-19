# AGENTS.md — HK_EditorScripts (Humankind modding editor tools, multi-package repo)

Orientation for a fresh agent. This is a **router**: it points to the authoritative doc/source for
each thing and only inlines facts that are always true. When a section here and a linked doc disagree,
the linked doc wins — fix this file. Verify any file/line ref before relying on it; the code moves.

## What this is
- As of **v1.1.0** this repo is a **multi-package monorepo**: four independent UPM packages live under
  [`Packages/`](Packages/), each with its own `package.json` + editor-only asmdef (no runtime references):
  `com.hk.modtools.shared`, `com.hk.modtools.core`, `com.hk.modtools.compatpatcher`,
  `com.hk.modtools.unitvisuals`. Before v1.1.0 this was a single root-level package
  (`com.shakee.hk-editorscripts`) — that layout is retired; see [README.md's migration
  section](README.md#migrating-from-the-pre-110-single-package-layout) if you find references to it.
- Consumed by Humankind mod Unity projects as UPM packages via each consumer's `Packages/manifest.json`
  (local `file:` while developing, or git URL + tag for a release). This repo holds the tool source;
  validate by opening a consuming project in the Unity editor. Broader modding context (decompiled game
  paths, Mod Tools DLLs, reset-gate gotchas) belongs in that consumer's docs — don't duplicate it here.
- **`com.hk.modtools.shared` is a real, declared `package.json` dependency of the other three** — not a
  reflection/soft dependency. `com.hk.modtools.core` merges what used to be separate `ModTools/`,
  `Upgrades/`, and `Debug/` folders into one package specifically so `DatabaseBrowser.cs`'s calls into
  `InspectorDiagnostics.cs` stay an ordinary same-package reference (no reflection seam needed).
  `compatpatcher`/`unitvisuals` only need `shared` (verified via grep — see git history of this file's
  restructure commit for the check) and ship **no tag** until each is stable enough to release; an
  untagged package is simply not installable via a pinned tag, which replaces the old release-branch
  file-stripping approach.
- No `.sln`/`.csproj` committed anywhere; every package is validated by opening the consuming project in
  the Unity editor and letting it assemble the scripts. Plan an editor pass (and time for a domain
  reload) into any non-trivial change.

## Where to start (docs)
| Doc | What it is |
|---|---|
| [README.md](README.md) | Full package + tool inventory, install instructions, migration notes. **Check here first** for "what does X do" or "which package is X in". |
| [EditorWindow-Dependencies.md](EditorWindow-Dependencies.md) | Maps each editor window to its local `.cs` dependencies, grouped by package — needed before exporting/packaging a subset of tools (see `ExportModEditorScriptsPackage.cs`). Update when adding a file with cross-dependencies. |
| [Packages/com.hk.modtools.compatpatcher/Docs/CompatPatcher.md](Packages/com.hk.modtools.compatpatcher/Docs/CompatPatcher.md) | Design spec for the mod Compatibility Patcher. |
| [Packages/com.hk.modtools.compatpatcher/Docs/CompatPatcher-LoadValidations.md](Packages/com.hk.modtools.compatpatcher/Docs/CompatPatcher-LoadValidations.md) | Which load-time validations can reset a retail game (only ~15; the rest are DEBUG-only) — read before changing `LoadOrderValidator.cs`. |
| [Packages/com.hk.modtools.compatpatcher/Docs/CompatPatcher-Manual.md](Packages/com.hk.modtools.compatpatcher/Docs/CompatPatcher-Manual.md) | User manual for the Compatibility Patcher. |
| [Packages/com.hk.modtools.core/Docs/manual.md](Packages/com.hk.modtools.core/Docs/manual.md) | User manual for the **export package** scripts (`ModEditorScripts.unitypackage`). |

## Package layout
All menu items live under **`Tools/…`**. Grouped by package, then by role-based subfolder within each
package's `Editor/` (see the README table for feature-area descriptions):

- **`Packages/com.hk.modtools.shared/Editor/`** — `VanillaDatabaseMount.cs`, `ArchiveTranslations.cs`
  (foundation mounts nearly everything else depends on), `WindowMinimize.cs` (floating-window minimize
  strip stack), `UpdateChecker.cs` (multi-package git-tag
  update checker + Install/Remove APIs; Options catalog auto-discovered from namespaced tags;
  fetches per-package `CHANGELOG.md` for What's new), `ToolsOptionsWindow.cs` (aggregate settings —
  package Install/Update/Remove catalog, auto-update toggles/intervals per installed git package + a
  link into Unity's Shortcuts manager), `ChangelogPopupWindow.cs` (utility popup: collapsible
  per-version cards for the update range). No dependencies of its own.
- **`Packages/com.hk.modtools.core/Editor/`** — depends on `shared`.
  - `ModTools/` — standalone browsing/editing windows: `DatabaseBrowser.cs`,
    `DescriptorPropertyIndex.cs`, `TechTreeWindow.cs`, `TechTreeData.cs`, `AssetExplorer.cs`,
    `ModBuildWindow.cs`.
  - `Upgrades/` — hooks that augment vanilla ModTools inspectors rather than open their own window:
    `DescriptorMapperPreview.cs`, `DescriptorMapperGenerator.cs`, `PropertyEffectDrawer.cs`,
    `InspectorAnalysisPanel.cs`, `InspectorDiagnostics.cs`, `LocalizationKeyDrawer.cs`,
    `InlineLocalizationEditor.cs`.
  - `Debug/` — standalone diagnostic probes: `Probing.cs`, `FormulaProbe.cs`,
    `FormulaAutocompleteProbe.cs`, `NarrativeEventDiagnostic.cs`, `GuidLookup.cs`,
    `BundleContentProbe.cs`.
  - `Editor/` root — `ExportModEditorScriptsPackage.cs` (general package infra, not scoped to one
    domain).
  - `Docs/manual.md` — user manual shipped alongside the exported `.unitypackage`.
- **`Packages/com.hk.modtools.compatpatcher/Editor/CompatPatcher/`** (pre-release while `0.x`) —
  depends on `shared` only: `CompatPatcherWindow.cs` (main window), `CompatCompareWindow.cs`,
  `CompatBundleMounts.cs`, `LiveElementBuilder.cs`, `ReflectionBodyMerge.cs`, `ModReader.cs`, `ConflictAnalyzer.cs`,
  `UnityYaml.cs`, `PatchBuilder.cs`, `FieldApplier.cs`, `MassChange.cs`, `MassFieldChangeWindow.cs`, `Sidecar.cs`,
  `LoadOrderValidator.cs`, `DiffGui.cs`, `CompatPatcherWorkspaceGui.cs`, `CompatPatcherManualWindow.cs`.
- **`Packages/com.hk.modtools.unitvisuals/Editor/UnitVisualWorkflow/`** (experimental, no tag yet) —
  depends on `shared` only: `UnitVisualWorkflow.cs` (wizard), `PawnFragmentAuthor.cs`,
  `Tier1MeshBaker.cs`, `BoneStructureMatcher.cs`, `FbxPrepPipeline.cs`, `ModelRequirementsChecker.cs`,
  `AnimationManagerContent.cs`, `VanillaAssetResolver.cs`.
- **`Packages/com.hk.modtools.orphanfinder/Editor/`** — `OrphanResourceFinderWindow.cs`, a standalone
  Resources-folder orphan scanner (images or 3D). **Uses NONE of `shared`'s types** — its asmdef
  (`HK.ModTools.OrphanFinder`) has an empty `references` list; it declares `shared` in `package.json`
  purely to participate in the shared Update Checker / Options ecosystem. The one package here whose
  `package.json` dependency is not backed by an asmdef reference — intentional, don't "fix" it by adding
  the reference (there's no code coupling).

## Tag scheme
- Each package versions **independently** via namespaced tags on this one repo:
  `<shortname>/<semver>` (e.g. `shared/1.0.0`, `core/1.1.0`) — `<shortname>` is the package name with
  the `com.hk.modtools.` prefix stripped.
- **Pre-release convention:** major version below 1 (`0.x`) is pre-release. Options labels it
  `v0.1.0 (pre-release)`. Prefer that over `-preview`/`-rc` suffixes (UpdateChecker uses
  `System.Version`, which does not parse those). Stable starts at `1.0.0`.
- Install URL form: `https://github.com/shakee2/HK_EditorScripts.git?path=Packages/<full-package-name>#<shortname>/<version>`.
- **Changelog:** each tagged package keeps `Packages/<full-package-name>/CHANGELOG.md` (Keep a
  Changelog + semver headings `## [x.y.z]` matching the tag). Ship it in the tagged commit — Options
  "What's new" fetches the file at the latest tag and shows every version between the installed
  release and that latest (newest first, one foldout card each). Untagged / experimental packages
  don't need one until first release.
- `compatpatcher`/`unitvisuals` may ship `0.x` pre-release tags when useful to pin; don't cut `1.0.0`
  until the tool is actually ready. An untagged package stays invisible to anyone pinning a tag.
- The retired single-package layout's last tag is plain `1.1.0` (no `?path=`, no shortname prefix) — its
  `UpdateChecker.cs` was patched to show a one-time "this package has moved" notice; don't reuse
  unprefixed tags for anything going forward.

## Conventions & gotchas
- **`com.hk.modtools.shared` is load-bearing** — every other package's `package.json` declares it as a
  real dependency (name + semver, not a git URL — UPM doesn't support git URLs inside a package's own
  `package.json`, only in the consumer's `manifest.json`). Installing a tool package without `shared`
  present doesn't get blocked outright, but Package Manager shows a dependency-error badge and the
  Console shows real compile errors for the missing types — document this in any user-facing text
  rather than implying installation would simply fail.
- **No local `.cs` dependencies unless noted** — most tools are standalone; the ones that share
  infrastructure lean on `VanillaDatabaseMount.cs` / `ArchiveTranslations.cs` (both in `shared`). Check
  [EditorWindow-Dependencies.md](EditorWindow-Dependencies.md) before assuming a file is self-contained,
  and update it when you add a new cross-file dependency.
- **`ExportModEditorScriptsPackage.cs`** (in `core`) exports a fixed subset of files spanning `shared` +
  `core` as a `.unitypackage` for sharing outside this repo (full list: [README.md § Export
  package](README.md#export-package-modeditorscriptsunitypackage)). If you add a dependency to an
  exported script, update `SHARED_SCRIPT_NAMES`/`CORE_SCRIPT_NAMES` there and the README table.
- **Editor-only, no engine assumptions beyond Editor APIs** — every asmdef restricts to
  `includePlatforms: ["Editor"]`; only `core`/`compatpatcher`/`unitvisuals` reference `shared`'s asmdef
  (by name, e.g. `"HK.ModTools.Shared"` in the `references` array) — no other cross-package asmdef
  references exist. Avoid adding runtime-only dependencies; anything reused from the game's decompiled
  types goes through Mercury's editor/runtime assemblies already available in the consuming project,
  not through any of these asmdefs' references lists.
- **Validate in the consuming project, not here** — this repo has no compile target of its own; after
  editing, open/reload whichever Unity project references these packages to confirm the scripts
  compile and the window(s) behave correctly. Note that a brand-new `.cs` file added here won't
  have a `.meta` until Unity generates one on the consumer's next domain reload — commit that generated
  `.meta` back into this repo afterward so the GUID is stable for everyone else.
- **Hardening new code that shares Mod Tools resources** — our peer is Amplitude's Mod Editor, not
  other tools in these packages. Before attaching to a scarce slot Mod Tools already uses, harden like
  `ArchiveTranslations` / `VanillaDatabaseMount` / the Upgrades drawers already do:
  - **Adopt before mount** — if a provider (or equivalent) is already attached under the same name/path,
    claim it; never double-`LoadFromFile` the same bundle (broken null-content provider → NREs
    Amplifiers' `BuildLocalizationCache` and poisons every inspector).
  - **Unregister side effects** — whoever mounts/stages must scrub orphans and broken providers on
    failure; use `try/finally` for scratch stages. Leaving garbage in Amplitude's global tables breaks
    *their* windows, not just ours.
  - **Layer, don't seize** — inspector hooks use `CallNextDrawer` / `finishedDefaultHeaderGUI` so
    Odin/Amplitude keep ownership of the body; we attach UI below, we don't replace their editor.
  - **Invalidate on the other author's writes** — Mod Editor Localization Window (and similar) can
    mutate the same project overrides; subscribe to `projectChanged` / undo (or bump a generation)
    instead of assuming exclusive authorship.
  - **Do not `AssetDatabase.Refresh()` mid-repair** — that forces Mod Tools cache rebuilds against
    half-registered state. Deep war stories live in the `shared` package's mount comments; keep this
    checklist short and point there rather than duplicating them.
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
  - **A hand-rolled `EditorWindow.OnGUI()` that calls `Editor.CreateEndor(...).OnInspectorGUI()` directly
    (embedded panes, e.g. `DatabaseBrowser`'s right-hand inspector) never fires
    `Editor.finishedDefaultHeaderGUI`** — anything hooked there (tooltip preview, diagnostics panel,
    the formula-suggestion popup's pointer-blocking) silently doesn't run/doesn't protect that pane.
    `LocalizationKeyStringDrawer` and `PropertyEffectDrawer`'s Odin-drawer-based pieces DO still fire
    there (they're regular per-property Odin drawers, a different seam) — only the header-hooked pieces
    are affected. If you add a new header hook, decide up front whether embedded-pane support matters,
    and if so, expose a "draw manually" entry point the embedded pane can call instead of relying solely
    on the global event.
- **Inline `%key` localization on mapper fields** — `LocalizationKeyStringDrawer` + `InlineLocalizationEditor`
  draw Import/edit translation boxes directly under `%key` string fields in the inspector (no Mod Editor
  Localization Window). Applies to **UIMapper** subclasses (**Title**, **Description**, facet titles — the
  UI labels) and **DescriptorMapper** (**LocalizedName**, **EffectLocalization**, …). Same helpers power
  the aggregate Localization foldout in `InspectorDiagnostics`. Full behaviour: [README.md § Inline
  Localization Editing](README.md#inline-localization-editing). **UIMapper auto-generation** is still
  out of scope (`DescriptorMapperGenerator` only pairs Descriptor → DescriptorMapper).
- **New untracked files still need README/dependency-doc entries** — reconcile `README.md` and
  `EditorWindow-Dependencies.md` when adding inspector hooks or cross-file dependencies.
- **The `release` branch predates this restructure and is now legacy** — it existed to strip
  `UnitVisualWorkflow/`/`CompatPatcher/` out of the old single-package layout for release tags. That job
  is now done by `0.x` / untagged experimental packages; don't add new work to the `release` branch.

## Commit conventions
- Commit/PR only when asked; branch off `main` first. Co-author trailer per repo norm.
- **This repo is the package source.** Normal commit/push for tool work happens here. Consuming Unity
  projects that reference these packages via UPM (`file:` or git URL) are separate repos — do not
  commit or push those unless that project's user **explicitly** asks.
- A change to one package's tools often only needs a tag on that one package — you don't need to bump
  every package's version just because you touched one of them.
