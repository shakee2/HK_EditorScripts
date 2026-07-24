# Changelog

All notable changes to `com.hk.modtools.core` are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Versions match namespaced git tags: `core/<semver>`.

## [Unreleased]

### Added
- **Unit Family Lines** window (`Tools/shakee's Tools/Unit Family Lines`) — pan/zoom DAG of
  `UnitFamilyDefinition` upgrade lines (`SerializableNextFamilyName`), laid out top→bottom in
  Land / Naval / Air domain bands with a wrapped "no upgrade path" singleton strip. Click a family
  to list its `UnitDefinition`s and reassign each unit's family/level, retarget (or clear) the
  family's Next link, and follow highlighted pre/after chains. Edits auto-import (lift)
  vanilla/mounted elements into New Additions before writing. Import/Export JSON round-trips family
  `next`/obsolete + unit family/level assignments and creates missing families on import. Debug
  dump at `Tools/shakee's Tools/Debug/Unit Family Lines/Dump Data`.

## [1.0.3] - 2026-07-19

### Added
- Database Browser: **Group** popup — **None** / **Type** / **Folder** (folder = directory of
  the project asset or vanilla collection path). Replaces the old Type-only toggle.
- Database Browser: **`!`** prefix + **Dupes !** scope filter for My Mod content rows that
  share the same `(type, name)` with another project object (load-order collision).
- Database Browser: multi-select list rows (**Ctrl/Cmd+click** toggle, **Shift+click** range).
  Right-click a vanilla multi-selection to **Import N Selected (Override from Archives)**;
  My Mod multi-selection gets **Delete N Selected**. Single-row Import/Delete unchanged.
- Database Browser: right-click **Delete** on My Mod rows (project assets under `Assets/`;
  collection sub-assets remove the element only; main assets delete the file).
- Pawn Probe field dump: expand `SettlementStabilityPrerequisite` (enum `Operator` + nested
  `PublicOrderEffects` list entries).
- Tooltip preview: resolve bracket icon tags (`[ScienceColored]`, `[Pollution1]`, …) to the
  matching UIMapper `Symbol` → `Images[Picto]` texture (project + mounted vanilla). Bakes a
  white+alpha mask (via `Hidden/HK/UIPictoTint`) and draws with `GUI.DrawTexture` so icons
  tint correctly and stay clipped inside scroll/containers (`DrawPreviewTexture` ignores IMGUI
  clip and floated over the inspector). Uses the UIMapper `Color` when set; black/unset falls
  back to white. Alias: `[Workplace]` reuses the `[Population]` picto. Also interprets Amplitude
  rich-text markup (`<c=RRGGBB[AA]>`, `<b>`, `<i>`, and other ProcessedText face tags —
  `<u>`/`<s>`/`<m>` are consumed so they don't leak as text). Unmatched tags stay as text.
  Applies to the header Tooltip Breakdown Preview and the PropertyEffect inline Rendered box.

### Changed
- Depends on `com.hk.modtools.shared` **1.1.1** (WindowMinimize on browser/tech tree/descriptor index).

## [1.0.2] - 2026-07-18

### Fixed
- Formula autocomplete: replace spans the full identifier (including text after the caret) so
  confirming mid-name no longer leaves a trailing fragment (e.g. `…Gainn`).
- Formula autocomplete: the already-present property is kept and pinned at the top of the list
  so default confirm is a no-op (avoids accidental swaps like Food ↔ Fame).

## [1.0.1] - 2026-07-16

### Added
- Per-package MIT `LICENSE` file.

### Changed
- Package version bump; depends on `com.hk.modtools.shared` 1.0.1.

## [1.0.0] - 2026-07-16

### Added
- Initial multi-package release: database browser, tech tree, asset explorer, build/export,
  inspector upgrades (inline localization, tooltip preview, formula autocomplete, diagnostics),
  and debug probes.
