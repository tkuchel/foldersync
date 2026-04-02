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
```

`status --verbose` is the best human-oriented operator view.

`status --json` emits the full structured service/runtime report, including install/config/log paths, runtime counters, reconciliation metadata, and recent activity.

`health` is a compact summary for quick checks.

`health --json` emits a smaller machine-friendly payload for scripts, automation, and dashboards.

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
