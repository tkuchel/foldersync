# Security Policy

## Supported Versions

FolderSync is a pre-1.x project in spirit: only the latest minor line receives
security fixes. Older tagged versions will not receive backports.

| Version | Supported          |
| ------- | ------------------ |
| 1.0.x   | :white_check_mark: |
| < 1.0   | :x:                |

## Reporting a Vulnerability

If you discover a security issue in FolderSync, **please do not open a public
GitHub issue**. Instead, report it privately so it can be triaged and fixed
before disclosure.

Please use one of the following channels:

1. **GitHub Security Advisories** (preferred): open a private report via
   [Security → Report a vulnerability](https://github.com/tkuchel/foldersync/security/advisories/new).
2. **Direct contact**: open a placeholder public issue titled
   "Security contact request" *without any vulnerability details* and a
   maintainer will reach out privately.

When reporting, please include:

- A clear description of the issue and its potential impact.
- A minimal reproduction or proof-of-concept if available.
- The affected version (`foldersync --version` or the value of `Version` in
  `Directory.Build.props`).
- Any relevant configuration excerpts (redact paths and secrets).

## What to Expect

- Acknowledgement within **5 business days**.
- A triage decision and rough remediation plan within **10 business days**.
- Coordinated disclosure: credit in the release notes and `CHANGELOG.md` once a
  fix is released, unless you request otherwise.

## Scope

In scope for this policy:

- Data loss, corruption, or accidental deletion outside the configured
  destination root.
- Privilege escalation via the installed Windows service.
- Path traversal, reparse-point escape, or archive-root bypass.
- Authentication or authorization flaws in the local dashboard.
- Credential leakage in logs, health snapshots, or control files.
- Dependency vulnerabilities shipped in packaged release artifacts.

Out of scope:

- Issues that require pre-existing administrative access to the machine
  running FolderSync.
- Social engineering of maintainers or operators.
- Theoretical issues with no demonstrable impact on a default, supported
  configuration.

Thank you for helping keep FolderSync and its users safe.
