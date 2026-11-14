# Engineering Quality Gates

Date: 2026-11-14

Canonical quality policy for product code, repository tooling, tests, analyzers, dependencies,
performance evidence, and completion claims.

## Assurance posture

The repository is defensive and fail-closed by default. Guard public preconditions and local
representability, assert internal invariants, and fail loudly rather than guess. Do not turn that
posture into a second validator for what the MCP C# SDK already validates at the protocol boundary;
product rules guard what the product decides, such as the transport selection and the HTTP endpoint
policy.

Assurance intensity follows blast radius:

1. The shipped MCP server — the executable in both transport modes, its standard ServiceDefaults
   and its native payloads — receives the strongest evidence: real processes, real protocol
   exchanges, owned cleanup and native execution on each supported platform.
2. Delivery artifacts are protected by inspecting the packages the .NET SDK actually produced and
   by whole-payload equality, never by matching version labels alone.
3. The AppHost and repository tooling receive targeted evidence for their actual consumers and
   known lossy seams. Every additional mechanism must name a consumer or a concrete failure it
   prevents.

The testing posture is deliberately strict: observation-based gates, deterministic tests, fakes
only for published contracts, TUnit on Microsoft.Testing.Platform, real-process integration where
the process boundary is the product, and three-OS managed acceptance. A flaky test is fixed at its
root cause. It is never retried, given a wider bound, or quarantined.

Claims stay separate. MCP connectivity, package acquisition, native execution without an installed
.NET runtime, and later OpenCode .NET SDK behavior against an OpenCode server process are distinct
claims, and the evidence for one never stands in for another. No artifact or platform is claimed as
supported before its own gate has run on it.

## Analyzer policy

The analyzer wall is fail-closed and final. Build owns compiler, SDK CA, third-party analyzer,
source-generator, and build-enforceable IDE diagnostics; the warning-level style formatter owns
Roslyn rules classified `EnforceOnBuild.Never` plus deterministic import organization. Do not
relitigate or broadly weaken either side to make a change pass. Redundant rules are deliberate where
their coverage differs or supplies defense in depth. When a rule misfires on valid code, use a
narrowly scoped per-rule arbitration comment that names the winning rule or contract; never roll
back the policy globally. An arbitration is a suppression channel: it follows the suppression
policy in `slop-review.md`, so a new one needs the maintainer's approval.

- A rule that cannot fire here but still costs build time is switched off compilation-wide in the
  repository-root `.globalconfig`, with the same winner-naming reason and a removal trigger. A
  file-scoped `.editorconfig` severity hides the diagnostic, but the analyzer still runs: Roslyn
  skips an analyzer only when all its diagnostics are off for the whole compilation. The repository
  has no `.globalconfig` until such a rule is proven on this build graph with the pinned analyzer;
  an entry is never borrowed from another repository.
- `LangVersion=14.0` and `AnalysisLevel=10.0` are deliberate numeric pins. Never replace either
  with `latest`. Future language changes move the numeric pin intentionally.
- `GenerateDocumentationFile=true` is load-bearing because CLI builds do not report IDE0005
  consistently without it. Keep the adjacent guard comment in `Directory.Build.props`.
- Analyzer package currency is manual. Since SDK 9, the build does not warn reliably when the
  pinned NetAnalyzers package falls behind the .NET SDK; deliberate analyzer-package updates own
  that comparison explicitly.
- Repository-wide analyzer packages inherit into every project, and global warning escalation has
  no `WarningsNotAsErrors` escape. A project `NoWarn` row is a suppression; it would apply equally
  to build and format, so build mechanically owns enabled warning/error diagnostics from every
  third-party analyzer.
- Product code uses `ConfigureAwait(false)`, triple-enforced by CA2007, MA0004 in `Always` mode,
  and VSTHRD111. Tests are exempt from all three rules.
- `CA1502` attributes local functions declared in top-level statements to the compiler-generated
  `Main`, so a top-level `Program.cs` accumulates every mode's dispatch complexity onto one
  method. Split the dispatch into a named class instead of arbitrating the rule: the MCP server's
  `Program.cs` is entry-point wiring only and hands the run to `McpApplication`, and the AppHost's
  hands its graph to `AppHostComposition`.

## Dependency audit

