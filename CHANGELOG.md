# Changelog

All notable changes to FolderSync are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.1] - 2026-04-09

### Added
- `LICENSE` file (MIT).
- `CHANGELOG.md` tracking release history in Keep a Changelog format.
- `SECURITY.md` with supported versions and private disclosure guidance.
- `CONTRIBUTING.md` documenting the branch model, build/test workflow, and PR expectations.
- `CODE_OF_CONDUCT.md` (Contributor Covenant 2.1).
- Build status, release, license, and .NET version badges in `README.md`.

### Changed
- Replaced absolute local filesystem links in `README.md` and `RELEASE_CHECKLIST.md`
  with repository-relative paths so they render correctly on GitHub.
- Bumped project version to `1.0.1` in `Directory.Build.props`.

## [1.0.0] - 2026-04-04

### Added
- Windows-first one-way folder synchronization service (`foldersync`).
- Continuous file watcher with overflow detection and automatic reconcile recovery.
- Periodic reconciliation with `robocopy`, including parsed exit summaries.
- Multi-profile configuration with inherited defaults and per-profile state.
- Commands: `run`, `reconcile`, `validate-config`, `validate-deploy`, `install`,
  `uninstall`, `status` (plus `--verbose` and `--json`), `health` (plus `--json`),
  `pause`, `resume`, `dashboard`.
- Pause/resume control at both service and per-profile scope via a persisted
  control file (`foldersync-control.json`).
- Runtime health snapshot persisted to `foldersync-health.json` with per-profile
  counters, watcher overflow counts, last failure, last reconcile metadata, and
  repeated-failure alerts.
- Local web dashboard on `http://127.0.0.1:8941/` with profile filtering,
  per-profile pause/resume, one-click reconcile, queued reconcile visibility,
  and per-profile recent activity history.
- Windows tray companion (`FolderSync.Tray`) with live status, alert balloons,
  quick pause/resume/reconcile actions, `Start with Windows` toggle, and a
  `Restart as administrator` option for control actions that need elevation.
- Optional destination-side retention policies for backup-style folders with
  file or directory item types, search patterns, minimum-age safeties, and
  multiple trigger modes (`ReconciliationOnly`, `SyncOnly`, `ReconciliationAndSync`).
- Alert notifications for Slack, Teams, and generic webhook providers with
  configurable cooldowns and title prefixes.
- Deletion safety defaults: `SyncDeletions=false`, `DeleteMode=Archive`,
  destination root constraint, and drive-root archive rejection.
- Deploy script `scripts/Deploy-Local.ps1` for installed-service deployments to
  `C:\FolderSync`, with `-WhatIf`, `-SkipTests`, `-NoRestart`, and `-SkipTray`.
- Release workflow `.github/workflows/release.yml` with tag-to-version gating,
  artifact validation (`Validate-ReleaseArtifacts.ps1`), and headless smoke
  tests (`Smoke-Test-ReleaseArtifacts.ps1`).
- CI workflow `.github/workflows/ci.yml` with build, test, and publish smoke
  tests for both the service and tray on `windows-latest`.

[Unreleased]: https://github.com/tkuchel/foldersync/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/tkuchel/foldersync/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/tkuchel/foldersync/releases/tag/v1.0.0
