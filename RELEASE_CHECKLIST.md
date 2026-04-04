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
dotnet run --project src\FolderSync -- validate-deploy --target-dir C:\FolderSync --skip-tests
```

## Before You Publish

1. Verify the working tree only contains intentional release changes.
2. Push the branch and confirm GitHub Actions CI is green.
3. Create and push the matching Git tag for the version in `Directory.Build.props`.
4. Expect the release workflow to fail if the tag and `Directory.Build.props` version do not match exactly.

## After You Tag

1. Confirm the release commit is the one you intended to ship.
2. Confirm the `Release` GitHub Actions workflow completed successfully and attached both zip assets to the GitHub release.
3. Download or inspect the published service and tray zip files from the GitHub release.
4. If you are validating locally, run [Validate-ReleaseArtifacts.ps1](C:/Users/terre/cowork-workspace/foldersync/scripts/Validate-ReleaseArtifacts.ps1) against the produced zip files.
5. If you are validating locally, run [Smoke-Test-ReleaseArtifacts.ps1](C:/Users/terre/cowork-workspace/foldersync/scripts/Smoke-Test-ReleaseArtifacts.ps1) against the produced zip files.
6. Verify published service and tray executables start successfully in a clean environment.
7. Smoke-test the operator path:

```powershell
foldersync status --verbose
foldersync health
```

8. If deploying locally, confirm:
   - `C:\FolderSync\foldersync.exe` exists
   - `C:\FolderSync\Tray\foldersync-tray.exe` exists
   - `C:\FolderSync\foldersync-health.json` and `C:\FolderSync\foldersync-control.json` are readable after startup
