# Changelog

All notable changes to `com.hk.modtools.shared` are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Versions match namespaced git tags: `shared/<semver>`.

## [Unreleased]

### Added
- `WindowMinimize` — fake minimize for floating tool windows (lower-right strip stack; paired siblings share one slot). Stack state persists across domain reload via SessionState; orphaned strip-locked windows are rescued if session is missing.

## [1.1.0] - 2026-07-18

### Added
- Options package catalog: Install / Update / Remove from namespaced git tags (auto-discovered).
- Dependency resolution on install/update (bump missing or outdated `com.hk.modtools.*` deps).
- Per-update "What's new" changelog popup (versions between installed and latest).
- "Changelog" button when already on the latest version (history through the installed release).

## [1.0.1] - 2026-07-16

### Added
- Per-package MIT `LICENSE` file.

### Changed
- Package version bump alongside Core 1.0.1.

## [1.0.0] - 2026-07-16

### Added
- Initial multi-package release: vanilla database mount, archive translations mount,
  Update Checker, and Tools Options window.
