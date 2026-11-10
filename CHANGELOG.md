# Changelog

## [Unreleased]

### Added

- Local NuGet MCP tool packages for Windows, Linux and macOS on x64 and Arm64, built from verified native payloads.
- A complete local package feed with MCP metadata and payload-integrity inspection.
- Isolated local-feed acquisition through `dotnet dnx` and an owned tool-path installation, with packaged stdio and HTTP startup checks for every supported native RID.
