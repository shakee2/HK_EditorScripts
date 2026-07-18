# Changelog

All notable changes to `com.hk.modtools.core` are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Versions match namespaced git tags: `core/<semver>`.

## [Unreleased]

### Added
- Pawn Probe field dump: expand `SettlementStabilityPrerequisite` (enum `Operator` + nested
  `PublicOrderEffects` list entries).

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
