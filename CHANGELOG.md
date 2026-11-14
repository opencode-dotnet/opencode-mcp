# Changelog

## [Unreleased]

### Security

- The Aspire AppHost SDK, `Aspire.Hosting.Testing` and the Aspire CLI move from 13.6.4 to 13.6.5, which fixes [GHSA-f7vh-h5g3-7jrh](https://github.com/advisories/GHSA-f7vh-h5g3-7jrh) (CVE-2026-120006, high: `Aspire.Hosting` and `Aspire.Hosting.AppHost`) and [GHSA-4g86-54fm-h9h8](https://github.com/advisories/GHSA-4g86-54fm-h9h8) (CVE-2026-118740, critical: `Aspire.Dashboard.Sdk.*`). They are development and test dependencies of the AppHost; the server and its packages do not contain them.

### Added

- Local NuGet MCP tool packages for Windows, Linux and macOS on x64 and Arm64, built from verified native payloads.
- A complete local package feed with MCP metadata and payload-integrity inspection.
- Isolated local-feed acquisition through `dotnet dnx` and an owned tool-path installation, with packaged stdio and HTTP startup checks for every supported native RID.
