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

`status --json` emits the full structured service/runtime report, including install/config/log paths, runtime counters, reconciliation metadata, and recent activity.

`health` is a compact summary for quick checks.

`health --json` emits a smaller machine-friendly payload for scripts, automation, and dashboards.

`pause` and `resume` control the running service through a persisted control file. When you pass `--profile`, only that profile pauses and the rest of the service keeps running.

`dashboard` starts a lightweight local web dashboard on `http://127.0.0.1:8941/` by default and opens it in your browser.
The dashboard now supports:

- filtering by profile name
- pause and resume actions for the whole service
- pause and resume actions per profile
- per-profile recent activity history
- one-click profile reconciliation from the installed service

`FolderSync.Tray` is a lightweight Windows tray companion for the installed service. The first version provides:

- live tray status from the persisted health snapshot
- alert balloons for service/profile warnings
- quick pause, resume, and reconcile actions
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

- The project version is set explicitly in [FolderSync.csproj](/T:/repos/foldersync/src/FolderSync/FolderSync.csproj).
- `Version` and `InformationalVersion` should match the intended release tag, for example `1.0.1`.
- `AssemblyVersion` and `FileVersion` should stay in four-part form, for example `1.0.1.0`.
- When cutting a release, bump the project version first, build/test, then create the matching Git tag.

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

Current progress on `develop`:

- profile-level pause and resume is implemented
- dashboard filtering and pause/resume actions are implemented
- Slack/Teams notification payload templates are implemented
- dashboard profile activity history and one-click reconcile actions are implemented

## Local Deployment

For the installed service in `C:\FolderSync`, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Deploy-Local.ps1
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
