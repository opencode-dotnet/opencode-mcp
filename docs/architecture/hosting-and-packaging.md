# Hosting and Packaging Architecture

Date: 2026-11-14

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
| `tools/OpenCode.Mcp.Tools` | Non-packable native verification, staging and packaging collaborators |
| `tests/OpenCode.Mcp.Tools.Tests` | In-process tool contract tests; no native compilation |

Every project targets `net10.0` with C# `14.0` and analysis level `10.0`; `Directory.Build.props`
owns those values. The AppHost and test projects are not package artifacts. The AppHost sits
outside the test-exemption globs, and its reference to the product is orchestration metadata.
ServiceDefaults is not a business-model or shared-helper bucket.

The product uses `Microsoft.NET.Sdk.Web` with explicit `OutputType=Exe` and is the solution's only
packable project.
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
  publishing. An output is owned when empty or marked by the persistent `.opencode-mcp-native`
  file; a foreign non-empty output without that marker is refused. Child build, restore,
  property-query and publish commands disable node reuse and shared compilation. It captures both
  publish streams, preserves the exit result, scans
  for trim/AOT diagnostics even on exit zero, inspects the native image, and runs `ExecutableTests`
  on that exact published path in both transport modes.
- `stage --rid <RID> --publish-directory <dir>` requires matching successful verification and an
  unchanged executable, resolved-assets digest and common source inputs. It normalizes the command to
  `opencode-mcp[.exe]`, preserves runtime assets, adds license, notices and version/RID metadata,
  inventories every payload file, tests the normalized command, archives it, extracts to an owned
  temporary directory, compares the complete inventory, and tests the extracted command.
- `pack-child --rid <RID> --evidence <directory>` requires that RID's staging evidence, matching
  executed SDK, source commit and common-input digest, and the manifest-ordered evaluated tool RIDs.
  Before deleting anything, it requires the evidence directory's `.opencode-mcp-native` ownership
  marker; an unmarked directory and its `packages/` are left untouched. It then deletes its derived
  `packages/` directory before checking the remaining preconditions and packs from `payload/`
  without building, restoring or publishing. Any pack warning or nonzero exit refuses the result.
  It revalidates the native artifacts after packing, requires exactly the expected child package
  and no other file-system entry, and checks its identity, sole child type, command settings,
  whole-payload digests, channel metadata and repository commit. Outside the tool directory, the
  entry set is closed: only `<package ID>.nuspec` at the package root, `.mcp/server.json`,
  `README.md`, `LICENSE`, `THIRD-PARTY-NOTICES.md`, `icon.png`, `[Content_Types].xml`, `_rels/.rels`
  and one `.psmdcp` under `package/services/metadata/core-properties/` are allowed. A post-pack refusal deletes
  `packages/` again; a cleanup failure follows the original refusal in the ordered failure list.
  In an evidence directory carrying the ownership marker, after `pack-child` returns, `packages/`
  exists only when it holds the package that this run accepted; when cleanup itself fails, the
  refusal lists both failures and the directory can remain. An unmarked directory is left untouched.
  This rule covers `pack-child`'s own run: a later `verify` does not remove `packages/`.
  Success prints the package path and SHA-256. It requires neither the six-RID set nor the later
  Linux runtime-independence proof.
- `pack-root --receipts <directory> --feed <directory>` first requires the complete native validation
  and six matching child packages, then packs the SDK-generated root package in Release, without a
  RID or payload override. The executing SDK, source commit and common-input digest must match the
  coherent receipt set, and the evaluated tool RIDs must match the manifest in order. Each evidence
  directory's `packages/` must hold exactly its expected child and no other entry; every child
  passes the child policy against its native receipt. The feed directory must be absent or empty;
  a non-empty directory or a file at that path is refused without a write or deletion. The command
  owns the absent or empty directory, packs the root, copies the children and their evidence, and
  validates the feed.
  Any pack warning, nonzero exit or later refusal deletes that owned feed; a cleanup failure follows
  the original refusal. Success prints the feed path and all seven package names and SHA-256 values,
  root first and then in manifest RID order.
