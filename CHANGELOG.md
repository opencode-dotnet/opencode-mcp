# Changelog

## [Unreleased]

## [0.1.0-preview.3] - 2026-11-16

### Fixed

- Windows installation recovers uninstalls interrupted after moving aside the payload, part-way through deleting it, or after deleting it but before removing its records, and removes verified leftover staging, previous, temporary and rolled-back removing directories on the next run.
- Windows installation stops at extraction of a corrupt release archive with the staging failure diagnostic instead of printing raw extraction errors.
- A Windows PATH update that fails before changing the registry now reports only that failure, not a cleanup failure as well.
- Windows installation checks platform support and prerequisites before resolving installation and asset paths.
- Windows install rollback and cleanup get a fresh sharing-violation retry budget, and command access checks detect a command that starts during the check.

## [0.1.0-preview.2] - 2026-11-15

### Security

- The Aspire AppHost SDK, `Aspire.Hosting.Testing` and the Aspire CLI move from 13.6.4 to 13.6.5, which fixes [GHSA-f7vh-h5g3-7jrh](https://github.com/advisories/GHSA-f7vh-h5g3-7jrh) (CVE-2026-120006, high: `Aspire.Hosting` and `Aspire.Hosting.AppHost`) and [GHSA-4g86-54fm-h9h8](https://github.com/advisories/GHSA-4g86-54fm-h9h8) (CVE-2026-118740, critical: `Aspire.Dashboard.Sdk.*`). They are development and test dependencies of the AppHost; the server and its packages do not contain them.

### Added

- Install scripts for Windows (PowerShell) and for Linux and macOS (POSIX shell) that verify the release archive before installing, and uninstall only what they installed.
- Release assets: one native archive per runtime, both install scripts, and a `SHA256SUMS` file, all from the verified candidate build.
- A manual release workflow that promotes one verified candidate, unchanged, to GitHub Packages, NuGet.org and a GitHub Release, and a channel verification that installs the published bytes on every supported runtime.
- Local NuGet MCP tool packages for Windows, Linux and macOS on x64 and Arm64, built from verified native payloads.
- A complete local package feed with MCP metadata and payload-integrity inspection.
- Isolated local-feed acquisition through `dotnet dnx` and an owned tool-path installation, with packaged stdio and HTTP startup checks for every supported native RID.
