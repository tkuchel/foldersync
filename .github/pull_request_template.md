<!--
Thanks for sending a pull request.

Please make sure:
- The target branch is correct (`develop` for features, `main` for operational fixes).
- You have read CONTRIBUTING.md.
- Secrets, local paths, and machine-specific configuration are not included.
-->

## Summary

<!-- What does this PR do, and why? Keep it focused on the operator-visible change. -->

## Linked Issues

<!-- e.g. Closes #123, Refs #456 -->

## Type of Change

- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Breaking change (fix or feature that would cause existing behavior to change)
- [ ] Documentation or CI only (no code behavior change)
- [ ] Refactor or internal cleanup (no functional change)

## Test Evidence

<!--
Describe how this was tested. Paste relevant command output if useful.
At minimum: `dotnet test FolderSync.slnx --nologo`
-->

## Safety Review

<!--
For any change touching sync, reconcile, deletion, retention, archive, or
persisted state files, confirm the following:
-->

- [ ] This change does not weaken any deletion safety default.
- [ ] Destination-root constraints and reparse-point checks are unchanged or still enforced.
- [ ] `foldersync-health.json` and `foldersync-control.json` shapes remain backward compatible, or a migration is documented.

## Checklist

- [ ] `dotnet build FolderSync.slnx --configuration Release` succeeds locally.
- [ ] `dotnet test  FolderSync.slnx --configuration Release --nologo` passes locally.
- [ ] New behavior is covered by unit or integration tests.
- [ ] `README.md`, `TROUBLESHOOTING.md`, and/or `RELEASE_CHECKLIST.md` are updated where relevant.
- [ ] `CHANGELOG.md` has an `[Unreleased]` entry describing the operator-visible change.
- [ ] No secrets, local paths, or machine-specific configuration are included.
