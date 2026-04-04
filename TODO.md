# FolderSync TODO

This is the short backlog for the next improvement pass after the current audit work.

## High Priority

- Consider adding a direct link from the tray troubleshooting dialog into the dashboard troubleshooting panel.

## Medium Priority

- Consider making stale queued-request pruning configurable instead of using a fixed 24-hour threshold.
- Consider adding a `--strict` or `--skip-artifact-checks` mode to `validate-deploy` for faster local rehearsals versus full release-style validation.

## Nice To Have

- Split the embedded dashboard HTML/JS/CSS into easier-to-test components or embedded resource files if the UI keeps growing.
- Add a simple retention or cleanup policy for generated local artifacts and old release validation outputs.
- Add a release verification smoke-test script that exercises install-time expectations beyond zip contents, such as command help or config validation.