Restore runs NuGet's default audit, which on `net10.0` covers transitive packages, and the
warnings-as-errors wall turns an advisory into a restore failure. There is no exception: no audit
suppression, no lowered audit level or mode, no `NoWarn` for an audit warning. The remedy for an
advisory is a prompt pin bump through the dependency refresh in `workflow.md`, never a suppression
or an audit override.

Build-time audit judges the advisories that exist when the build runs. The other half is the
`dependency-review` workflow on pull requests: it reads the pull request's dependency diff and fails
on a newly introduced package with an advisory of moderate severity or above, so a new package is
judged before it merges. It updates nothing. GitHub's dependency graph does not read
`Directory.Packages.props`, so the review sees a new package's name but not its centrally managed
version (it reports `>= 0`); the version-level advisory gate is the restore audit above.

## Completion gate

"Builds" and "works" are different claims. A pull request is ready for the merge queue when the CI
its own head triggers is green: the lint job and the three operating-system legs run every step of
the full gate below. A green queue run permits the merge; the change is complete when the CI its
merge commit triggers on `master` is green. Which changes skip those jobs, and what a merge
requires, are in `workflow.md`.

Every build and test command in the completion gate and local check runs with an owned
`ASPIRE_HOME` beneath the user's temp folder. Without it, the AppHost SDK installs the Aspire
bundle into the shared `%USERPROFILE%/.aspire` directory. Set it before the first build and keep
it until all tests finish because generated metadata retains its bundle paths. Set `ASPIRE_HOME`
to an owned directory named `opencode-mcp-aspire-home` under the system temp folder.

Before a pull request opens, run the local check from the repository root. It catches failures
before they cost a CI run. A current handoff may add scope-specific checks but cannot weaken it.
The local check builds with the analyzer wall on and runs every test module of the solution:

```text
dotnet tool restore
dotnet tool run slopwatch analyze --exclude ".scratchpad/**" --fail-on warning
dotnet build OpenCode.Mcp.slnx --configuration Release
dotnet format whitespace OpenCode.Mcp.slnx --verify-no-changes --no-restore
dotnet format style OpenCode.Mcp.slnx --verify-no-changes --no-restore --severity warn
dotnet build tools/native.cs
dotnet test --solution OpenCode.Mcp.slnx --configuration Release --no-build --treenode-filter "/*/*/*/*[Category!=Package]" --report-trx --results-directory artifacts/test-results --minimum-expected-tests 1
```

The file-based entry is not part of the solution compilation. Its separate build retains the
analyzer wall. Also run this real CLI smoke against a new owned empty directory; require exit 2
and the complete ordered missing-receipt diagnostic, not just any nonzero exit:

```text
dotnet run tools/native.cs -- validate --receipts "<owned-empty-receipt-directory>"
```

Create that empty directory under system temp before the smoke. Compare the output to one
`Missing receipt: <RID>` line for each manifest entry, in manifest order. Remove only that owned
directory afterwards. The CI lint job performs the same assertion automatically.

Every compiler, analyzer, style and test step must succeed, and every test module must execute
tests; a module that runs none fails the gate. MSBuild task warnings are errors too
(`MSBuildTreatWarningsAsErrors` in the root props), so SourceLink's report of missing repository
metadata fails the build: that is a failing gate to report, never one to suppress.

Test options are written directly on the command, not after a `--` forwarding separator, so their
scope is unambiguous. A tree-node filter takes OR only inside one path segment:
`"/*/*/(ClassA|ClassB)/*"`, never `"/*/*/ClassA/*|/*/*/ClassB/*"`. A category-filtered solution run
can return the zero-test exit code 8 for a module with no matching tests, so a category run targets
the owning project with `--project` instead.

Slopwatch runs from the root tool manifest against the repository's own baseline in
`.slopwatch/baseline.json`, which holds no entries; `analyze` refuses to run without a baseline. It
excludes local scratch work because that is not repository-authored product surface. A failed,
skipped, weakened, or unrun gate must be reported honestly.

The full gate is what CI runs. It runs locally only when a handoff asks for it or a CI failure
needs reproducing. Any local test run, check or gate, runs on an otherwise idle machine: the
real-process tests have hang guards, and a concurrent build, package install or second gate on the
same machine can turn them red without any change to the code. A red result is recorded before any
rerun.

A change limited to Markdown and `LICENSE` needs no local check.

A fresh clone or worktree runs `dotnet tool restore` once before its first local check, which
makes the repository's local tools, Slopwatch among them, available.

