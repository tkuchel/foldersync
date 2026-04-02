# FolderSync

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
foldersync install
foldersync uninstall
foldersync status
foldersync status --verbose
foldersync status --json
foldersync health
foldersync health --json
foldersync pause --reason "Maintenance window"
foldersync resume
foldersync dashboard
```

`status --verbose` is the best human-oriented operator view.

`status --json` emits the full structured service/runtime report, including install/config/log paths, runtime counters, reconciliation metadata, and recent activity.

`health` is a compact summary for quick checks.

`health --json` emits a smaller machine-friendly payload for scripts, automation, and dashboards.

`pause` and `resume` control the running service through a persisted control file.

`dashboard` starts a lightweight local web dashboard on `http://127.0.0.1:8941/` by default and opens it in your browser.

For a deployed service under `C:\FolderSync`, these commands may need an elevated PowerShell window so they can update the control file in the install directory.

## Configuration

The application reads configuration from:

- [appsettings.example.json](/T:/repos/foldersync/src/FolderSync/appsettings.example.json)
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
```

The running service also persists runtime health to `foldersync-health.json` in the install directory.

That snapshot includes:

- per-profile processed/succeeded/skipped/failed counts
- watcher overflow counts
- last successful sync and last failure
- repeated-failure / repeated-overflow alerts
- latest reconciliation trigger, duration, exit meaning, and parsed robocopy summary

Optional alert notifications can be configured under `FolderSync:Notifications` with a webhook URL and cooldown.

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

The repo ignores generated output like `bin/`, `obj/`, logs, IDE state, and local Codex/Claude workspace files.

## Branching

- `main` is the stable branch for tested, deployable changes.
- `develop` is the integration branch for ongoing feature work and product experiments.
- New feature batches should land on `develop` first, then be promoted to `main` once they are verified and ready to deploy.
- Operational fixes that must go live quickly can still land on `main`, but should be merged or replayed back into `develop` so both branches stay aligned.

## Roadmap

The current next-frontier work is:

- profile-level pause and resume controls instead of only global pause/resume
- richer dashboard interactions, including profile filtering and operator actions
- notification integrations and templates for tools like Slack or Teams
- broader operator UX improvements built on the existing health and status APIs

## Local Deployment

For the installed service in `C:\FolderSync`, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Deploy-Local.ps1
```

This script:

- runs tests by default
- validates the live `C:\FolderSync\appsettings.json`
- publishes fresh binaries from the repo
- preserves the live `appsettings.json`
- leaves the `logs` directory untouched
- stops and restarts the `FolderSync` service when needed

Useful options:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Deploy-Local.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\scripts\Deploy-Local.ps1 -SkipTests
powershell -ExecutionPolicy Bypass -File .\scripts\Deploy-Local.ps1 -NoRestart
```
