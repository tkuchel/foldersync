# FolderSync TODO

This is the short backlog for the next improvement pass after the current audit work.

## High Priority

- Distinguish per-profile reconcile states more explicitly across operator surfaces: `queued`, `running`, `idle`, and `service unavailable`.
- Add integration coverage for watcher overflow recovery under sustained channel pressure.
- Add integration coverage for cross-process operator flows, especially pause/resume plus queued reconcile interactions against the persisted control file.

## Medium Priority

- Deduplicate or coalesce repeated queued reconcile requests for the same profile when an operator clicks multiple times in quick succession.
- Surface the currently running reconciliation trigger in the dashboard and tray, not just historical completion data.
- Add a tagged-release workflow that produces service and tray artifacts from GitHub Actions.

## Nice To Have

- Split the embedded dashboard HTML/JS/CSS into easier-to-test components or embedded resource files if the UI keeps growing.
- Add a small operator troubleshooting guide for common states like watcher overflow, service unavailable, and repeated failures.
- Consider a dry-run validation command for local deployment that exercises publish + config validation without touching the installed service.
