# Changelog

All notable changes to FolderSync are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.3] - 2026-04-09

### Added
- `.editorconfig` with solution-wide formatting defaults, test-project
  rule overrides, and documented severity tweaks for stylistic analyzers.
- Package and source metadata in `Directory.Build.props`: `Authors`,
  `Company`, `Product`, `Copyright`, `PackageLicenseExpression`,
  `PackageProjectUrl`, `RepositoryUrl`, `RepositoryType`,
  `PublishRepositoryUrl`, and `EmbedUntrackedSources` for SourceLink.
- `Microsoft.SourceLink.GitHub` package reference (non-test projects only)
  for debuggable release binaries.
- `AnalysisLevel=latest`, `AnalysisMode=Recommended`, and
  `EnforceCodeStyleInBuild` for consistent analyzer coverage.
- `TreatWarningsAsErrors` is now enabled on CI builds (`CI=true`) so new
  warnings block CI while local development remains unblocked mid-edit.
- `coverlet.collector` package reference in `FolderSync.Tests.csproj` to
  enable `dotnet test --collect:"XPlat Code Coverage"` coverage reports.

### Fixed
- `ReconciliationService` now implements `IDisposable` and disposes its
  `SemaphoreSlim` run gate. `ProfilePipeline.Dispose` now chains the call,
  fixing a semaphore leak when profile pipelines were recycled
  (`CA1001`).
- `FolderSync.Tray.TrayApplicationContext.ResolveInstallLocation` and
  `PersistJson` are now `static` (`CA1822`).
- Logging calls in `FolderSyncService` and `RuntimeHealthStore` now use
  constant message templates so structured logging picks up parameter
  names (`CA2254`).
- `FolderSync.Tray.csproj` no longer hardcodes `Version=1.0.0`. It now
  inherits the shared version from `Directory.Build.props`, so service
  and tray assemblies stay in lockstep. Previous 1.0.1 and 1.0.2 tags
  did not actually update the tray assembly version because of this.

### Changed
- Bumped project version to `1.0.3` in `Directory.Build.props`.
- `IProcessRunner.RunAsync` suppresses `CA1068` locally with a
  justification. Moving `CancellationToken` would require a breaking
  signature change across the service and tests; the trailing
  `TimeSpan? timeout` is a config-style parameter, not an operation
  input.
- `IWatcherService` suppresses `CA1716` locally with a justification.
  `Stop()` is the conventional counterpart to `Start()` and the VB.NET
  keyword conflict is not relevant to this project.

## [1.0.2] - 2026-04-09

### Added
- `.github/ISSUE_TEMPLATE/bug_report.yml` with structured bug reporting form.
- `.github/ISSUE_TEMPLATE/feature_request.yml` for enhancement proposals.
- `.github/ISSUE_TEMPLATE/config.yml` disabling blank issues and linking
  Security Advisories and Discussions.
- `.github/pull_request_template.md` with a test-evidence and safety-review
  checklist covering deletion, reparse-point, and persisted-state-file
  compatibility.
- `.github/CODEOWNERS` auto-assigning reviewers for workflow, script, and
  safety-critical source paths.
- `.github/dependabot.yml` with weekly NuGet and GitHub Actions updates,
  grouped by ecosystem (Microsoft.Extensions, Serilog, test tooling, and a
  catch-all for minor/patch updates).

### Changed
- Bumped project version to `1.0.2` in `Directory.Build.props`.

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

[Unreleased]: https://github.com/tkuchel/foldersync/compare/v1.0.3...HEAD
[1.0.3]: https://github.com/tkuchel/foldersync/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/tkuchel/foldersync/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/tkuchel/foldersync/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/tkuchel/foldersync/releases/tag/v1.0.0
