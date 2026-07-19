# Changelog

All notable changes to `com.hk.modtools.compatpatcher` are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Versions match namespaced git tags: `compatpatcher/<semver>`.
Major below 1 (`0.x`) is pre-release.

## [Unreleased]

## [0.1.0] - 2026-07-19

### Added
- First pre-release of the Compatibility Patcher (compare mods, resolve conflicts, build `Patch/`).
- In-memory `.assetbundle` compare (session mounts; no staging for bundle sources).
- Workspace: type cards, virtualized element lists, filters beside the list, Diff ↓ / A–Z sort.
- Embedded side-by-side Compare; Mass Change / Apply-to-Patch patterns.
- Patch orphans card; load-order validation; sidecar re-patch fingerprints.
- Unlock-carrier annotations + mod localization resolution for assetbundle sources in diffs.
- Toolbar **Manual** window (`Docs/CompatPatcher-Manual.md`).

### Changed
- Depends on `com.hk.modtools.shared` **1.1.1** (WindowMinimize, benign-NRE filter on mounts, `0.x` Options labels).

### Fixed
- Cross-type Compare selection loading the wrong element.
- Import not refreshing list markers; list scroll / type-switch hitches.
- Name-set diffs showing full lists when only a few entries differ.
