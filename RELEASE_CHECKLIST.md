# FolderSync Release Checklist

## Before You Tag

1. Confirm the intended version and update [Directory.Build.props](C:/Users/terre/cowork-workspace/foldersync/Directory.Build.props).
2. Review [README.md](C:/Users/terre/cowork-workspace/foldersync/README.md), [TODO.md](C:/Users/terre/cowork-workspace/foldersync/TODO.md), and deployment notes for anything that changed materially in the release.
3. Run:

```powershell
dotnet build FolderSync.slnx --nologo
dotnet test FolderSync.slnx --nologo
```

4. If the release is intended for the installed Windows service, run a local deployment validation:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Deploy-Local.ps1 -WhatIf
```

## Before You Publish

1. Verify the working tree only contains intentional release changes.
2. Push the branch and confirm GitHub Actions CI is green.
3. Create the matching Git tag for the version in `Directory.Build.props`.

## After You Tag

1. Confirm the release commit is the one you intended to ship.
2. Verify published service and tray executables start successfully in a clean environment.
3. Smoke-test the operator path:

```powershell
foldersync status --verbose
foldersync health
```

4. If deploying locally, confirm:
   - `C:\FolderSync\foldersync.exe` exists
   - `C:\FolderSync\Tray\foldersync-tray.exe` exists
   - `C:\FolderSync\foldersync-health.json` and `C:\FolderSync\foldersync-control.json` are readable after startup
