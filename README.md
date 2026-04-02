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
foldersync install
foldersync uninstall
foldersync status
```

## Configuration

The application reads configuration from:

- [appsettings.json](/T:/repos/foldersync/src/FolderSync/appsettings.json)
- An optional custom file passed with `--config`

The checked-in `appsettings.json` is a safe example. Replace the example paths with your own local folders before running.

## Safe Setup

1. Update the source and destination paths in `src/FolderSync/appsettings.json`.
2. Start with `"DryRun": true` while validating behavior.
3. Run `foldersync reconcile --config <your-config>` to test a one-shot sync.
4. Review the destination and logs.
5. Only enable deletion sync after you are confident the mapping is correct.

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