The build is the semantic analyzer wall: compiler diagnostics, build-enforceable IDE rules, SDK CA
rules, third-party analyzers, and source-generator diagnostics run there for every compilation. The
local check builds with the wall on. CI runs it once, in the lint job's Linux build; the three OS
legs build with `RunAnalyzers=false`. Every OS compiles the same LF-normalized sources for the same
single target framework, so a second analysis per OS would find nothing new and only cost build
time.
The whitespace pass separately retains UTF-8, LF, final-newline, trailing-whitespace, and
syntax-whitespace enforcement. The warning-level style pass retains deterministic import
organization and every configured Roslyn style error, including rules classified
`EnforceOnBuild.Never`, without rerunning the SDK CA and third-party analyzer set. It deliberately
uses no diagnostic allow-list so future warning/error style rules remain covered automatically.

### Separate passes

The managed pass above never implies a native or package result. Two kinds of test run in passes
of their own, never beside the managed pass:

- **Native publishing.** A native publish runs one ILC compile that loads every core of the host
  for a sustained stretch, which the real-process tests and their hang guards cannot run beside.
  The native pass publishes the MCP server for one RID on a matching host, then runs the same
  executable tests directly against the published native executable, never a managed assembly,
  `dotnet run` or the AppHost. A native path that is missing or is not native fails the pass
  instead of falling back to the managed product.
- **Packages.** Tests that need SDK-produced packages from the feed carry the `Package` category.
  Unit tests of the package inspector and its policy over archives built in the test file system
  stay in the managed pass. The managed pass excludes that category with
  `--treenode-filter "/*/*/*/*[Category!=Package]"`; the local package pass selects `PackageContentsTests`
  and `PackageAcquisitionTests` on the owning product-test project against the complete feed produced
  after the native pass. Remote `ChannelAcquisitionTests` run only in the separate channel pass.

`../architecture/hosting-and-packaging.md` owns the native and package contract;
`../ROADMAP.md` records which of these passes exist today.

### Native verification and staging

On an otherwise idle matching host, after the managed local check, run:

```text
dotnet run tools/native.cs -- verify --rid win-x64
dotnet run tools/native.cs -- control --rid win-x64
dotnet run tools/native.cs -- stage --rid win-x64 --publish-directory artifacts/native/win-x64/publish
dotnet run tools/native.cs -- pack-child --rid win-x64 --evidence artifacts/native/win-x64
dotnet run tools/native.cs -- validate --receipts artifacts/native
```

Use the manifest RID matching the actual OS, OS architecture, process and SDK architecture. The
Windows prerequisites are in the architecture canon. No prerequisite is installed by the tool.
`verify`, `control`, `stage` and `pack-child` must exit zero. The last command must exit 2 for a
local one-RID set, naming the other five missing receipts in manifest order; only a complete hosted
set may exit zero.
`--output <dir>` selects verify's owned evidence directory; stage uses the publish directory's
parent. `control` owns its temporary publication and cleanup. Its successful result requires
publish exit zero and exactly the missing Native AOT export refusal for the executable file,
independent of companion-file detection.

The tool supplies `OPENCODE_MCP_TEST_EXECUTABLE` and `OPENCODE_MCP_TEST_EXPECT_NATIVE=true` to the
existing owning project's `ExecutableTests`; the resolver strips both from each product child's
environment and never substitutes managed output. Linux container execution additionally supplies
`OPENCODE_MCP_TEST_CONTAINER_IMAGE` with the exact manifest runtime image; the resolver strips all
three selectors. Each of the published, normalized and extracted
runs uses `--no-build --treenode-filter "/*/*/ExecutableTests/*" --minimum-expected-tests 1`, TRX
under `artifacts/test-results/native/<RID>/`, `TUNIT_DISABLE_ARTIFACT_UPLOAD=true`, and the session
timeout/hang-dump options below. Test-runner preparation and native compilation are sequential.

Review the warning scan, effective properties, native export inspection, executable digest,
complete payload inventory, archive round trip and all three test totals. Native acceptance before
queue entry requires all six same-run manifest receipts, matching common build inputs and the
two Linux runtime-independence proofs. It is separate from `CI result`; it adds no required check
or dependency to that job. Local Windows evidence proves neither hosted execution nor Linux/macOS
support, package acquisition or installation.

