# Engineering Quality Gates

Date: 2026-11-05

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
judged before it merges. It updates nothing.

## Completion gate

"Builds" and "works" are different claims. A pull request is ready for the merge queue when the CI
its own head triggers is green: the lint job and the three operating-system legs run every step of
the full gate below. The change is complete when the CI its merge commit triggers on `master` is
green. Which changes skip those jobs, and what a merge requires, are in `workflow.md`.

Before a pull request opens, run the local check from the repository root. It catches failures
before they cost a CI run. A current handoff may add scope-specific checks but cannot weaken it.
The local check builds with the analyzer wall on and runs every test module of the solution:

```powershell
dotnet tool restore
dotnet tool run slopwatch analyze --exclude ".scratchpad/**" --fail-on warning
dotnet build OpenCode.Mcp.slnx --configuration Release
dotnet format whitespace OpenCode.Mcp.slnx --verify-no-changes --no-restore
dotnet format style OpenCode.Mcp.slnx --verify-no-changes --no-restore --severity warn
dotnet test --solution OpenCode.Mcp.slnx --configuration Release --no-build --report-trx --results-directory artifacts/test-results --minimum-expected-tests 1
```

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
- **Packages.** Tests that inspect packages or acquire the packaged MCP server carry the `Package`
  category. Once that category exists, the managed pass's test command adds
  `--treenode-filter "/*/*/*/*[Category!=Package]"`, and the package pass runs the category on its own
  against the packages the native pass produced.

`../architecture/hosting-and-packaging.md` owns the native and package contract;
`../ROADMAP.md` records which of these passes exist today.

## Continuous integration

This is the decided CI contract; `../ROADMAP.md` records whether it is in place.

- **Triggers.** Pull requests and merge-queue entries (`merge_group`) always start the workflow,
  with no path filter, so the required check reports on every change. Pushes to the default branch
  ignore Markdown and `LICENSE`. A commit never carries `[skip ci]`.
- **Changes job.** A `changes` job classifies the diff. A change limited to Markdown and `LICENSE`
  skips the lint and build/test jobs inside the run instead of skipping the run, which would leave
  the required check missing.
- **Lint job.** One Linux job restores the local tools, runs Slopwatch, the Release build with the
  analyzer wall, and both format passes.
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
  is cancelled, a default-branch run never is; every job has a bounded timeout. The .NET SDK is set
  up from `global.json` and each job logs the version that resolved. Steps run in `pwsh` and check
  every command's exit code.
- **Artifacts.** Jobs upload only explicit, bounded and redacted TRX files, hang dumps and owned
  process logs to `artifacts/test-results/`, with `if: always()` and seven-day retention; never an
  Aspire dashboard store, its tokens, an `.aspire` directory or user profile data. A failed upload
  never turns a failed test step green.
- **Actions.** Actions are referenced by major version tag (`actions/checkout@v7`,
  `actions/setup-dotnet@v6`, `actions/upload-artifact@v7`); a major tag is not an immutable SHA pin.
- **Dependency review.** The `dependency-review` workflow (Dependency audit, above) runs on every
  pull request with `contents: read`.

A prerequisite that is missing or coverage that is unavailable is a visible blocker, never a silent
skip and never a green support claim. Native and package lanes are a separate manifest-driven
workflow over the six approved RIDs; `../architecture/hosting-and-packaging.md` owns it.

### Session timeout and hang dump

Every CI test step runs under a 25-minute session timeout and a hang dump:
`--timeout 25m --hangdump --hangdump-timeout 15m --hangdump-type Mini`. The session timeout makes a
hung module fail inside its step instead of at the job timeout. That timeout runs inside the test
host, and a hang that stops the host's own timer stops it too. So each test step also carries
`timeout-minutes: 35`, which the runner enforces from outside the host: a hung step fails within 35
minutes, before the merge queue's check timeout drops the pull request.

A test host that runs for 15 minutes is taken as hung. Microsoft.Testing.Platform's hang-dump
extension, whose controller is a process of its own, then lists the tests still running, writes a
mini dump of the host into the step's results directory, which the job uploads, and ends the host.
Every test project references the extension: `--hangdump` exits with code 5 in a module without it.
An expired session exits with code 7 and keeps the TRX of the rows that finished, but it doesn't
name the rows still in flight. When a test step fails, the job also writes that step's test listing
into the results directory, and the rows in flight are among those listed but not recorded.

## Owned processes

A gate that starts a process owns it. Every process a test, fixture or script starts is ended and
waited for until it has closed its handles, its readers are joined, and its owned roots are removed
afterwards; it ends only processes it started. A stdio MCP server proves a clean exit by ending
after end of input, an HTTP MCP server by its endpoint refusing connections after stop, and an
AppHost run by the recorded product processes having exited. A missing prerequisite fails fast and
names the missing requirement. `testing-style.md` §6 owns the authoring rules.

## Performance

Performance is a standing concern weighted by artifact. The MCP server's request paths avoid
needless allocation and redundant parsing, copying, buffering, and enumeration. Do not speculate:
a performance claim carries exact before/after evidence measured in one environment, and a
performance change is not disguised as a refactor (`workflow.md`). A benchmark project arrives only
with a performance claim that needs one.
