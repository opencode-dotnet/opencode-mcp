# Hosting and Packaging Architecture

Date: 2026-11-06

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
| `tools/OpenCode.Mcp.Tools` | Non-packable native verification and staging collaborators |
| `tests/OpenCode.Mcp.Tools.Tests` | In-process tool contract tests; no native compilation |

Every project targets `net10.0` with C# `14.0` and analysis level `10.0`; `Directory.Build.props`
owns those values. The AppHost and test projects are not package artifacts. The AppHost sits
outside the test-exemption globs, and its reference to the product is orchestration metadata.
ServiceDefaults is not a business-model or shared-helper bucket.

The product uses `Microsoft.NET.Sdk.Web` with explicit `OutputType=Exe` and `IsPackable=false`.
The Web SDK supplies `Microsoft.AspNetCore.App` implicitly. Its top-level entry point delegates to
the launcher; HTTP uses a slim web builder and stdio a generic builder
(`server-and-transports.md`). The [README's source-launch commands](../../README.md#run-from-source)
run a managed development build that needs .NET and establishes no native, package, or installation
claim.

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

The AppHost is a C# project-based Aspire AppHost that declares one HTTP product project resource, `mcp`,
through `AddProject<T>` with generated project metadata. It starts the product with
`--transport http` on a dynamically allocated loopback endpoint named `http`, in the Development
environment, with a health check on `/health`. Its `http` endpoint is explicitly proxy-less, with
numeric target host `127.0.0.1` and `ASPNETCORE_URLS` bound to the allocated target port. The discovered
authority therefore reaches the product's Host and Origin checks directly, without proxy Host
rewriting or a test-only Host override. Forwarded-header injection is disabled; that annotation
governs publishing, not Origin validation. Aspire also adds the hidden, explicit-start
`mcp-rebuilder` infrastructure resource; the tests require it never to start.
The AppHost starts no OpenCode server process, model provider, database, cache, container, or cloud
service. Endpoints are discovered, not hardcoded.

The AppHost uses the Aspire CLI bundle (`AspireUseCliBundle=true`) with
`AspireCliInvocationMode=DnxPinned`, so an SDK-mediated launch runs the CLI version paired with the
pinned AppHost SDK, never an unversioned or newer global `aspire` command. A direct lifecycle
command also goes through `dotnet tool run aspire`, pinned by the `Aspire.Cli` entry in the root
`dotnet-tools.json`, so `global.json` selects the .NET SDK. Repository tooling lives in that
manifest; no
duplicate `.config` manifest exists.

Run `dotnet tool restore` before using the local tool. `DnxPinned` pins the CLI, not an independently
selected bundle. Build-time bundle
discovery can select an existing newer bundle; if no bundle exists, the SDK can acquire the pinned
CLI through DNX and run its setup before any lifecycle command runs. Set an owned `ASPIRE_HOME`
before building and keep it through the tests: generated AppHost assembly metadata retains the
resolved DCP and dashboard paths. Record the bundle marker and the DCP binary's own version, which
is not the Aspire release number. Restore and DNX can populate the shared NuGet package and HTTP
caches. A CLI package can also extract a bundle beside its cached binary.

`ASPIRE_HOME` is not complete machine-state isolation. Hosting uses the real profile's
`.aspire/cli/bch` for its backchannel socket and `.aspire/dev-certs` for its developer-certificate
cache; socket discovery can prune matching stale entries. Hosting can inspect the CurrentUser
certificate store, and a CLI launch can generate an absent developer certificate or enter a
platform-specific trust flow. Never repoint `HOME`, `USERPROFILE`, or `DOTNET_CLI_HOME` to conceal
these effects, and never delete or rewrite the real `.aspire` directory. Tests isolate the product
child's home/config/cache/temp paths, not the hosting test process's profile.

The CLI's `--isolated` switch randomizes development ports; it does not isolate certificates,
profile state or caches. No `UserSecretsId` is added. For an owned lifecycle check, place dashboard
data under the owned Aspire home with `ASPIRE_DASHBOARD_DATA_DIRECTORY`; CLI logs and session
metadata remain there too. The CLI can create an ignored repository-local `aspire.config.json`.
The dashboard's SQLite store and authentication-related state are sensitive and are never uploaded.

The AppHost testing builder invokes the actual AppHost entry point inside the test process. DCP
launches the product as a separate process. To give that process an empty owned working directory,
`OwnedAppHost` attaches an executable annotation for the built Release product selected from the
generated project metadata and the inherited target framework. It preserves the composition's
arguments, endpoints, environment and health registration; it replaces no services in the product.
The CLI lifecycle check separately exercises the normal project-launch path. The test dependency
graph deliberately contains Aspire's transitive `ModelContextProtocol` 1.4.1 beside directly
selected Core 2.3.1; no product reference or transitive pin changes that graph. The separate product
process keeps its own MCP dependency graph.

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

`packaging/native-manifest.json` owns the ordered native RID/runner mapping and the digest-qualified
Linux builder and clean runtime image references. The tool rejects unknown manifest fields,
duplicate or unknown-shape RIDs, missing runners, and image references without SHA-256 digests. A
committed-manifest test pins the approved ordered mapping. Non-Linux RIDs use neither
image. Packaging and acquisition consume this manifest rather than maintaining separate RID lists.

Linux means glibc; musl is not in the matrix, and there is no managed `.any` fallback.

- Native AOT is scoped to the product: `PublishAot` and `InvariantGlobalization=true` are set in the
  product project for every RID, never on the AppHost, ServiceDefaults, or test projects.
- Trim and AOT warnings are fatal. `IlcTreatWarningsAsErrors` inherits the wall's
  `TreatWarningsAsErrors`; `ILLinkTreatWarningsAsErrors` does not govern an AOT publish. The native
  gate asserts that `IlcTreatWarningsAsErrors` resolves to true and also scans the publish output
  for any `IL2xxx` or `IL3xxx` warning code, so a property drift cannot hide one. There is no
  assembly rooting, no copied dependency DLL, and no suppression except the SDK-owned `IL2121`
  exemption for redundant suppression warnings, which do not report unsafe code.
- Invariant globalization removes the ICU dependency that would otherwise stop a Linux payload from
  starting on a system without `libicu`; the product has no culture-sensitive behaviour. In this
  mode, constructing a named culture throws; this is rechecked when OpenCode-backed tools arrive.
- A native claim rests on executing the published executable on a compatible target, in both
  transport modes; a property, library metadata, a clean JIT build, a managed DLL, `dotnet run`, or
  the AppHost does not satisfy it. Cross-OS compilation is not supported, and a permitted
  cross-architecture compilation does not prove execution.

### Native build and runtime independence

Each RID builds and runs on the matching hosted runner declared in `packaging/native-manifest.json`.

Every native job checks the actual operating system and architecture against its RID before it
compiles, including process and SDK architecture so emulation cannot stand in for matching-host
execution.

### Verification tool and evidence

`dotnet run tools/native.cs -- <subcommand> ...` is the lowercase file-based entry over the
solution's tooling library. Its top-level entry owns Ctrl+C through a semaphore and awaited
`CancelAsync`; the signal handler does not run cancellation callbacks. The entry uses the library's
root namespace and disables the file-app SDK's default AOT setting: this is managed build tooling,
not a second distributed product. The entry is compiled separately from the solution.

- `verify --rid <RID> [--output <dir>]` guards the host, checks prerequisites, builds the owning
  test runner, restores the RID-specific product, and queries its effective properties. It requires
  `PublishAot=true`, `InvariantGlobalization=true`, `IlcTreatWarningsAsErrors=true`, and
  `DebuggerSupport` not false. Neither trim/AOT suppression property may be true, and `NoWarn` may
  contain no `IL2xxx` or `IL3xxx` code except exactly `IL2121`, the pinned SDK's default for
  redundant suppression warnings, which do not report unsafe code. The receipt preserves the
  effective `NoWarn` value. It removes its owned publish and payload directories before
  publishing. Child build, restore, property-query and publish commands disable node reuse and
  shared compilation. It captures both publish streams, preserves the exit result, scans
  for trim/AOT diagnostics even on exit zero, inspects the native image, and runs `ExecutableTests`
  on that exact published path in both transport modes.
- `stage --rid <RID> --publish-directory <dir>` requires matching successful verification and an
  unchanged executable, resolved-assets digest and common source inputs. It normalizes the command to
  `opencode-mcp[.exe]`, preserves runtime assets, adds license, notices and version/RID metadata,
  inventories every payload file, tests the normalized command, archives it, extracts to an owned
  temporary directory, compares the complete inventory, and tests the extracted command.
- `inspect --rid <RID> --path <executable-or-directory>` inspects only the supplied image when the
  path is a file. A directory also scans its companion files recursively. The framework-dependent
  negative control uses the file path.
- `validate --receipts <dir> [--manifest <path>]` checks the manifest's complete ordered receipt set,
  common source/SDK/version/dependency identities, successful publish and three test runs, effective
  properties, warning results, manifest runner/image identities, payload manifests, archive
  identities and archive contents. Each payload must contain the command, `LICENSE`,
  `THIRD-PARTY-NOTICES.md` and `payload.json`; the command digest must equal the verified executable
  digest. Validation does not rerun native inspection. The manifest defaults to
  `packaging/native-manifest.json`. An incomplete local set is a refusal, never six-RID acceptance.

Default evidence lives under `artifacts/native/<RID>/`: publish and toolchain logs, effective
properties in `receipt.json`, native inspection, `payload-manifest.json`, the normalized `payload/`,
and a ZIP on Windows or tar.gz on Unix. Symbols and build evidence stay outside the runtime payload.
Receipts record actual source commit/status and common input hash, SDK and compiler package
identities, requested runner versus actual host, executable and archive digests, resolved runtime
packages, and published/normalized/extracted test totals. Dirty development receipts remain dirty
development evidence; they are not relabeled as clean candidates. Unix payload modes flow through a
file-mode seam and the target RID: Windows payloads write no Unix mode; Unix tar entries and
extracted files preserve their recorded modes. The real adapter guards platform APIs on the actual
host and refuses Unix-mode writes on Windows.

Both Linux payloads build inside a product-owned builder image based on Ubuntu 22.04, built natively
for each architecture and pinned by digest. It carries the Native AOT prerequisites (clang and the
zlib development files), the tools the CI steps need (`curl`, `ca-certificates`, `git`), and a
pinned PowerShell LTS release; a job installs only the .NET SDK, from `global.json`. The image is CI
build infrastructure, not a product release artifact. The supported glibc floor is the highest
`GLIBC_` symbol version the payload references, measured with `objdump -T`, not the image's own
glibc version.

`docker/linux-builder.Dockerfile` owns the Ubuntu 22.04 recipe, its per-architecture PowerShell
archive pins and SHA-256 checks, and build-time architecture/toolchain smoke. It includes PowerShell's
runtime dependencies, but no .NET SDK or Docker engine. PowerShell bundles a .NET runtime, so this
builder is not the clean environment used for runtime-independence proof.

`.github/workflows/linux-builder.yml` builds and publishes
`ghcr.io/opencode-dotnet/opencode-mcp-linux-builder` only on manual dispatch. The native Linux
runners declared in `packaging/native-manifest.json` push by digest; a dependent merge job assembles
the multi-architecture index. Its only tag is
`jammy-<yyyymmdd>-<short source sha>-<github.run_id>-<github.run_attempt>` (UTC date, seven-character
source SHA), unique to a dispatch and re-run attempt and never moved. There is no mutable tag,
schedule, QEMU or automatic image deletion. The image carries source and revision labels. Native
consumers pin the published index digest, not its discovery tag. The package must be public and
anonymously accessible before native verification uses it; the workflow does not change visibility.

Runtime independence has its own proof. A Linux payload runs in a clean container that has no .NET
SDK or shared runtime. A Windows or macOS payload runs on its matching runner and passes a
native-image inspection: the payload holds no managed assemblies, runtime configuration, dependency
manifest, or host and runtime libraries (`hostfxr`, `hostpolicy`, `coreclr`), and the executable
exports the Native AOT `DotNetRuntimeDebugHeader` symbol (`_DotNetRuntimeDebugHeader` on macOS).
`DebuggerSupport` must not be false. A real framework-dependent apphost is the negative control:
inspection of the image itself must refuse it, independently of its companion files. Import lists
alone cannot tell a Native AOT image from a framework-dependent apphost and are supplementary only.
This Windows/macOS standard is inspection plus matching-host execution, not an SDK-free-machine
claim. Linux receipts additionally require clean-container runtime-independence evidence and the
measured glibc floor; builder execution alone does not supply either.

### Windows prerequisites

A Native AOT publish on Windows links with the platform linker, so the machine needs the Visual
Studio Build Tools workload "Desktop development with C++" (MSVC and a Windows SDK). Without it the
publish fails with "Platform linker not found".

With the Visual Studio 2026 Build Tools, ILCompiler finds the linker through `VsDevCmd.bat`, which
calls `vswhere.exe` by its bare name from the Visual Studio Installer folder. A publish from a shell
that sets `NoDefaultCurrentDirectoryInExePath` and has no Installer folder on `PATH` fails with
"'vswhere.exe' is not recognized" inside the linker command and then MSB3073: put
`C:\Program Files (x86)\Microsoft Visual Studio\Installer` on that process's `PATH` rather than
clearing the variable. The tool locates `vswhere.exe` at that standard path and puts the Installer
folder on the publish child's `PATH` only; it changes no machine or user setting. Before publishing,
it checks `Microsoft.VisualStudio.Component.VC.Tools.x86.x64` (the ARM64 tools for `win-arm64`),
Windows SDK libraries, linker and export inspector, and records their actual versions. A missing
prerequisite fails with its name, without an install or alternate-runner fallback.

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
from the product/ServiceDefaults runtime graph in the RID-restored `project.assets.json`, not the
test/AppHost graph. The tool records package IDs, versions and content identities, requires each
resolved runtime package's notice marker in `THIRD-PARTY-NOTICES.md`, and appends the actual package
and .NET/ASP.NET Core runtime distribution license/notice material. Missing required material is a
refusal. Polly's BSD-3-Clause copyright, conditions and disclaimer remain in the payload notices.
