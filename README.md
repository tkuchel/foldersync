# FolderSync

[![CI](https://github.com/tkuchel/foldersync/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/tkuchel/foldersync/actions/workflows/ci.yml)
[![CodeQL](https://github.com/tkuchel/foldersync/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/tkuchel/foldersync/actions/workflows/codeql.yml)
[![Release](https://img.shields.io/github/v/release/tkuchel/foldersync?include_prereleases&sort=semver)](https://github.com/tkuchel/foldersync/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6)](https://github.com/tkuchel/foldersync)

FolderSync is a Windows-first one-way folder synchronization tool for keeping a destination folder in sync with a source folder.

It supports:

- Continuous file watching
- Periodic reconciliation with `robocopy`
- Optional delete mirroring with archive mode
- Windows service install, uninstall, and status commands
- Multiple sync profiles with shared defaults

## Commands

```powershell
foldersync run
foldersync reconcile
foldersync validate-config
foldersync validate-deploy
foldersync install
foldersync uninstall
foldersync status
foldersync status --verbose
foldersync status --json
foldersync health
foldersync health --json
foldersync pause --reason "Maintenance window"
foldersync pause --profile example-workspace --reason "Index rebuild"
foldersync resume
foldersync resume --profile example-workspace
foldersync dashboard
```

Tray companion:

```powershell
dotnet run --project src\FolderSync.Tray\FolderSync.Tray.csproj
```

`status --verbose` is the best human-oriented operator view.

`status --json` emits the full structured service/runtime report, including install/config/log paths, runtime counters, pause state, queued reconcile requests, reconciliation metadata, and recent activity.

`health` is a compact summary for quick checks.

`health --json` emits a smaller machine-friendly payload for scripts, automation, and dashboards.

`pause` and `resume` control the running service through a persisted control file. When you pass `--profile`, only that profile pauses and the rest of the service keeps running.

Dashboard and tray reconcile actions are also routed through the persisted control file. The running service consumes those queued reconcile requests, so installed-service operator actions no longer spawn a second one-shot reconcile process.

`dashboard` starts a lightweight local web dashboard on `http://127.0.0.1:8941/` by default and opens it in your browser.
The dashboard now supports:

- filtering by profile name
- pause and resume actions for the whole service
- pause and resume actions per profile
- queued reconcile visibility for the whole service and per profile
- per-profile recent activity history
- one-click profile reconciliation from the installed service

`FolderSync.Tray` is a lightweight Windows tray companion for the installed service. The first version provides:

- live tray status from the persisted health snapshot
- alert balloons for service/profile warnings
- quick pause, resume, and reconcile actions
- queued reconcile visibility in the tray status and profile menus
- per-profile quick actions
- one-click dashboard launch
- one-click profile jump into the dashboard
- one-click open install folder
- immediate pause-state overlay from the control file
- tray-originated recent-activity breadcrumbs
- a `Start with Windows` toggle
- a `Restart as administrator` option when control actions need elevation

For a deployed service under `C:\FolderSync`, these commands may need an elevated PowerShell window so they can update the control file in the install directory.

The tray app works best as a published executable. The local deploy script now publishes it to:

```text
C:\FolderSync\Tray\foldersync-tray.exe
```

If you run it via `dotnet run`, the `Start with Windows` option will point at the current process path rather than a separately installed tray binary.

## Configuration

The application reads configuration from:

- [appsettings.example.json](./src/FolderSync/appsettings.example.json)
- An optional local `appsettings.json`
- An optional custom file passed with `--config`

The checked-in example file is safe to commit. Your local `appsettings.json` is ignored by Git and overrides the example when present.

## Safe Setup

1. Copy `src/FolderSync/appsettings.example.json` to `src/FolderSync/appsettings.json`.
2. Update the source and destination paths in your local `appsettings.json`.
3. Start with `"DryRun": true` while validating behavior.
4. Run `foldersync reconcile --config <your-config>` to test a one-shot sync.
5. Run `foldersync validate-config --config <your-config>` to verify profile safety checks.
6. Review the destination and logs.
7. Only enable deletion sync after you are confident the mapping is correct.

Useful runtime checks:

```powershell
foldersync status --verbose
foldersync status --json
foldersync health
foldersync health --json
foldersync validate-deploy --target-dir C:\FolderSync --skip-tests
```

A practical operator reference for common runtime states lives in [TROUBLESHOOTING.md](./TROUBLESHOOTING.md).

The running service also persists runtime health to `foldersync-health.json` in the install directory.
Control state and queued operator requests are persisted separately in `foldersync-control.json`.

That snapshot includes:

- per-profile processed/succeeded/skipped/failed counts
- watcher overflow counts
- last successful sync and last failure
- repeated-failure / repeated-overflow alerts
- latest reconciliation trigger, duration, exit meaning, and parsed robocopy summary

The control file includes:

- global pause state and pause reason
- per-profile pause state
- queued reconcile requests waiting to be consumed by the running service

Profiles can also apply optional destination-side retention for backup-style folders. When enabled, FolderSync keeps only the newest top-level destination directories that match a configured search pattern after a successful reconciliation run.

Example:

```json
"Retention": {
  "Enabled": true,
  "KeepNewestCount": 5,
  "ItemType": "Directories",
  "RelativePath": "Nightly",
  "Recursive": false,
  "TriggerMode": "ReconciliationOnly",
  "MinAgeHours": 24,
  "SearchPattern": "backup-*",
  "SortBy": "NameDescending"
}
```

For nightly zip-style backups in a single destination folder, switch to file retention instead:

```json
"Retention": {
  "Enabled": true,
  "KeepNewestCount": 5,
  "ItemType": "Files",
  "RelativePath": "ZipBackups",
  "Recursive": false,
  "TriggerMode": "ReconciliationOnly",
  "MinAgeHours": 24,
  "SearchPattern": "backup-*.zip",
  "SortBy": "NameDescending"
}
```

Set `Recursive` to `true` if you want retention to search nested folders under the scoped path instead of only its top level.

`TriggerMode` controls when retention runs:

- `ReconciliationOnly`: only after a successful reconciliation run
- `SyncOnly`: only after successful watcher-driven sync operations
- `ReconciliationAndSync`: after both successful reconciliations and successful watcher-driven sync operations

`MinAgeHours` adds a safety floor before pruning. For example, `24` means FolderSync will still protect the newest `N` items first, and then only prune older overflow items once they are at least 24 hours old.

`RelativePath` is optional and is resolved under the profile destination. Retention applies to the matching files or directories inside that scoped folder and can either archive or delete older items based on the profile's existing `DeleteMode` settings.

Optional alert notifications can be configured under `FolderSync:Notifications` with a webhook URL and cooldown.
Supported notification providers are:

- `Generic`
- `Slack`
- `Teams`

Example:

```json
"Notifications": {
  "Enabled": true,
  "Provider": "Slack",
  "WebhookUrl": "https://hooks.slack.com/services/...",
  "CooldownMinutes": 15,
  "TitlePrefix": "FolderSync"
}
```

## Deletion Safety

- `SyncDeletions` defaults to `false`
- `DeleteMode` defaults to `Archive`
- File operations are constrained to the configured destination root
- Archive roots that point at a drive/share root are rejected

## Development

Build and test with:

```powershell
dotnet test FolderSync.slnx --nologo
```

## Versioning

- Shared version metadata for both binaries is defined in [Directory.Build.props](./Directory.Build.props).
- `Version` and `InformationalVersion` should match the intended release tag, for example `1.0.1`.
- `AssemblyVersion` and `FileVersion` should stay in four-part form, for example `1.0.1.0`.
- When cutting a release, bump the project version first, build/test, then create the matching Git tag.

## Releases

- CI now smoke-tests `dotnet publish` for both the service and tray companion on Windows.
- Tagged pushes such as `v1.0.1` now run [release.yml](./.github/workflows/release.yml), which builds, tests, publishes, zips, and attaches both service and tray artifacts to a GitHub release.
- The release workflow validates that the pushed tag matches the `Version` declared in [Directory.Build.props](./Directory.Build.props) before publishing artifacts.
- The packaged zip files are also checked by [Validate-ReleaseArtifacts.ps1](./scripts/Validate-ReleaseArtifacts.ps1) so missing executables or runtime files fail the release.
- The packaged executables are smoke-tested by [Smoke-Test-ReleaseArtifacts.ps1](./scripts/Smoke-Test-ReleaseArtifacts.ps1), which validates the published service binary against a temp config and runs the tray in a headless `--smoke-test` mode.
- Local release validation should still include `dotnet test FolderSync.slnx --nologo` before tagging.
- A short human release checklist lives in [RELEASE_CHECKLIST.md](./RELEASE_CHECKLIST.md).
- Operator troubleshooting guidance lives in [TROUBLESHOOTING.md](./TROUBLESHOOTING.md).

The repo ignores generated output like `bin/`, `obj/`, logs, IDE state, and local Codex/Claude workspace files.

## Branching

- `main` is the stable branch for tested, deployable changes.
- `develop` is the integration branch for ongoing feature work and product experiments.
- New feature batches should land on `develop` first, then be promoted to `main` once they are verified and ready to deploy.
- Operational fixes that must go live quickly can still land on `main`, but should be merged or replayed back into `develop` so both branches stay aligned.

## Roadmap

The current next-frontier work is:

- clearer operator state transitions in the dashboard and tray, especially differentiating `queued`, `running`, and `service unavailable`
- deeper integration coverage around watcher overflow and cross-process operator workflows
- release packaging automation for tagged builds and attached artifacts
- incremental operator UX improvements built on the existing health, status, and control-file APIs

Current progress on `develop`:

- profile-level pause and resume is implemented
- dashboard filtering and pause/resume actions are implemented
- Slack/Teams notification payload templates are implemented
- dashboard profile activity history and one-click reconcile actions are implemented
- service-owned queued reconcile requests are implemented for dashboard and tray operator actions
- queued reconcile visibility is implemented in `status --json`, the dashboard, and the tray

## Local Deployment

For the installed service in `C:\FolderSync`, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Deploy-Local.ps1
```

For a no-touch deployment rehearsal that validates the live config, runs publish, and checks packaged artifacts without stopping or updating the service, use:

```powershell
dotnet run --project src\FolderSync -- validate-deploy --target-dir C:\FolderSync
```

This script:

- runs tests by default
- validates the live `C:\FolderSync\appsettings.json`
- publishes fresh binaries from the repo
- publishes the tray companion to `C:\FolderSync\Tray\`
- preserves the live `appsettings.json`
- leaves the `logs` directory untouched
- stops and restarts the `FolderSync` service when needed

Useful options:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Deploy-Local.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\scripts\Deploy-Local.ps1 -SkipTests
powershell -ExecutionPolicy Bypass -File .\scripts\Deploy-Local.ps1 -NoRestart
powershell -ExecutionPolicy Bypass -File .\scripts\Deploy-Local.ps1 -SkipTray
```

Useful `validate-deploy` options:

```powershell
dotnet run --project src\FolderSync -- validate-deploy --target-dir C:\FolderSync --skip-tests
dotnet run --project src\FolderSync -- validate-deploy --target-dir C:\FolderSync --skip-tray
dotnet run --project src\FolderSync -- validate-deploy --target-dir C:\FolderSync --configuration Release
```

## Verifying Downloads

Every tagged GitHub release ships with verification artifacts alongside
the `foldersync-service-<tag>.zip` and `foldersync-tray-<tag>.zip`:

- **`foldersync-<tag>-SHA256SUMS.txt`** — SHA256 checksums for both zips.
- **`foldersync-service-<tag>-sbom.cdx.json`** and
  **`foldersync-tray-<tag>-sbom.cdx.json`** — CycloneDX software bill of
  materials for each published output.
- A **build provenance attestation** (SLSA v1, signed via GitHub OIDC).

Verify a download before running it:

```powershell
# Checksums
Get-Content .\foldersync-<tag>-SHA256SUMS.txt
(Get-FileHash .\foldersync-service-<tag>.zip -Algorithm SHA256).Hash.ToLower()
(Get-FileHash .\foldersync-tray-<tag>.zip    -Algorithm SHA256).Hash.ToLower()

# Build provenance
gh attestation verify .\foldersync-service-<tag>.zip --owner tkuchel
gh attestation verify .\foldersync-tray-<tag>.zip    --owner tkuchel
```

Attestation verification proves that the zip was built by the
`.github/workflows/release.yml` workflow in this repository, at the commit
matching the tag, on a GitHub-hosted runner. No additional code-signing
certificate is required.

## Contributing

Pull requests are welcome. Please read [CONTRIBUTING.md](./CONTRIBUTING.md) for
the branch model, build/test commands, and PR expectations, and follow the
[Code of Conduct](./CODE_OF_CONDUCT.md).

For release history see [CHANGELOG.md](./CHANGELOG.md).

## Security

If you discover a security issue, please follow the private disclosure process
described in [SECURITY.md](./SECURITY.md) rather than opening a public issue.

## License

FolderSync is released under the [MIT License](./LICENSE).
