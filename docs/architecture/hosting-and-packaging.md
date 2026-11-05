# Hosting and Packaging Architecture

Date: 2026-11-05

Canonical current rules for product identity, projects, the build toolchain, dependencies,
versioning, the Aspire development graph, Native AOT, NuGet packaging, installation and release
boundaries, and licensing. Transport modes and the protocol live in `server-and-transports.md`.
This document states the decided contract; which parts are implemented is operational state in
`../ROADMAP.md`.

## Identity

| Identity | Value |
|---|---|
| Repository | `opencode-dotnet/opencode-mcp` |
| Product project, assembly, and root namespace | `OpenCode.Mcp` |
| Installed command and native executable | `opencode-mcp` (`opencode-mcp.exe` on Windows) |
| Root NuGet package | `OpenCodeDotNet.Mcp` |
| Native NuGet implementation packages | `OpenCodeDotNet.Mcp.<RID>` |

The NuGet ID, the command, and the CLR names are deliberately distinct. No NuGet ID-prefix right
and no MCP Registry ownership is assumed: `io.github.opencode-dotnet/opencode-mcp` is an
unregistered listing candidate, not a registered identity.

## Projects

| Project | Role |
|---|---|
| `src/OpenCode.Mcp` | The MCP server executable; the only distributed product |
| `src/OpenCode.Mcp.ServiceDefaults` | The standard Aspire ServiceDefaults, a focused non-packable local library |
| `src/OpenCode.Mcp.AppHost` | The Aspire development orchestrator, non-packable |
| `tests/OpenCode.Mcp.Tests` | Product tests, starting from the production composition |
| `tests/OpenCode.Mcp.AppHost.Tests` | Closed-box AppHost integration tests |

Every project targets `net10.0` with C# `14.0` and analysis level `10.0`; `Directory.Build.props`
owns those values. The AppHost and test projects are not package artifacts. The AppHost sits
outside the test-exemption globs, and its reference to the product is orchestration metadata.
ServiceDefaults is not a business-model or shared-helper bucket.

A folder, helper, or project is created only for a consumer: there is no shared test library,
benchmark project, generator, or additional distributed-application resource without one.
`Directory.Build.targets` and `.globalconfig` exist only for an actual late-bound invariant or a
validated compilation-wide arbitration (`../engineering/quality-gates.md`).

## .NET SDK

`global.json` selects the .NET SDK floor with `rollForward` `latestPatch`, prereleases disabled,
and Microsoft.Testing.Platform as the test runner. The floor is the newest servicing patch of the
.NET 10 LTS feature band: a Native AOT payload embeds the runtime patch of the SDK that built it, so
a lower floor would ship without newer runtime security fixes. `latestPatch` keeps every host and CI
image in one feature band, because bands differ in tool-packaging output. A move to another feature
band, or to a newer .NET major, is an explicit change, not drift. Every receipt records the SDK
version that actually ran, and a package candidate needs one identical SDK patch across its six
receipts.

## Dependencies

- `Directory.Packages.props` is the one central package-management owner, with literal pinned
  versions; project references are versionless. Exact versions are read there, not from this
  document. A new package enters through `dotnet add package`.
- Only genuinely aligned release families share a version variable. There are no floating
  versions, no unused version variables, no transitive pinning, and no lock-file policy.
- Declare explicitly every package this repository's source uses directly; trust the transitive
  graph otherwise. A new shipped dependency is a maintainer decision recorded with its consumer; no
  package is added for a capability no scheduled work consumes.
- The MCP C# SDK is referenced only through the packages the source consumes
  (`ModelContextProtocol.Core`, `ModelContextProtocol`, `ModelContextProtocol.AspNetCore`).
- The Aspire AppHost SDK, `Aspire.Hosting.Testing`, and the Aspire CLI move together on one Aspire
  release. The AppHost MSBuild SDK is pinned in the AppHost project's SDK reference, not through a
  central version; the `Aspire.Hosting.AppHost` package that SDK adds implicitly gets neither an
  explicit reference nor a central version (NU1009).
- The ServiceDefaults packages (OpenTelemetry, `Microsoft.Extensions.Http.Resilience`,
  `Microsoft.Extensions.ServiceDiscovery`) take the versions of the pinned Aspire release's
  ServiceDefaults template. They are a separate family from the common `Microsoft.Extensions` 10.0
  packages, which are not forced to match them. Hosting APIs the framework reference supplies need
  no central version.
- OpenCode-backed MCP tools consume the published OpenCode .NET SDK package, never a project
  reference to its source; it is added with its first consumer.
- Restore uses `nuget.config` with a cleared source list and nuget.org only, unless a concrete new
  source is approved. NuGetAudit fails restore on an advisory (`../engineering/quality-gates.md`),
  and the remedy is a prompt pin bump (`../engineering/workflow.md`).

## Versioning

Root `Directory.Build.props` is the single version owner (`VersionPrefix` and `VersionSuffix`).
The product, manifest, package, and payload versions are checked projections of it, not
independent release identities. A preview version authorizes no publication.

## Aspire AppHost and ServiceDefaults

