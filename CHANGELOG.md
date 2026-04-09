# Changelog

All notable changes to FolderSync are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.6] - 2026-04-09

### Fixed
- `AlertNotifierTests.Publish_Retries_After_Failed_Send_Instead_Of_Entering_Cooldown`
  was flaky on CI. The test waited for the handler's `SendAsync` to be
  called but not for the background `Task.Run` inside `Publish` to finish
  its catch/finally block, so the second `Publish` could observe stale
  `_inFlight` state and bail out early. `AlertNotifier` now exposes an
  internal `WaitForPendingPublishAsync` test observation hook (visible
  via the existing `InternalsVisibleTo` to the test project) and the
  test awaits it after each publish to deterministically wait for
  background completion.
- `.github/workflows/dependency-review.yml` now skips on private
  repositories, where `actions/dependency-review-action` requires GitHub
  Advanced Security and would fail every dependabot PR. The workflow is
  gated on `github.event.repository.private == false || workflow_dispatch`
  so it is ready to enforce automatically the moment the repository is
  made public.
- `.github/workflows/ci.yml` test-reporter step now sets
  `fail-on-empty: false` so the job summary does not error when tests
  fail before producing a TRX file.

### Changed
- Bumped project version to `1.0.6` in `Directory.Build.props`.

## [1.0.5] - 2026-04-09

### Added
- `.github/workflows/release.yml` now generates a `SHA256SUMS.txt` file
  for the service and tray zips and attaches it to the GitHub release.
- CycloneDX SBOMs are generated for both published outputs via
  `anchore/sbom-action` and attached to the release as
  `foldersync-service-<tag>-sbom.cdx.json` and
  `foldersync-tray-<tag>-sbom.cdx.json`.
- Build provenance attestations are produced for both release zips via
  `actions/attest-build-provenance`, giving signed, OIDC-backed
  verification of where and how the binaries were built (no code
  signing certificate required).
- Release notes for each tag are now extracted from the matching
  `## [x.y.z]` heading in `CHANGELOG.md` and used as the GitHub release
  body. Auto-generated commit-based notes are appended after the
  curated section.
- `workflow_dispatch` now accepts a `dry_run` boolean input that builds,
  validates, smoke-tests, and uploads artifacts without publishing a
  GitHub release or creating attestations.

### Changed
- Release workflow now sets the standard
  `DOTNET_CLI_TELEMETRY_OPTOUT`, `DOTNET_NOLOGO`, and
  `DOTNET_SKIP_FIRST_TIME_EXPERIENCE` environment variables, caches
  `~/.nuget/packages`, uses `fetch-depth: 0` so tags and history are
  available for CHANGELOG extraction, and declares explicit
  `contents: write`, `id-token: write`, and `attestations: write`
  permissions.
- `upload-artifact` now bundles the zips, checksums, and both SBOMs
  under a single `foldersync-release-<tag>` artifact.
- Bumped project version to `1.0.5` in `Directory.Build.props`.

## [1.0.4] - 2026-04-09

### Added
- `.github/workflows/codeql.yml` running CodeQL `security-and-quality`
  analysis on pushes to `main` and `develop`, on pull requests, and on a
  weekly schedule.
- `.github/workflows/dependency-review.yml` running
  `actions/dependency-review-action` on pull requests with a
  high-severity vulnerability gate and license and vulnerability checks.
- CodeQL status badge in `README.md`.

### Changed
- `.github/workflows/ci.yml` now triggers on `develop` branch pushes as
  well as `main`, adds `workflow_dispatch`, a `concurrency` group that
  cancels superseded runs on the same ref, and explicit read permissions.
- CI now caches `~/.nuget/packages` keyed on `*.csproj` and
  `Directory.Build.props` hashes, and sets the standard
  `DOTNET_CLI_TELEMETRY_OPTOUT`, `DOTNET_NOLOGO`, and
  `DOTNET_SKIP_FIRST_TIME_EXPERIENCE` environment variables.
- CI `dotnet test` step now collects `XPlat Code Coverage` and a TRX
  logger. Test results are published via `dorny/test-reporter` and the
  Cobertura coverage file is uploaded as a 14-day artifact. A short
  coverage summary (line and branch percentages) is written to the
  job step summary.
- Removed the redundant "Validate JSON Report Shape" filtered test pass
  from CI; those tests already run in the main `dotnet test` step.
- Bumped project version to `1.0.4` in `Directory.Build.props`.

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

[Unreleased]: https://github.com/tkuchel/foldersync/compare/v1.0.6...HEAD
[1.0.6]: https://github.com/tkuchel/foldersync/compare/v1.0.5...v1.0.6
[1.0.5]: https://github.com/tkuchel/foldersync/compare/v1.0.4...v1.0.5
[1.0.4]: https://github.com/tkuchel/foldersync/compare/v1.0.3...v1.0.4
[1.0.3]: https://github.com/tkuchel/foldersync/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/tkuchel/foldersync/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/tkuchel/foldersync/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/tkuchel/foldersync/releases/tag/v1.0.0
