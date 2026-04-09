# FolderSync Release Checklist

## Before You Tag

1. Confirm the intended version and update [Directory.Build.props](./Directory.Build.props).
2. Review [README.md](./README.md), [TODO.md](./TODO.md), and deployment notes for anything that changed materially in the release.
3. Move the `[Unreleased]` block in [CHANGELOG.md](./CHANGELOG.md) under the new version heading. The release workflow extracts the matching `## [x.y.z]` section and uses it as the GitHub release body, so anything you put there is what operators will see.
4. Run:

```powershell
dotnet build FolderSync.slnx --nologo
dotnet test  FolderSync.slnx --nologo
```

5. If the release is intended for the installed Windows service, run a local deployment validation:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Deploy-Local.ps1 -WhatIf
dotnet run --project src\FolderSync -- validate-deploy --target-dir C:\FolderSync --skip-tests
```

6. (Optional) Rehearse the release workflow without publishing anything by triggering it manually with `dry_run: true` from the Actions tab. This runs the full build, validate, smoke-test, SBOM, and checksum pipeline and uploads the artifacts without creating a GitHub release or attesting provenance.

## Before You Publish

1. Verify the working tree only contains intentional release changes.
2. Push the branch and confirm GitHub Actions CI is green.
3. Confirm CodeQL is green on the release commit.
4. Create and push the matching Git tag for the version in `Directory.Build.props`.
5. Expect the release workflow to fail if the tag and `Directory.Build.props` version do not match exactly.

## After You Tag

1. Confirm the release commit is the one you intended to ship.
2. Confirm the `Release` GitHub Actions workflow completed successfully and attached the following assets to the GitHub release:
   - `foldersync-service-<tag>.zip`
   - `foldersync-tray-<tag>.zip`
   - `foldersync-<tag>-SHA256SUMS.txt`
   - `foldersync-service-<tag>-sbom.cdx.json`
   - `foldersync-tray-<tag>-sbom.cdx.json`
3. Confirm a build-provenance attestation was created on the release commit (Actions → attest-build-provenance step, or the Attestations tab on the repo). This replaces traditional code signing for verifying authenticity of the zips. **Note:** attestations only run when the repository is public. On private repos the step is skipped and operators fall back to verifying the SHA256 checksums.
4. Download or inspect the published service and tray zip files from the GitHub release.
5. Verify the SHA256 checksums match:

```powershell
Get-Content .\foldersync-<tag>-SHA256SUMS.txt
(Get-FileHash .\foldersync-service-<tag>.zip -Algorithm SHA256).Hash.ToLower()
(Get-FileHash .\foldersync-tray-<tag>.zip    -Algorithm SHA256).Hash.ToLower()
```

6. (Optional) Verify the build provenance attestation with:

```powershell
gh attestation verify .\foldersync-service-<tag>.zip --owner tkuchel
gh attestation verify .\foldersync-tray-<tag>.zip    --owner tkuchel
```

7. If you are validating locally, run [Validate-ReleaseArtifacts.ps1](./scripts/Validate-ReleaseArtifacts.ps1) against the produced zip files.
8. If you are validating locally, run [Smoke-Test-ReleaseArtifacts.ps1](./scripts/Smoke-Test-ReleaseArtifacts.ps1) against the produced zip files.
9. Verify published service and tray executables start successfully in a clean environment.
10. Smoke-test the operator path:

```powershell
foldersync status --verbose
foldersync health
```

11. If deploying locally, confirm:
    - `C:\FolderSync\foldersync.exe` exists
    - `C:\FolderSync\Tray\foldersync-tray.exe` exists
    - `C:\FolderSync\foldersync-health.json` and `C:\FolderSync\foldersync-control.json` are readable after startup