The `Native` workflow uses prepare → Linux builder/Windows/macOS build jobs → Linux host proof
jobs → native validation. After native validation, `packages` builds the complete feed and its
consumer legs run; independently, `release-assets` assembles the release asset set. The release-assets
job has a 30-minute guard and uses the preparation job's exact SDK. Other job guards are 30 minutes
for preparation and validation, 150 minutes for builds, and 90 minutes for Linux proof, packages and
consumers. Every step invoking tests has a 35-minute guard; tool-owned test processes have a
30-minute outer guard around the 25-minute session bound. Listing steps have a five-minute guard
and do not rerun tests. All test invocations disable TUnit's automatic artifact upload and use unique
phase result directories. Every job owns `DOTNET_INSTALL_DIR` under `runner.temp`; Windows sets
`TEMP` and `TMP` there before SDK setup. Linux proof runs on hosts, not in the builder container,
and executes downloaded binaries only from the verified extracted tar.

Every `native-*` artifact has 90-day retention on `workflow_dispatch` runs and seven-day retention
otherwise. This includes evidence, symbols, test results, package feeds, release assets and process
snapshots; upload failures remain blocking. Artifact names are unique per job/per RID.
Completed receipts are uploaded only after successful proof; validation
has no `always()` override and starts only when all needed jobs succeed. Both workflows prebuild
the file-based entry in a normal step before tests. Process snapshots run with `always()` and
`--no-build` through the shared `snapshot` subcommand in both workflows. Upload errors remain
blocking. The validation success and its log establish the complete-set result; no index file or
separate job-result input stands in for that dependency graph. The native workflow must be green
at the pull request's head before queue entry, including hosted parsing and all six RIDs.

### Package pass

After the complete native pass, consolidate its completed evidence and run:

```text
dotnet run tools/native.cs -- pack-root --receipts artifacts/native-receipts --feed artifacts/feed
dotnet test --project tests/OpenCode.Mcp.Tests/OpenCode.Mcp.Tests.csproj -c Release --no-build --treenode-filter "/*/*/(PackageContentsTests|PackageAcquisitionTests)/*" --report-trx --results-directory artifacts/test-results/package/product --minimum-expected-tests 1 --timeout 25m --hangdump --hangdump-timeout 15m --hangdump-type Mini
```

Build the owning test project in Release before the no-build test pass. Set
`OPENCODE_MCP_TEST_FEED` explicitly to the complete feed directory before testing; a relative value
resolves against the repository root, not the runner's working directory. An absent, empty or
whitespace-only input, or a nonexistent feed directory, fails with the instruction to build
`native-package-feed` with `pack-root` after the native pass and set `OPENCODE_MCP_TEST_FEED` to its
directory. An incomplete feed fails with the feed validator's diagnostics. There is no skip or
default feed. The Package category lives in the product-test project only. Its input-refusal rows
pass their values explicitly and never modify the process environment.

Acquisition tests additionally require explicit `OPENCODE_MCP_TEST_RID`, resolved only when an
acquisition test starts. Absent, blank, unknown and host/private-dotnet-mismatched values fail
instructively, never default or skip. Package-contents inspection needs only the feed. Acquisition
runs on a host matching the claimed manifest RID and uses the private SDK without shared ASP.NET
Core; unownable ambient NuGet configuration fails before an acquisition launch.

The acquisition tests run these two command vectors, which maintainers can also run by hand:

```text
dotnet dnx -v minimal OpenCodeDotNet.Mcp@<version> --configfile <config> --yes -- --transport stdio
dotnet tool install OpenCodeDotNet.Mcp --version <version> --tool-path <tool-path> --configfile <config> --framework net10.0 -v minimal
```