The AppHost is a C# project-based Aspire AppHost that declares one HTTP product resource, `mcp`,
through `AddProject<T>` with generated project metadata. It starts the product with
`--transport http` on a dynamically allocated loopback endpoint named `http`, in the Development
environment, with a health check on `/health`. It starts no OpenCode server process, model
provider, database, cache, container, or cloud service. Endpoints are discovered, not hardcoded.

The AppHost uses the Aspire CLI bundle (`AspireUseCliBundle=true`) with
`AspireCliInvocationMode=DnxPinned`, so an SDK-mediated launch runs the CLI version paired with the
pinned AppHost SDK, never an unversioned or newer global `aspire` command. A direct lifecycle
command also goes through a named, pinned entry point: a repository script that reads the AppHost
SDK version from the AppHost project and runs `dotnet dnx Aspire.Cli@<that version>`, so
`global.json` selects the .NET SDK. Repository tooling lives in the root `dotnet-tools.json`; no
duplicate `.config` manifest exists.

ServiceDefaults keeps the standard Aspire behaviour: OpenTelemetry logs with formatted messages and
scopes; ASP.NET Core, HTTP client, and runtime metrics; application, ASP.NET Core, and HTTP client
traces that exclude the health endpoints; OTLP export when an endpoint is configured; a `self`
liveness check; service discovery; and factory-created `HttpClient` instances with the standard
resilience handler and service discovery. `/health` and `/alive` are mapped in Development only.
The adaptation changes names, namespaces, central package management, and real Native AOT
necessities, never the behaviour for brevity. Registration stays wiring; behaviour and external I/O
keep named, testable owners.

`OpenCode.Mcp.ServiceDefaults.ServiceDefaultsExtensions` owns `AddServiceDefaults`,
`ConfigureOpenTelemetry`, `AddDefaultHealthChecks`, and `MapDefaultEndpoints`. The non-packable
shared project inherits the root target framework, declares `IsAspireSharedProject=true`, and
references `Microsoft.AspNetCore.App`. The adapted template retains its named wiring methods and
active registrations, adds public null guards and XML documentation, and removes the inactive
discovery-scheme, gRPC and cloud-exporter hooks. Its derivation notice is in
`THIRD-PARTY-NOTICES.md`.

- The standard resilience handler applies to factory-created clients only. It does not inject a
  handler into the OpenCode .NET SDK's own transport, and it authorizes no blind retry of an unsafe
  OpenCode write.
- The stdio logging and endpoint rules (`server-and-transports.md`) are mode boundaries, not a
  reason to remove a standard default.
- AppHost tests disable the dashboard. The development dashboard can persist sensitive telemetry;
  its store, its tokens, and a user's `.aspire` directory are never collected as failure artifacts.
- AppHost health proves that the MCP server process started, not that an OpenCode server process
  or a model provider is available.

## Native AOT

The native matrix is:

```text
win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64
```

Linux means glibc; musl is not in the matrix, and there is no managed `.any` fallback.

- Native AOT is scoped to the product: `PublishAot` and `InvariantGlobalization=true` are set in the
  product project for every RID, never on the AppHost, ServiceDefaults, or test projects.
- Trim and AOT warnings are fatal. `IlcTreatWarningsAsErrors` inherits the wall's
  `TreatWarningsAsErrors`; `ILLinkTreatWarningsAsErrors` does not govern an AOT publish. The native
  gate asserts that `IlcTreatWarningsAsErrors` resolves to true and also scans the publish output
  for any `IL2xxx` or `IL3xxx` warning code, so a property drift cannot hide one. There is no
  assembly rooting, no copied dependency DLL, and no suppression.
- Invariant globalization removes the ICU dependency that would otherwise stop a Linux payload from
  starting on a system without `libicu`; the product has no culture-sensitive behaviour. In this
  mode, constructing a named culture throws; this is rechecked when OpenCode-backed tools arrive.
- A native claim rests on executing the published executable on a compatible target, in both
  transport modes; a property, library metadata, a clean JIT build, a managed DLL, `dotnet run`, or
  the AppHost does not satisfy it. Cross-OS compilation is not supported, and a permitted
  cross-architecture compilation does not prove execution.

### Native build and runtime independence

Each RID builds and runs on a matching hosted runner:

| RID | Runner |
|---|---|
| `win-x64` | `windows-2025` |
| `win-arm64` | `windows-11-arm` |
| `linux-x64` | `ubuntu-24.04` |
| `linux-arm64` | `ubuntu-24.04-arm` |
| `osx-x64` | `macos-15-intel` |
| `osx-arm64` | `macos-26` |

Every native job checks the actual operating system and architecture against its RID before it
compiles.

Both Linux payloads build inside a product-owned builder image based on Ubuntu 22.04, built natively
for each architecture and pinned by digest. It carries the Native AOT prerequisites (clang and the
zlib development files), the tools the CI steps need (`curl`, `ca-certificates`, `git`), and a
pinned PowerShell LTS release; a job installs only the .NET SDK, from `global.json`. The image is CI
build infrastructure, not a product release artifact. The supported glibc floor is the highest
`GLIBC_` symbol version the payload references, measured with `objdump -T`, not the image's own
glibc version.

