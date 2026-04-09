# Contributing to FolderSync

Thanks for your interest in contributing! This document describes the workflow,
expectations, and conventions used in this repository.

By participating, you agree to abide by the [Code of Conduct](./CODE_OF_CONDUCT.md).

## Before You Start

- For **bug reports** and **feature requests**, please open an issue using the
  provided templates rather than sending a direct change.
- For **security issues**, follow the process in [SECURITY.md](./SECURITY.md)
  and do *not* open a public issue.
- For anything non-trivial, please discuss the approach in an issue first so
  nobody duplicates work.

## Prerequisites

- Windows 10 21H2 or newer (the tray companion targets
  `net10.0-windows10.0.19041.0`).
- [.NET SDK 10.0.x](https://dotnet.microsoft.com/download)
- PowerShell 7+ for the deployment and release-validation scripts.
- Administrator rights if you plan to exercise the `install`, `uninstall`, or
  `Deploy-Local.ps1` code paths.

## Build and Test

The full build/test loop mirrors CI:

```powershell
dotnet restore FolderSync.slnx
dotnet build   FolderSync.slnx --no-restore --configuration Release
dotnet test    FolderSync.slnx --no-build   --configuration Release --nologo
```

Publish smoke tests (optional but recommended before pushing):

```powershell
dotnet publish src\FolderSync\FolderSync.csproj --no-build --configuration Release -o $env:TEMP\foldersync-publish
dotnet publish src\FolderSync.Tray\FolderSync.Tray.csproj --no-build --configuration Release -o $env:TEMP\foldersync-tray-publish
```

Local deployment rehearsal (no service changes):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Deploy-Local.ps1 -WhatIf
```

## Branching Model

- `main` — stable, tested, deployable. All tagged releases are cut from `main`.
- `develop` — integration branch for ongoing feature work and experiments.

New feature work should:

1. Branch from `develop`.
2. Open a pull request targeting `develop`.
3. Be promoted to `main` only after it has been validated on `develop`.

Operational fixes that must ship quickly can land directly on `main` and should
be merged or replayed back into `develop` so both branches stay aligned.

## Commit Messages

- Use imperative present tense: *Fix watcher overflow*, not *Fixed* or *Fixes*.
- Keep the subject line under ~72 characters.
- Add a body when the *why* is not obvious from the subject.
- Reference issues with `#123` where applicable.

## Pull Request Checklist

Before requesting review, please confirm:

- [ ] `dotnet test FolderSync.slnx --nologo` passes locally.
- [ ] New behavior is covered by tests (unit or integration as appropriate).
- [ ] Public operator-facing changes are reflected in `README.md` and, where
      relevant, `TROUBLESHOOTING.md` and `RELEASE_CHECKLIST.md`.
- [ ] `CHANGELOG.md` has an `[Unreleased]` entry describing the change from the
      operator's perspective.
- [ ] No secrets, local paths, or machine-specific configuration are included.

## Code Style

- Follow existing patterns in the module you are editing.
- Nullable reference types are enabled project-wide — prefer fixing the warning
  over suppressing it.
- Prefer `sealed` classes and readonly fields unless inheritance or mutation is
  required.
- Keep services constructor-injected and testable.
- Use the `ILogger<T>` abstraction; do not add ad-hoc `Console.WriteLine`
  statements to service code.

## Releases

Release mechanics live in [`RELEASE_CHECKLIST.md`](./RELEASE_CHECKLIST.md). In
short:

1. Bump `Version` in `Directory.Build.props`.
2. Move the `[Unreleased]` block in `CHANGELOG.md` under a new version heading.
3. Commit, push, and tag `vX.Y.Z` from `main`.
4. The `Release` workflow validates the tag matches the declared version,
   builds, tests, publishes, packages, validates, smoke-tests, and attaches
   both service and tray zips to a GitHub release.

## Questions

If any of this is unclear, open an issue labelled `question` and we will try to
help. Contributions of all sizes are welcome, from typo fixes to new features.