`<version>` is the feed's product version, and `<tool-path>` is a directory the caller owns.
`<config>` is a NuGet configuration that names only the complete local feed; see the
[complete-local-feed contract](../architecture/hosting-and-packaging.md#root-package-and-complete-local-feed).
Acquisition needs the .NET SDK, and the native server needs no installed .NET runtime; see the
[package prerequisites](../architecture/hosting-and-packaging.md#nuget-mcp-tool-package).
The installed command is a `.cmd` file on Windows and a symbolic link on Linux and macOS; see the
[installed-command contract](../architecture/hosting-and-packaging.md#payload-candidate-and-acquisition).
The second command carries `--framework net10.0` because without it, on Windows Arm64 and macOS
Arm64, the SDK asks for the x64 apphost pack, which a feed holding only this product's packages
cannot supply.
The acquisition tests run these vectors inside the
[owned-process isolation](testing-style.md#6-owned-processes-are-hermetic); a run by hand uses the
caller's own NuGet stores unless the caller isolates them, so it does not establish cold acquisition
or prove that the launched payload came from this feed.

Package acceptance requires seven real SDK-produced packages, their coherent native receipts and
payload manifests, exact root RID mappings, byte-identical packed MCP manifests, and whole-payload
path and SHA-256 equality. The `packages` job downloads its own run's six completed artifacts,
packs and validates the feed, runs `PackageContentsTests` with `/*/*/PackageContentsTests/*`, and
uploads `native-package-feed` only on success. Its failure listing uses the same class filter.
It always records a process snapshot and uploads package test results. A failed test gets a
five-minute listing step, not a rerun. Native validation, package inspection, release-asset assembly
and all six consumer legs must succeed at the pull request's head before queue entry. `CI result`
remains the only required check.
Contents-only acceptance makes no acquisition or packaged-startup claim. The local acquisition
class additionally exercises cold exact-version DNX, owned tool-path/shim startup, the installed
HTTP entry, missing-host-implementation refusal on both routes, complete payload equality, observed
native process paths and owned cleanup. Local matching-host results establish only that host's
development evidence; they are no hosted, six-RID, final-candidate or publication claim.

The `matrix` subcommand projects `consumer-matrix` from every manifest row in manifest order, with
only `rid` and `runner`; no second RID list is maintained. After `packages` succeeds, each consumer
leg runs on its matching RID host, downloads its own workflow run's complete `native-package-feed`
to `artifacts/feed`, and runs `/*/*/(PackageContentsTests|PackageAcquisitionTests)/*` with `OPENCODE_MCP_TEST_FEED` set to that
directory and `OPENCODE_MCP_TEST_RID` set to its matrix RID. A failed test after a successful build
gets the same class filter's test listing under a five-minute guard unless cancelled, never a rerun.
Each leg always records the prebuilt tool's post-test process snapshot and uploads
`native-process-snapshot-consumer-<RID>` and `native-package-test-results-consumer-<RID>`, with
retention governed by the native artifact rule above and missing files treated as errors. Package
results come only from `artifacts/test-results/package/**`; owned homes, caches, private SDK copies
and the feed are not uploaded by consumers.

Release-asset acceptance requires exactly six version-named archives and `SHA256SUMS`. Each archive
is byte-identical to its staged input and matches its receipt digest; checksum lines follow manifest
order. The pull request's hosted `Native` run proves assembly and upload, not publication, installer
behaviour or a live-channel download. Dispatch retention is confirmed on an authorized manual
candidate run.

### Publication pass

Publication is separate from managed, native and local-package acceptance. A publication candidate
is one successful manual Native run on master, identified by its full commit SHA and run ID.
Publication consumes that run's validated package feed and release-assets artifact unchanged.

The manual release workflow checks candidate provenance, evaluated version, workflow-tree equality,
target absence and artifact integrity before publication. A dry run performs those checks without
publication privileges or an environment deployment. Its release-identity check proves absence of
published releases and the tag, not drafts; the `github-release` job checks drafts with
`contents: write` before creating anything.

Publishing requires the `release` environment and the maintainer's separate authorization. The
environment binds NuGet.org publication through its Trusted Publishing policy. For GitHub Packages
and GitHub Releases, the gate is procedural: a branch copy of a workflow can request `packages: write`
or `contents: write` with `GITHUB_TOKEN`. The lane therefore dispatches no workflow but master's
`release.yml` for publication.

Real-channel acceptance requires every manifest RID's verification leg to succeed against the selected
published channel. Feed acceptance uses cold global-tool and DNX acquisition and complete
running-payload path/SHA-256 equality with candidate evidence. NuGet.org's repository-signed package
envelope is not compared with the original package digest. Release acceptance checks downloaded archive
hashes against both the published checksum file and independent candidate receipts before execution.

The manual `verify-channel.yml` validates the Native candidate, waits for exact channel presence,
and selects only `ChannelAcquisitionTests` for feeds or `ExecutableTests` for release archives.
Remote feed tests carry `Category("Package")` but never run in the local package pass or local check.
They require explicit channel, owned configuration, candidate reference feed and matching RID inputs;
missing inputs fail rather than skip. The reference feed supplies payload comparison evidence, not
an acquisition source. GitHub Packages metadata and installs use the job's `GITHUB_TOKEN` with
`packages: read`; every leg requires all seven packages to be public as seen by this repository's
Actions access. Metadata failures fail closed. A successful aggregate retains manifest-ordered,
nonzero test totals and result-artifact links in `channel-verification` for promotion.

Secret-bearing channel jobs retain the existing test guards but upload only selected
credential-redacted TRX and process snapshots. Credential files, acquisition stores and memory dumps
are not uploaded. Existing credential-free CI artifact policy is unchanged.

### Linux builder verification

The builder recipe and publication contract are owned by
`../architecture/hosting-and-packaging.md`. On a local amd64 Docker host, verify the recipe once
without registry login or push:

```text
docker buildx build --load --platform linux/amd64 -f docker/linux-builder.Dockerfile -t opencode-mcp-linux-builder:local-check .
docker run --rm opencode-mcp-linux-builder:local-check pwsh -NoProfile -Version
docker image rm opencode-mcp-linux-builder:local-check
```

Require every exit code to be zero and confirm the local tag is gone. The Dockerfile prints and
checks the architecture, glibc 2.35, compiler, linker, pinned PowerShell version, Git and curl; any
failed probe fails the build. The separate container command must return the recipe's PowerShell
version. This check proves only the local amd64 builder, not arm64 or native product execution.

Dispatch `linux-builder.yml` only from approved source on `master`. Its build
matrix checks OS/process architecture and `uname -m` before native builds; both legs must succeed
before index merge. Only the build and merge jobs have `packages: write`, alongside `contents: read`;
the workflow default is `contents: read`. Build jobs are bounded to 30 minutes and merge to 5 minutes.
Intermediate digest artifacts and one consolidated run-evidence artifact have seven-day retention.
The latter and the step summary contain the index digest, both runtime platform digests, source
commit and `docker buildx imagetools inspect` output. Upload failures block success. Review that
evidence, then confirm anonymous access to the public index and both platform images before using
the index digest in native verification. A local build is not hosted arm64 or publication evidence.
This dispatch is separate from `CI result` and does not change that check's dependencies.

### Focused executable checks

Focused managed-process verification uses the actual Release product, not `dotnet run` or the
AppHost:

```text
dotnet test --project tests/OpenCode.Mcp.Tests/OpenCode.Mcp.Tests.csproj -c Release --treenode-filter "/*/*/(WireTranscriptTests|ProductExecutableTests|RecordingDuplexStreamTests|OwnedMcpProcessTests)/*" --minimum-expected-tests 1
dotnet test --project tests/OpenCode.Mcp.Tests/OpenCode.Mcp.Tests.csproj -c Release --treenode-filter "/*/*/ExecutableTests/*" --minimum-expected-tests 1
dotnet test --project tests/OpenCode.Mcp.Tests/OpenCode.Mcp.Tests.csproj -c Release --treenode-filter "/*/*/(McpAppTests|HttpTransportTests)/*" --minimum-expected-tests 1
```

`OPENCODE_MCP_TEST_EXECUTABLE` selects an existing non-DLL executable directly for the executable
class; an invalid explicit path fails instead of falling back. This selector does not inspect the
native image or establish native, RID, package or runtime-independence evidence. Without it, the
tests resolve the built Release DLL from the test executable's location. Focused runs do not replace
the local check.

### Focused AppHost checks

Set an owned `ASPIRE_HOME` before building or testing the AppHost, not only before the CLI check.
Keep it until all consumers of the generated bundle paths finish. Bundle acquisition, real-profile
backchannel/certificate effects and the limits of `--isolated` are defined in
`../architecture/hosting-and-packaging.md`. No global Aspire installation is needed.

```text
dotnet test --project tests/OpenCode.Mcp.AppHost.Tests/OpenCode.Mcp.AppHost.Tests.csproj -c Release --treenode-filter "/*/*/(AppHostTests|OwnedAppHostTests)/*" --minimum-expected-tests 1
```

Record the resolved package graph, SDK, generated `Projects.*` metadata, bundle marker and DCP
version. The test builder runs the entry point in-process; its product runs closed-box under DCP.
The focused tests do not replace the local check, native proof or package proof.

The local Aspire tool also needs a real lifecycle check on an otherwise idle host. First confirm no
existing run targets this exact AppHost path: `start` can stop an existing instance, and
`stop --apphost` is path-scoped, not session-ID-scoped. Under the same owned Aspire home, set
`ASPIRE_DASHBOARD_DATA_DIRECTORY` beneath it, then run:

```text
dotnet tool run aspire start --apphost src/OpenCode.Mcp.AppHost/OpenCode.Mcp.AppHost.csproj --isolated --format Json --non-interactive
dotnet tool run aspire wait mcp --apphost src/OpenCode.Mcp.AppHost/OpenCode.Mcp.AppHost.csproj --status healthy --timeout 120 --non-interactive
dotnet tool run aspire describe mcp --apphost src/OpenCode.Mcp.AppHost/OpenCode.Mcp.AppHost.csproj --format Json --non-interactive
```

Bound the foreground CLI processes, drain stdout/stderr concurrently, and parse launch and
resource JSON in memory without retaining dashboard login URLs. Retain the started process handles
and the discovered endpoint. Always stop the exact owned run in `finally`, inspect the exit code,
await retained exits, and require a refused fresh connection:

```text
dotnet tool run aspire stop --apphost src/OpenCode.Mcp.AppHost/OpenCode.Mcp.AppHost.csproj --non-interactive
```

Never use `--force` or stop another session. Record the actual bundle and outside-repository writes;
do not collect profile directories, certificate material or dashboard databases as artifacts.

## Continuous integration

This is the decided CI contract; `../ROADMAP.md` records whether it is in place.

- **Triggers.** Pull requests and merge-queue entries (`merge_group`) always start the workflow,
  with no path filter, so the required check reports on every change. Pushes to the default branch
  ignore Markdown and `LICENSE`. A commit never carries `[skip ci]`.
- **Changes job.** A `changes` job classifies the diff. A change limited to Markdown and `LICENSE`
  skips the lint and build/test jobs inside the run instead of skipping the run, which would leave
  the required check missing. Pull requests compare their synthetic merge ref with its first
  parent; merge groups compare with their declared base, including preceding queue entries.
  Git supplies NUL-separated paths with rename detection disabled, so a source-to-Markdown rename
  still selects code. Only an ordinal `.md` suffix or the exact root `LICENSE` path is exempt.
- **Lint job.** One Linux job restores the local tools, runs Slopwatch, the Release build with the
  analyzer wall, both format passes, and the "Verify native tool entry" step (the separate file-app
  build and empty-receipt smoke).
- **Aspire home input.** The CI task supplies an owned `ASPIRE_HOME` under each job's temporary
  directory before every build and test command, and retains it through that job's tests. Dashboard
  data stays beneath that home. The AppHost SDK's normal build acquires its paired CLI bundle
  through `DnxPinned`; no global Aspire, Docker, native compiler or certificate-trust preparation
  is required for this HTTP-only, container-free graph. Build output is never shared between OSes.
- **Build/test legs.** Windows, Linux and macOS legs (`windows-2025`, `ubuntu-24.04`, `macos-15`)
  build in Release with `-p:RunAnalyzers=false` and run every test project under the session timeout
  and hang dump below, with `fail-fast: false`. Each sets `TUNIT_DISABLE_ARTIFACT_UPLOAD: true` on
  every test step so TUnit does not upload with the ambient runtime token. These managed legs
  establish no native coverage for any RID.
- **`CI result`.** One fail-closed job named `CI result` runs with `if: always()`, needs every
  other job, and is the one required check: a skipped matrix leg reports under its unexpanded name,
  so the legs cannot be required one by one. A change that reaches code needs every job green; a
  documentation-only change needs the changes job green and the skipped jobs skipped.
- **Merge queue.** Everything lands on the default branch through the merge queue, which requires
  the `CI result` check (`workflow.md`).
- **Least privilege and bounds.** The workflow has `contents: read`; a superseded pull request run
  is cancelled, while each default-branch push has a per-commit concurrency group so a newer commit
  does not cancel its pending or running workflow; every job has a bounded timeout. The .NET SDK is set
  up from `global.json` and each job logs the version that resolved. Steps run in `pwsh` and check
  every command's exit code. Job bounds are 5 minutes for classification, 30 for lint, 55 for each
  build/test leg and 5 for the aggregate check.
- **Artifacts.** Each build/test leg uploads `artifacts/test-results/**` with `if: always()`, an
  OS-specific artifact name and seven-day retention. This directory holds the test platform's TRX
  files, TUnit HTML and JSON reports and, on a hang, hang-dump files; all are uploaded. No redaction
  or size-processing step is needed: the workflow uses no secret, has only `contents: read`, sets
  `TUNIT_DISABLE_ARTIFACT_UPLOAD` on every test step to prevent automatic upload without disabling
  report generation, and runs on ephemeral hosted runners. The owned `ASPIRE_HOME`, real profile
  and dashboard data are never beneath this path or uploaded. Test listings stay in the job log.
  Upload failures remain blocking and never turn a failed test green; there is no `continue-on-error`.
- **Actions.** Actions are referenced by major version tag (`actions/checkout@v7`,
  `actions/setup-dotnet@v6`, `actions/upload-artifact@v7`); a major tag is not an immutable SHA pin.
- **Dependency review.** The `dependency-review` workflow (Dependency audit, above) runs on every
  pull request with `contents: read`, using `actions/dependency-review-action@v5` without PR comments.
  `CI result` does not cover Dependency Review; the maintainer checks it before queue entry.

The matrix's exact test command is:

```text
dotnet test --solution OpenCode.Mcp.slnx --configuration Release --no-build --treenode-filter "/*/*/*/*[Category!=Package]" --report-trx --results-directory artifacts/test-results --minimum-expected-tests 1 --timeout 25m --hangdump --hangdump-timeout 15m --hangdump-type Mini
```

After a failed test step, if the build succeeded and the run was not cancelled, a bounded diagnostic
step runs `dotnet test --solution OpenCode.Mcp.slnx --configuration Release --no-build --treenode-filter "/*/*/*/*[Category!=Package]" --list-tests`.
This lists tests without executing them again. Both steps disable TUnit artifact upload.

`CI result` accepts exactly these dependency states; missing, failed, cancelled or invalid states
fail closed:

| Changes result | Code output | Lint result | Build/test result | Verdict |
|---|---|---|---|---|
| `success` | `true` | `success` | `success` | Pass |
| `success` | `false` | `skipped` | `skipped` | Pass |
| Any other combination | | | | Fail |

Review workflow syntax before submission and require hosted parse validation; a local build does
not parse workflow YAML.

A prerequisite that is missing or coverage that is unavailable is a visible blocker, never a silent
skip and never a green support claim. Native and package lanes are a separate manifest-driven
workflow over the six approved RIDs; `../architecture/hosting-and-packaging.md` owns it.
Its release-asset acceptance and event-dependent retention follow the
[native staging and package-pass rules](#native-verification-and-staging) above; managed CI's
triggers, permissions and retention are unchanged.

### Session timeout and hang dump

Every CI test step runs under a 25-minute session timeout and a hang dump:
`--timeout 25m --hangdump --hangdump-timeout 15m --hangdump-type Mini`. The session timeout makes a
hung module fail inside its step instead of at the job timeout. That timeout runs inside the test
host, and a hang that stops the host's own timer stops it too. So each test step also carries
`timeout-minutes: 35`, which the runner enforces from outside the host: a hung step fails within 35
minutes, before the merge queue's check timeout drops the pull request.

A test host with no activity for 15 minutes is taken as hung. Microsoft.Testing.Platform's hang-dump
extension, whose controller is a process of its own, then lists the tests still running, writes a
mini dump of the host into the step's results directory, which the job uploads, and ends the host.
Every test project references the extension: `--hangdump` exits with code 5 in a module without it.
An expired session exits with code 7 and keeps the TRX of the rows that finished, but it doesn't
name the rows still in flight. When a test step fails, the job also writes that step's test listing
to the job log, and the rows in flight are among those listed but not recorded.

## Owned processes

A gate that starts a process owns it. Every process a test, fixture or script starts is ended and
awaited with `Process.WaitForExitAsync`, its readers are joined, and its owned roots are removed
afterwards. The `Process` instance is retained until disposal; the owner ends only processes it
started. A stdio MCP server proves a clean exit by ending
after end of input, an HTTP MCP server by its endpoint refusing connections after stop, and an
AppHost run by the recorded product processes having exited. A missing prerequisite fails fast and
names the missing requirement. `testing-style.md` §6 owns the authoring rules.

## Performance

Performance is a standing concern weighted by artifact. The MCP server's request paths avoid
needless allocation and redundant parsing, copying, buffering, and enumeration. Do not speculate:
a performance claim carries exact before/after evidence measured in one environment, and a
performance change is not disguised as a refactor (`workflow.md`). A benchmark project arrives only
with a performance claim that needs one.