Runtime independence has its own proof. A Linux payload runs in a clean container that has no .NET
SDK or shared runtime. A Windows or macOS payload runs on its matching runner and passes a
native-image inspection: the payload holds no managed assemblies, runtime configuration, dependency
manifest, or host and runtime libraries (`hostfxr`, `hostpolicy`, `coreclr`), and the executable
exports the Native AOT `DotNetRuntimeDebugHeader` symbol. Import lists alone cannot tell a Native
AOT image from a framework-dependent apphost and are supplementary only.

### Windows prerequisites

A Native AOT publish on Windows links with the platform linker, so the machine needs the Visual
Studio Build Tools workload "Desktop development with C++" (MSVC and a Windows SDK). Without it the
publish fails with "Platform linker not found".

With the Visual Studio 2026 Build Tools, ILCompiler finds the linker through `VsDevCmd.bat`, which
calls `vswhere.exe` by its bare name from the Visual Studio Installer folder. A publish from a shell
that sets `NoDefaultCurrentDirectoryInExePath` and has no Installer folder on `PATH` fails with
"'vswhere.exe' is not recognized" inside the linker command and then MSB3073: put
`C:\Program Files (x86)\Microsoft Visual Studio\Installer` on that process's `PATH` rather than
clearing the variable. The native verification script checks both prerequisites and fails with the
name of the missing one.

## NuGet MCP tool package

- The product packs as a .NET tool with the MCP server package type: `PackAsTool=true`,
  `PackageType=McpServer`, the SDK-added `DotnetTool` type, the command `opencode-mcp`, and
  `.mcp/server.json` at the package root.
- `ToolPackageRuntimeIdentifiers` lists exactly the six native RIDs. The SDK generates one
  implementation package per RID, `OpenCodeDotNet.Mcp.<RID>` of type `DotnetToolRidPackage`, at the
  same version. The root package never maps an absent child, and no `any` mapping exists.
- Each child packs the verified native payload of its RID without recompiling it.
- `.mcp/server.json` declares stdio acquisition through `dnx` with explicit
  `--transport stdio` package arguments. `dnx` receives `-v minimal` before `--`, both there and in
  the README's manual launch command: on macOS a cold acquisition at default verbosity writes a
  signature-verification notice to standard output before the server starts, which would corrupt
  the protocol stream. That line is never filtered out to pass a test.
- Native execution needs no installed .NET runtime; `dnx` acquisition still needs the .NET SDK.
  Direct native archives are the SDK-free route.
- The packed README stays free of repository links unless it is pinned to the packed commit. No
  `McpName` property and no unverified repository URL is set.

### Payload, candidate, and acquisition

- One verified payload per RID feeds the NuGet child, the direct archive, and later installer
  wrappers. Payload equality covers the complete declared file set by normalized path and digest:
  the executable, required assets, notices, and payload metadata. An executable hash alone is not
  equality. Native debug symbols, build receipts, and channel wrappers stay outside the payload.
- A package candidate is one real source commit with no uncommitted source or build-input change,
  consistent recorded common build inputs, and each RID's own toolchain, environment, and payload
  evidence. Equal version labels do not prove equal sources. The candidate commit needs separate
  maintainer approval (`../engineering/workflow.md`). Builds during development are development
  evidence, not candidate evidence. A source or build-input change makes a new candidate that is
  verified again; an earlier artifact or receipt is never relabeled as produced from a later
  commit.
- Acquisition tests use a controlled, complete local feed and fresh test-owned home, configuration,
  cache, and tool directories, with the .NET SDK already available. They prove package selection and
  packaged startup without personal settings, earlier packages, or development output. They do not
  require external network denial, do not promise offline installation, and never install globally
  or publish.

## Installation and release boundaries

The product target includes PowerShell and shell installers, WinGet, Homebrew, suitable deb and rpm
packages, a Windows MSI, and a macOS pkg. Archive-only delivery does not close that target. Each
surface is a separate delivery increment after native and package proof, never a placeholder.

- Installers use version-pinned immutable assets, detect the OS and architecture, verify a trusted
  digest, replace atomically after staging, default to user scope for scripts, own their `PATH`
  changes, detect collisions, upgrade by version, and uninstall without touching unrelated
  commands, configuration, or data. System packages and some macOS installer scopes need documented
  privilege. No installer silently configures an MCP client or starts a daemon.
- Channel versions are projections of one release identity with deterministic, monotonic upgrade
  ordering. Before installer authoring, the MSI tool and its terms are approved and the platform
  version conversion is fixed and tested.
- Signing and notarization, registry namespaces and prefix authority, real download URLs,
  package-manager submissions, and maintained signed APT or DNF repositories are separately
  authorized release operations. A checksum does not authenticate a publisher; final hashes follow
  every byte-changing signing step. Native debug symbols do not enter a managed symbol package.

## Licensing

The repository uses the MIT license in `LICENSE`; `THIRD-PARTY-NOTICES.md` carries the notices of
derived content. A native payload carries `LICENSE` and the notices of the code it embeds, derived
from the resolved publish graph, and a test compares that notice set with the graph.