- `assets --receipts <directory> --evidence <directory> --output <directory> --version <version>`
  requires the complete coherent native receipt set, an exact version match for every receipt, and
  matching completed staging evidence. The evidence root contains `native-complete-<RID>`
  directories; the receipt search root may be the same directory or a separate consolidated receipt
  tree. Each staged archive has its canonical RID basename and passes the existing artifact
  validator. The checkout's `packaging/install/install.sh` and `install.ps1` must both exist with
  exactly one whole-line default-version assignment matching the requested version (see
  [install scripts](#install-scripts)). Input refusals precede output ownership.

  The output directory must be absent or empty; a file or non-empty directory is refused without
  modification. The command owns an accepted directory and copies the six staged archives to
  `opencode-mcp-<version>-<RID>.zip` or `.tar.gz`, without rebuilding or re-archiving. Each copied
  archive's SHA-256 must equal its receipt. It then copies the two installer scripts unchanged,
  shell first, and checks each copy's SHA-256 against its captured source bytes.

  The nine-file output comprises six archives, `install.sh`, `install.ps1` and `SHA256SUMS`.
  `SHA256SUMS` contains exactly eight lines: six archives in manifest order, then `install.sh`,
  then `install.ps1`. Each line has lowercase SHA-256, two ASCII spaces,
  release basename and LF, encoded as UTF-8 without BOM. It has no self-entry. Any refusal after
  ownership deletes the output; a cleanup failure follows the original refusal. Success prints the
  output directory, the eight asset names and SHA-256 values in that same order, then the checksum
  file's name and SHA-256.
- `inspect --rid <RID> --path <executable-or-directory>` inspects only the supplied image when the
  path is a file. A directory also scans its companion files recursively. The framework-dependent
  negative control uses the file path.
- `control --rid <RID>` publishes a real framework-dependent apphost under owned temporary storage,
  including isolated intermediate outputs. It inspects the executable alone and requires exactly
  the missing Native AOT export refusal. A companion file or a generic inspector failure cannot
  satisfy the control. The control does not change the native receipt or invalidate its assets.
- `matrix --manifest <path> --github-output <file>` projects the manifest, in order, into
  `linux-matrix`, `native-matrix` and `consumer-matrix` Actions outputs, in that output order. Linux
  build entries also carry the builder and runtime images; consumer entries contain exactly `rid`
  and `runner` for every manifest row.
- `prove-linux --rid <RID> --evidence <downloaded-directory>` requires a matching Linux host,
  source inputs and executed SDK. It clears stale proof, verifies the complete downloaded payload,
  manifest and archive, and extracts the verified tar before executing anything downloaded. It
  probes the manifest's already-pulled clean runtime image and runs the prebuilt `ExecutableTests`
  in that image against the extracted command, and measures the highest numeric `GLIBC_` requirement
  with `objdump -T`. Failed, absent or malformed measurement is a refusal. Proof is written only
  after successful tests and owned cleanup. The caller builds the product-test runner in Release
  and pulls the manifest runtime image before this command; neither preparation runs inside the
  test step's budget. The proof still inspects the local image before using it.
- `snapshot --output <directory>` records PID, process name, CPU, working set, age and command line,
  sorted by CPU descending, in `processes.txt`; its first 25 rows also reach the job log.
- `validate --receipts <dir> [--manifest <path>]` checks the manifest's complete ordered receipt set,
  common source/SDK/version/dependency identities, successful publish and three test runs, effective
  properties, warning results, manifest runner/image identities, payload manifests, archive
  identities and archive contents, matching actual OS/OS architecture/process architecture, and the
  platform's structured runtime-independence proof. Each payload must contain the command, `LICENSE`,
  `THIRD-PARTY-NOTICES.md` and `payload.json`; the command digest must equal the verified executable
  digest. Validation does not rerun native inspection. The manifest defaults to
  `packaging/native-manifest.json`. An incomplete local set is a refusal, never six-RID acceptance.

Default evidence lives under `artifacts/native/<RID>/`: publish and toolchain logs, effective
properties in `receipt.json`, native inspection, `payload-manifest.json`, the normalized `payload/`,
a ZIP on Windows or tar.gz on Unix, and `pack-child`'s accepted child package in `packages/`.
Symbols and build evidence stay outside the runtime payload.
Receipts record actual source commit/status and common input hash, SDK and compiler package
identities, requested runner versus actual host, executable and archive digests, resolved runtime
packages, and published/normalized/extracted test totals. Dirty development receipts remain dirty
development evidence; they are not relabeled as clean candidates. Unix payload modes flow through a
file-mode seam and the target RID: Windows payloads write no Unix mode; Unix tar entries and
extracted files preserve their recorded modes. The real adapter guards platform APIs on the actual
host and refuses Unix-mode writes on Windows.

`RuntimeIndependence` records `Kind`, optional `Tests`, `ImageReference`, `ImageId`, `DotnetAbsent`
and `Inspection`. Linux requires `linux-clean-container`, successful nonzero container tests, the
exact manifest runtime reference, a recorded image ID, and `DotnetAbsent=true`. The probe requires
an exit-127 or exit-1 `command -v dotnet` lookup with empty stdout and stderr, and successful
same-filesystem whole-root enumeration with no `dotnet`, `libhostfxr.so`, `libhostpolicy.so` or
`libcoreclr.so`. Linux also requires
`GlibcFloor` with the measured `Version` and `ToolVersion`. Windows/macOS require
`matching-host-inspection` and the successful final normalized-payload export inspection;
their glibc field is null. Receipt validation checks recorded evidence, not an inspection rerun.

The `Native` workflow projects both build matrices and the consumer matrix from the manifest, with
no second RID list. Preparation resolves the SDK from `global.json`; every later job installs that
exact SDK. Linux builds run in the pinned builder job containers; Windows/macOS build on matching
hosts. Separate Linux host jobs prove the downloaded archives. The native validation job runs only
after every needed job succeeds, downloads completed per-RID evidence into separate directories,
and validates the receipt set. Its success and log are the fan-in evidence; no preparation file or
receipt index is produced. This workflow has read-only contents permission, runs on pull requests
and manual dispatch, and adds no required check. A subsequent `packages` job downloads the completed
artifacts of its own workflow run, packs the root, validates the complete feed and runs its separate
package-contents pass. The workflow's artifact download establishes the one-run boundary; receipts
prove source, common-input, SDK, version and dependency coherence, not workflow-run origin. Local
`pack-root` makes no workflow-run-origin claim. After `packages`, one `consumer` leg per manifest
row runs on its matching RID host against that same workflow run's complete `native-package-feed`,
and the [package gate](../engineering/quality-gates.md#package-pass) owns its inputs, artifacts and
acceptance rule.

A separate `release-assets` job needs native validation and downloads the same run's completed
evidence. It assembles and uploads `native-release-assets`; it neither publishes a release nor
depends on package assembly.

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
builders use `ubuntu-24.04` and `ubuntu-24.04-arm`, matching the manifest's Linux runners,
and push by digest; a dependent merge job assembles the multi-architecture index. Its only tag is
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

### Container executable-test launches

The container selector applies only when `OPENCODE_MCP_TEST_CONTAINER_IMAGE` is present. It must
be digest-qualified, requires both native selectors and a compatible Linux image/test host, and
is consumed before the product starts. Direct launches retain their existing selection and
environment behavior.

Container launches bind the exact prepared root read-write at `/opencode-mcp-run`, the payload
read-only at `/opencode-mcp-payload`, and work in `/opencode-mcp-run/work`. They use host networking,
the measured host UID/GID, no TTY, and no published ports. Isolation directories are created inside
the owned root and mapped explicitly; ordinary test inputs use explicit environment entries,
including distinct empty and removed values. The Docker CLI receives only allow-listed launch
necessities, an owned home and empty `DOCKER_CONFIG`, and a deterministic diagnostic locale. It
inherits no Docker host/context, credentials, proxies or SDK environment. Docker requests opt out
of environment inheritance and use the default local socket. Explicit `DOTNET_*`, `LANG`, `LC_ALL`
and `PATH` test inputs are refused with a diagnostic naming the input; they are never silently
dropped.
The Docker CLI process alone may carry the host's `DOTNET_ROOT`; it is never forwarded into the container, and explicit `DOTNET_*` container inputs stay refused.

Cleanup ends and awaits the CLI before forced exact-name removal and absence verification;
attached reader EOF is never awaited before removal. Absence needs a recognized missing-container
inspection response and successful all-container listing without the exact name. Unknown errors,
daemon failure and a still-present container fail cleanup. Independent cleanup phases have fresh
15-second guards and retain failures in operation/CLI/removal/absence/readers/handles/root order.
A removal-already-in-progress response proceeds to the same absence proof instead of failing by
itself.
Unresolved termination or absence retains and names the owned root. Repeated disposal is idempotent.

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
- The packages carry authors, copyright, this product's project URL and tags, and the OpenCode
  .NET SDK repository's icon as `icon.png` at the package root.
  `Directory.Build.props` owns the values, and the product project owns its tags. `Company` and
  `Copyright` also stamp the assemblies; a package has no company field. Package inspection pins
  the authors to `Deniz İrgin` and the project URL to
  `https://github.com/opencode-dotnet/opencode-mcp`, for the root and every child; tags, copyright
  and the nuspec icon element are not pinned. The icon stays outside the native payload and
  publish output.
- `ToolPackageRuntimeIdentifiers` lists exactly the six native RIDs. The SDK generates one
  implementation package per RID, `OpenCodeDotNet.Mcp.<RID>` of type `DotnetToolRidPackage`, at the
  same version. The root package never maps an absent child, and no `any` mapping exists.
- The ordered tool RID list is a checked projection of `packaging/native-manifest.json`, not
  another owner. Each child packs its staged native payload through the SDK's supported
  `IsPublishable=false`, `PublishDir` and `ToolEntryPoint` composition, with `--no-build --no-restore`;
  it never recompiles, relinks or republishes the payload. The SDK owns the child ID, package type,
  runner and implementation layout. MCP package metadata is channel metadata outside that payload.
- The manifest's two versions and root package identifier are checked against the evaluated product
  identity at pack time. Its product policy checks listing identity, description bounds, the single
  NuGet package, exact ordered arguments, stdio transport and a UTF-8 size below 20,000 bytes.
  The manifest is checked by a typed product policy; the repository carries no JSON Schema engine
  and no copy of the schema. The policy mirrors the published 2025-12-11 schema's
  `ServerDetail.required` and `Package.required` fields, `Package.version` prohibition of `latest`,
  transport type enumeration (`stdio`, `streamable-http`, `sse`), and array types for
  `Package.packageArguments` and `Package.runtimeArguments`, naming each rule in its diagnostics.
  The exactly pinned listing name is tested against the `ServerDetail.name` pattern and 3 to 200
  character bounds. Packing never rewrites the tracked manifest.
- `.mcp/server.json` declares stdio acquisition through `dnx` with explicit
  `--transport stdio` package arguments. `dnx` receives `-v minimal` before `--`, both there and in
  the [manual verification recipe](../engineering/quality-gates.md#package-pass): on macOS a cold
  acquisition at default verbosity writes a signature-verification notice to standard output before
  the server starts, which would corrupt the protocol stream. That line is never filtered out to
  pass a test.
- Native execution needs no installed .NET runtime; `dnx` acquisition still needs the .NET SDK.
  Direct native archives are the SDK-free route.
- The packed README is `packaging/README.md`, packed as `README.md`, and contains no repository
  links. No `McpName` property and no unverified repository URL is set.

### Root package and complete local feed

The root is packed only after the complete native set and all six children pass. Its ID and version
equal the evaluated product identity, and its package types are exactly `DotnetTool` and
`McpServer`. Its single SDK-generated `DotnetToolSettings.xml`, found by name, has the command
`opencode-mcp` with no runner or entry point. It maps exactly the six manifest RIDs in manifest order,
each to `<PackageId>.<RID>`: no missing, extra, reordered or `any` mapping is accepted. The SDK's
`tools/any/any/` settings directory is legitimate; `any` is forbidden as a RID mapping, not as a
directory name. No implementation file may sit beside the settings. The root has the same closed
channel-metadata entry set as a child, including the exact root nuspec name, and its repository
commit equals the native receipts' source commit.
Both the root and every child require `README.md`, `LICENSE`, `THIRD-PARTY-NOTICES.md` and `icon.png`
at the package root. The closed channel-metadata entry set allows `icon.png` only there.

`native-package-feed` contains exactly seven flat packages and
`evidence/<RID>/receipt.json` plus `evidence/<RID>/payload-manifest.json` for every manifest RID.
No other file is accepted. Only `evidence/` and `evidence/<RID>/` directories may exist; an extra
directory, empty or not, is refused. Evidence is copied byte-for-byte from the completed native
directories; payload directories and archives are not duplicated in the feed. The shared feed validator checks
the complete receipt policy, every receipt's version against the expected product version, equality
of each receipt's payload list and its retained manifest, the root and child package policies, and
byte-identical MCP manifests in all seven packages. Children are linked to native evidence by
complete payload content, not by a second package receipt or version labels alone.

`packaging/local-feed.nuget.config` clears inherited sources and names only `../artifacts/feed`,
resolved from that configuration's directory. It is not used for ordinary product, tooling or test
dependency restore. A complete local feed authorizes no publication and makes no package-acquisition
or packaged-startup claim.

### Payload, candidate, and acquisition

- One verified payload per RID feeds the NuGet child, the direct archive, and later installer
  wrappers. Payload equality covers the complete declared file set by normalized path and digest:
  the executable, required assets, notices, and payload metadata. An executable hash alone is not
  equality. Native debug symbols, build receipts, and channel wrappers stay outside the payload.
- A release archive is the staged archive under its versioned release basename, with the same
  archive SHA-256 recorded in that RID's receipt. Renaming occurs while assembling the candidate's
  `native-release-assets`, not during publication. A release publisher consumes that artifact
  unchanged.
- A package candidate is one real source commit with no uncommitted source or build-input change,
  consistent recorded common build inputs, and each RID's own toolchain, environment, and payload
  evidence. Equal version labels do not prove equal sources. The candidate commit needs separate
  maintainer approval (`../engineering/workflow.md`). Builds during development are development
  evidence, not candidate evidence. A source or build-input change makes a new candidate that is
  verified again; an earlier artifact or receipt is never relabeled as produced from a later
  commit.
- Acquisition uses an exact product version and the complete controlled local feed through both
  `dotnet dnx` and an owned tool-path installation; neither route installs globally or changes a
  product tool manifest.
  Windows launches the SDK's `.cmd` through the absolute system command interpreter with a verbatim
  command tail; Unix launches the installed symbolic link directly, never by changing `PATH`.
- Acquisition children use the process owner's isolation policy, extended with owned NuGet package,
  HTTP, plugin, scratch and tool stores, an empty plugin-discovery directory, and a private-SDK/OS-only
  `PATH`. Windows Program Files paths and Linux common application data are owned. Ambient ancestor
  NuGet configuration and macOS's fixed machine configuration are refused, never read or hidden.
  `../engineering/testing-style.md` section 6 owns the exact environment and SDK-first-use boundary.
  Cold means empty package, tool and temporary acquisition stores, not execution of SDK first use.
- A lazy session-owned real copy of the selected SDK contains only its host, complete SDK directory,
  required Core runtime and matching hostfxr. Acquisition and native startup run without a shared
  ASP.NET Core runtime. Contents-only tests and the managed pass do not create that copy.
- Packaged startup requires the observed running native entry to be the selected implementation
  package's exact version and its complete payload to match the feed evidence by path and SHA-256.
  Managed and development-output fallbacks are forbidden. Process ownership includes every live
  executable inside the owned root, before protocol readiness too; root exit alone proves nothing.
  Acquisition is distinct from native runtime independence and OpenCode integration. It requires no
  external network denial, promises no offline installation, and authorizes no publication.

## Installation and release boundaries

Publication consumes the candidate's seven original packages and release-assets artifact without
rebuilding, repacking, re-archiving or renaming. A candidate's workflow tree must equal master's at
publication.

The `publication` command resolves installer sources from the candidate checkout's `packaging/install`.
After native receipt, candidate identity and feed validation, it requires exactly six versioned
archives, `install.sh`, `install.ps1` and `SHA256SUMS`, with no extra directory. Archives match
their receipts; scripts are byte-equal to the committed installers and their default-version
projections match the requested version. Checksum bytes are rebuilt independently, in the assembly
order and format above. Inputs remain unchanged. Script identity refuses with
`<name>: Release script differs from committed installer.`; checksum identity refuses with
`SHA256SUMS differs from the ordered release checksums.`

NuGet.org repository signing changes the package envelope. Real-channel identity is therefore
established through the running native payload's complete paths and SHA-256 values, not downloaded
package-envelope equality.

The product target includes PowerShell and shell installers, WinGet, Homebrew, suitable deb and rpm
packages, a Windows MSI, and a macOS pkg. Archive-only delivery does not close that target. Each
surface is a separate delivery increment after native and package proof, never a placeholder.

- Installers use version-pinned immutable assets, detect the OS and architecture, verify a trusted
  digest, replace atomically after staging, default to user scope for scripts, own their `PATH`
  changes, detect collisions, upgrade by version, and uninstall without touching unrelated
  commands, configuration, or data. System packages and some macOS installer scopes need documented
  privilege. No installer silently configures an MCP client or starts a daemon.
- Channel versions are projections of one release identity with deterministic, monotonic upgrade
  ordering. The MSI tool and its terms are approved before MSI authoring; the version conversion of each installer surface is fixed and tested before that surface is authored.
  The install scripts install the SemVer release version unchanged. Their version conversion is the identity; installer acceptance tests preserve the exact version, including prerelease and build metadata.
- Signing and notarization, registry namespaces and prefix authority, real download URLs,
  package-manager submissions, and maintained signed APT or DNF repositories are separately
  authorized release operations. A checksum does not authenticate a publisher; final hashes follow
  every byte-changing signing step. Native debug symbols do not enter a managed symbol package.

### Install scripts

The release includes POSIX-shell and Windows PowerShell installers, copied unchanged from the
candidate checkout. Their embedded default versions are checked projections of the committed
product version. Explicit versions select immutable, version-pinned release assets; neither
installer resolves “latest.” Each script contains exactly one whole line that assigns its
version variable, without leading whitespace; a longer variable name or a comment is not such a
line:

```sh
OPENCODE_MCP_RELEASE_VERSION='<version>'
```

```powershell
$OpenCodeMcpReleaseVersion = '<version>'
```

Assembly and publication require the exact whole-line form above for the requested version.
Missing or mismatched sources produce, in shell-then-PowerShell order,
`Missing installer script: <name>.` or `Installer default version must be <version>: <name>.`.
Neither operation stamps or rewrites the source.

#### Options and defaults

| POSIX spelling | PowerShell spelling | Contract/default |
|---|---|---|
| `--version <version>` | `-Version <version>` | Embedded committed release-version projection. Exact SemVer 2.0.0 without leading `v`; no normalization or suffix removal. |
| `--install-dir <directory>` | `-InstallDir <directory>` | Unix `${XDG_BIN_HOME:-$HOME/.local/bin}` holds the command link; Windows `%LOCALAPPDATA%\Programs\opencode-mcp` holds the installation tree. |
| `--data-dir <directory>` | None | Unix `${XDG_DATA_HOME:-$HOME/.local/share}/opencode-mcp` holds the private payload and records. No Windows counterpart. |
| `--add-to-path` | `-AddToPath` | False. Explicit consent to persistent user-scope PATH modification. |
| `--uninstall` | `-Uninstall` | False. Remove the owned installation and owned PATH modification, without downloading. |
| `--asset-dir <directory>` | `-AssetDir <directory>` | Absent: version-pinned HTTPS release source. Test-only flat local source, still checksum-verified, without network fallback. |

No additional product options or aliases exist. Empty or relative `XDG_*` values count as unset.
Explicit relative directories resolve against the invocation's working directory; empty values
are refused. POSIX options are case-sensitive separate tokens, once only, with no `--name=value`
form. PowerShell retains ordinary named-parameter/switch binding and its native diagnostics.
Uninstall accepts only its switch and the installation-directory option, plus Unix `--data-dir`;
explicit version, asset source and PATH-addition options are refused with uninstall.
Invalid versions and unrepresentable paths are refused before mutation. PATH insertion refuses
directories containing the platform PATH separator or a line break; spaces and apostrophes work.

#### Layout and activation

| Platform | Command | Private layout |
|---|---|---|
| Unix | `<install-dir>/opencode-mcp`, an installer-owned link to `<data-dir>/versions/<version>-<rid>/opencode-mcp` | `<data-dir>/owner`, `current`, `inventory`, optional `path.record`, `versions/<version>-<rid>/` and staging directories |
| Windows | `<install-dir>\current\opencode-mcp.exe`, the native executable, not a wrapper | `current\` holds the complete payload; `.opencode-mcp\` holds records, staging and previous payload during activation. No version directory remains after activation. |

Scripts default to user scope and verify the selected archive against that release's checksum
file before extraction. They preserve the complete native payload and Unix executable modes,
stage on the destination filesystem and publish only complete payloads. Unix atomically replaces
the public link by creating a link beside it and renaming it over the old one. The owner record
names the canonical data directory, absolute link path and installer-created directories.
Records are data, never sourced or evaluated.

Windows verifies `.opencode-mcp\staging-<unique>`, renames `current` to
`.opencode-mcp\previous-<unique>`, then renames staging to `current`. If the second rename fails,
it restores the previous payload and stops. Only after the second rename succeeds does it replace
the records and delete the previous payload.
A running command can make the first rename fail with `Installation failed during activation.`;
nothing is stopped and the old installation remains intact. Individual activation surfaces are
replaced atomically; this is not a crash-atomic transaction across filesystem state and user PATH.
An operation failure precedes any cleanup failure, which names retained owned storage.

A same-version, byte-identical installation is idempotent. Explicit versions select that exact
release, including prerelease/build metadata, without an automatic newest lookup or downgrade
policy. Both archive and checksum file come from
`https://github.com/opencode-dotnet/opencode-mcp/releases/download/v<version>/`.
The selected archive requires exactly one checksum entry: lowercase 64-character SHA-256, two
spaces and its exact basename. Scripts hash it independently before extraction and never execute
the downloaded command during installation. Checksums check integrity, not publisher authenticity;
the HTTPS release source (or explicit local source) is trusted. A piped script cannot authenticate
its own executing bytes. Script definitions precede an entry-point call on the last line so a
truncated download cannot execute a partial installation.

PATH modification requires explicit consent. Unix adds the installation directory through one
exactly recorded, uniquely delimited, correctly quoted block: `sh` uses `~/.profile`; `bash` uses
the first existing `~/.bash_profile`, `~/.bash_login`, `~/.profile`, else creates `~/.profile`;
`zsh` uses `${ZDOTDIR:-$HOME}/.zprofile`. Existing bytes and permissions are preserved.
`fish` uses one owned `${XDG_CONFIG_HOME:-$HOME/.config}/fish/conf.d/opencode-mcp.fish` file and
refuses an existing file. Other shells refuse `--add-to-path`, not installation without it.
An already-effective PATH entry is not adopted. Uninstall removes only the exact recorded block
or byte-identical owned fish file; missing, edited or duplicate owned state is ambiguous.

Windows adds `<install-dir>\current` once to the raw user `Path`, preserving its registry kind
and unrelated tokens. Upgrades keep the owned token without repeated `-AddToPath`. Writes broadcast
`WM_SETTINGCHANGE` with `Environment`; no machine PATH, `setx`, or caller-process PATH edit occurs.
The change is for future sessions, not a promise to refresh an existing terminal or Explorer.
Uninstall reverses only an unambiguous owned token and preserves pre-existing entries/later edits.

Foreign commands, invalid ownership, changed recorded files/targets and unexpected payload files
are refused without destructive repair or adoption. Uninstall preflights ownership, inventory and
PATH reversal, removes only recorded state, and removes installer-created directories only when
empty. Pre-existing directories, unrelated files, configuration and data survive. Unix ownership
comes from the data directory; an explicit installation directory must contain the recorded link.
No owned installation means no change. Locked files fail without process termination, retry or
elevation. Scripts configure no MCP client, start no daemon and install no prerequisite.

POSIX requires ordinary platform utilities, `tar`, `curl` for HTTPS, and Linux `sha256sum` or
macOS `shasum`. PowerShell requires Windows PowerShell 5.1 or PowerShell 7 and built-in archive/hash
facilities; 5.1 enables TLS 1.2 for downloads when needed. Local-source installation makes no HTTP
operation; uninstall needs neither network nor archive tools. Platform detection selects the
manifest's six native OS/architecture pairs, handles Rosetta's native architecture and Windows
native OS versus process architecture, and refuses unsupported hosts, 32-bit userlands and unknown
Linux libc rather than assuming glibc; musl is unsupported.

#### Results and diagnostics

| Exit (file/process invocation) | Meaning |
|---|---|
| `0` | Installed, already installed, uninstalled, or no owned installation found |
| `2` | Script-owned validation, ownership or integrity refusal |
| `1` | Acquisition, filesystem, activation or cleanup operation failed |

PowerShell binding failures retain native diagnostics and exit behavior. Through `iex` or an
invoked scriptblock, failures are terminating errors, not `exit` statements closing the caller.
Stable script-owned output has the `opencode-mcp: ` prefix and these diagnostic templates:

| Diagnostic template | Detail |
|---|---|
| `Version must be an exact SemVer 2.0.0 value without a leading v.` | Version refusal |
| `Unknown option: <option>.` | Unknown POSIX option |
| `Option requires a value: <option>.` | Missing value |
| `Option may be specified only once: <option>.` | Repeated option |
| `Uninstall accepts only the installation directory.` | Invalid uninstall combination; Unix also permits its data directory |
| `Unsupported platform: <os>/<architecture>.` | Unsupported host |
| `A 64-bit operating system and shell process are required.` | 32-bit refusal |
| `Linux requires glibc; musl is not supported.` | Libc refusal |
| `Required command is unavailable: <name>.` | Missing prerequisite |
| `Install collision: <absolute-path>.` | Foreign destination or command |
| `Installed files differ from the ownership record: <relative-path>.` | Changed owned payload |
| `Invalid checksum entry: <archive-basename>.` | Missing, duplicate or malformed entry |
| `SHA-256 mismatch: <archive-basename>.` | Archive integrity failure |
| `PATH ownership is ambiguous: <startup-file-or-user-Path>.` | PATH reversal/ownership refusal |
| `Installation directory differs from the ownership record: <absolute-path>.` | Explicit Unix install directory does not contain the owned link |
| `Unsupported shell for --add-to-path: <shell>.` | Unsupported Unix PATH shell |
| `Installation failed during <phase>.` | `download`, `staging`, `activation`, `path`, `uninstall` or `cleanup` |
| `Cleanup failed; retained owned directory: <absolute-path>.` | Cleanup follows the operation failure |

Success templates are `Installed <version> (<rid>): <absolute-native-command>`,
`Already installed <version> (<rid>): <absolute-native-command>`,
`Uninstalled opencode-mcp: <absolute-install-dir>` and `No owned installation: <absolute-install-dir>`.
Installation also reports exactly `PATH unchanged.` or `PATH updated for future login sessions.`.
Validation is fail-fast in argument → platform/prerequisite → ownership/collision →
acquisition/checksum → staging → activation order.

## Licensing

The repository uses the MIT license in `LICENSE`; `THIRD-PARTY-NOTICES.md` carries the notices of
derived content. A native payload carries `LICENSE` and the notices of the code it embeds, derived
from the product/ServiceDefaults runtime graph in the RID-restored `project.assets.json`, not the
test/AppHost graph. The tool records package IDs, versions and content identities, requires each
resolved runtime package's notice marker in `THIRD-PARTY-NOTICES.md`, and appends the actual package
and .NET/ASP.NET Core runtime distribution license/notice material. Missing required material is a
refusal. Polly's BSD-3-Clause copyright, conditions and disclaimer remain in the payload notices.
