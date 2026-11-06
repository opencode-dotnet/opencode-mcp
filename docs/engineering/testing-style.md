# Testing Style — authoring tests

Date: 2026-11-06

Binding authorship style for every test in this repository. `quality-gates.md` owns the
current assurance posture and completion gates; operational build-out state belongs in
`../ROADMAP.md`. This document governs how tests are written inside that architecture and
applies `coding-style.md` to test code - test infrastructure is code and follows every rule
there.

## 1. Test infrastructure is a first-class citizen

Test setup is designed, reviewed, and refactored like product code — never accumulated
as copy-paste arrange blocks. The canonical shape is a scenario, context, and builder,
sized to need:

- **Inline scenarios are the default for small, one-off variations.** The shared scenario
  mechanism owns assembly (a fresh fake filesystem, owned run roots, launch inputs, and the
  context); the test supplies only a domain-aware builder action. A one-line variation does not
  earn a class and file.
- **Named scenario classes are promoted, not automatic.** A `sealed` scenario class earns
  one-concept/one-file status when at least one of these holds: the same arrangement is
  reused across test classes; setup is non-trivial (roughly more than five fluent
  statements, or an embedded fixture plus additional shaping); or the situation carries
  durable domain/landmark identity used across slices.
- **Domain-aware fluent builders.** Scenario state is expressed in domain verbs (a launch with a
  transport, an environment entry, an owned run directory), not raw file writes or dictionary
  edits scattered through test bodies. Builders compose over the canonical filesystem fake and
  follow coding-style rules (sealed, no tuple returns, no concrete-collection parameters);
  where they live follows the test layout below.
- **Grow on demand.** The scenario mechanism, context, and builder are the pattern; named
  scenarios, test hosts, and builder vocabulary grow only when a promotion/reuse signal
  fires. No speculative infrastructure, and no infrastructure duplication either: the
  second copy-paste of an arrange block first grows a reusable builder verb or preset;
  class promotion follows the rule above rather than file-count convention.

### Test layout

- A test mirrors its subject's source path with every `Internal` segment dropped:
  `Hosting/TransportSelector.cs` is tested in `Hosting/TransportSelectorTests.cs`,
  `Tools/Bootstrap/BootstrapTools.cs` in `Tools/Bootstrap/BootstrapToolsTests.cs`, and
  `Hosting/Internal/X.cs` would be tested in `Hosting/XTests.cs`. Test namespaces follow test
  folders, so a test folder named like a BCL type shadows it there: tests in such a folder qualify
  the BCL type. Two source types never project onto one test path.
- `OpenCode.Mcp.Tests` mirrors `src/OpenCode.Mcp/`; the tests of the ServiceDefaults project live
  in it under `ServiceDefaults/`. `OpenCode.Mcp.AppHost.Tests` holds the closed-box AppHost tests
  and their own `Support/`.
- A helper or fixture used by one slice lives in that slice's `Support/` or `Fixtures/`. Helpers
  and fixtures used by several slices stay in the test project's root `Support/` and `Fixtures/`.
  Embedded fixtures retain their `Fixtures.` resource-name prefix through explicit `LogicalName`
  metadata. There is no separate test-support project and no shared test library.
- The mirror rule also applies to test infrastructure: `Support/X.cs` is tested in
  `Support/XTests.cs`. A subject with several test files keeps those files beside the subject.

## 2. Filesystem rule

**Testably supplies the repository's only filesystem double** through the shared
`System.IO.Abstractions.IFileSystem` contract (sealed decision; the independent TestableIO
analyzer enforces it wherever an abstractions assembly is referenced — even test code reaches
`Path` through `IFileSystem.Path`):

- Unit and contract tests: `Testably.Abstractions.Testing.MockFileSystem`, assembled
  through the scenario builders.
- Tests that run a real process or server, and full-artifact smoke tests:
  `Testably.Abstractions.RealFileSystem`.
- Raw `System.IO` never appears in test code, and no second filesystem fake is ever
  introduced — one canonical fake per repository. Fault injection decorates that fake: an
  NSubstitute `IFileSystem` that delegates to the Testably double and fails the chosen calls is
  the same fake with a failure, not a second one.
- Tests consume `IFileSystem` directly. Product code that touches the filesystem does so through
  an injected `IFileSystem` seam (`coding-style.md` §2), so its test double is the same fake.

## 3. Test data policy — no inline dumps

**Raw JSON/XML/string-literal dumps pasted inline in test methods are forbidden.** A
test body states *intent*; the data it runs on lives in one of three sanctioned homes:

1. **Embedded fixture files** — in the `Fixtures/` folder of the slice that uses them, or the test
   project's root `Fixtures/` when several slices share them, loaded through a resource-loader helper
   by name. The default for wire-shaped data such as MCP protocol frames:
   one small file per quirk, named for the construct it isolates, reviewable on its own.
2. **Typed builders** — for variation families where files would multiply: red tests
   composing a valid base plus exactly one offending construct through the builder. The
   variation reads as a domain statement in the test body, not as a diff between two
   pasted strings.
3. **Centralized constants** — small, single-file-scoped values in a static
   `<Domain>Data` class. Never the same literal repeated across test methods.

A short literal is acceptable only when the literal *is* the subject under test (a
transport name in a transport-selector test). Data that describes structure always
goes through 1 or 2.

Representative wire fixtures prove the behavior of the code that reads them, such as the
transcript parser that checks a recorded stdout stream, not protocol breadth. Real observed frames
are promoted into the fixture set when available. The protocol proof itself comes from real
exchanges with the running MCP server, not from fixtures. Documentation states the observed fixture
count rather than projecting completeness onto it.

The protocol fixture set contains five files: the HTTP recorder fixture, three complete stdio
response frames (discovery, tool list and tool call), and malformed stdout. These representative
inputs exercise observation and fragmentation; they are not a protocol conformance corpus.

## 4. Fakes and mocks

- **Substitute at seams only** (NSubstitute over interfaces). Records, tool types, and
  pure classes are constructed, never mocked.
- **Never hand-build types you do not own** where a published contract can be exercised
  instead: protocol assertions read what the MCP C# SDK client returns from the real MCP server and
  the bytes recorded on the wire; a test never constructs SDK protocol types to stand in for a
  server response, and never substitutes the MCP server with an in-memory or delegate server. This
  is the authorship side of the fakes-only-for-published-contracts posture (`quality-gates.md`).
- **No giant shared `TestBase`.** Shared behavior lives in scenario bases and small
  single-responsibility helpers; a base class accumulating unrelated conveniences is
  split like any other class (§1 red flags apply to test code).
- **If mocking hurts, fix the seam, not the test.** A painful mock setup means the
  production boundary is wrong — redesign the seam instead of layering test helpers
  over it.

## 5. TUnit mechanics

- Naming: `{Symbol}_Should_{Expected_Behavior}[_When_{Condition}]`. Symbol names stay intact as one
  token (`Parse`, `NuGet`); every other word is underscore-separated and starts with a capital.
  Test classes are `{Sut}Tests`, one class per file; promote a SUT to a folder with per-area test
  files only when it outgrows comfortable navigation.
- Data-driven cases use TUnit's `[Arguments]` rows on one test method, one row per case; a family
  that would repeat structure goes through a builder (§3).
- TUnit creates a fresh instance per test: setup belongs in the constructor or
  `[Before(Test)]`; no state carried between tests, no shared mutable fields.
- In-process host tests pin their decisive configuration on the command line, including the
  environment name, listening URLs, and an empty `OTEL_EXPORTER_OTLP_ENDPOINT`, so those values win
  over the developer's environment.
- Assertions state intent in the test body. Behavior tests never snapshot; the repository has no
  snapshot package, and a snapshot use is a maintainer decision with its own reviewed baseline.
- TUnit runs tests in parallel by default. Parallel keys and `[NotInParallel]` order tests inside
  one test application only, while the two test applications of a solution run can run at the same
  time. A test class that starts real processes carries `[NotInParallel("processes")]`; every
  process test uses dynamic loopback ports and its own owned roots, so the two applications may
  overlap. When contention appears, run the applications one after another rather than widening a
  key. A test whose load a runner cannot carry beside other timed tests carries a category the
  documented gate runs in a pass of its own (`quality-gates.md`): the native executable tests and
  the `Package` category.
- A test releases every thread, handle and process it starts, directly or through a server it
  launches, and joins every reader it owns before it ends.
- A test does not block a thread-pool thread on a release that another pool item must perform; it
  holds the blocking side on a dedicated thread, unless the product itself runs that side on the
  pool.
- A proof that a path leaves nothing behind counts only what that path creates and compares it
  exactly: the processes it started, their handles, their listeners. A whole-process count is never
  the measure: it moves with the runtime's own thread churn, which no product path controls. A
  released listener is proven by its endpoint refusing connections, never by rebinding the exact
  ephemeral port, which races with other listeners. A wait before the count is for an asynchronous
  release the path starts and names, and for nothing else.

## 6. Owned processes are hermetic

A direct-launch process test runs against an MCP server process its fixture owns, and that process
may read nothing the fixture did not put there. The AppHost product inherits the ambient environment
except the isolation groups, `NO_PROXY` and `ASPIRE_HOME`.
No test, and no developer running `dotnet test`, exports anything to make the direct-launch boundary
true; the fixture hands the boundary to the child process it starts. The same boundary covers an
OpenCode server process a later fixture starts. A test never connects to, starts,
stops or reuses the user's own OpenCode server process or shared OpenCode service; a fixture that
cannot own its server fails.

- **One environment policy.** A direct owned launch takes its environment from one place, the process
  owner in the test project's `Support/`. It launches the product in an owned empty run directory
  with an owned home, cache, configuration and temp root, and it clears inherited `OPENCODE_*`,
  `OTEL_*` and exporter, ASP.NET Core endpoint, Aspire, startup-hook and profiler variables before
  it adds the test's explicit inputs. It keeps the operating-system and runtime variables a launch
  needs and a loopback proxy bypass, and nothing of the user's secrets or global state. It drains
  standard error concurrently within a bound and records only redacted, selected lines.
  Explicit inputs override the scrubbed environment, but an explicit isolation-path variable is
  refused. The launch explicitly transfers an owned root: a direct child of the temporary directory
  whose name starts with `opencode-mcp-` and which contains the working directory. The owner refuses
  other roots before starting a process and deletes only that root. Home, config, cache and temp
  paths are assigned beneath it by the owner last. The direct owner
  diagnostic probes use the operating system's command interpreter; they never launch an MCP
  stand-in or require an additional installed runtime.
- **One stdout reader.** The MCP stream transport is the sole stdout reader while connected.
  `RecordingDuplexStream` observes transferred bytes and owns the stream it wraps; both synchronous
  and asynchronous disposal close it idempotently. Synchronous I/O is unsupported. Stdin EOF is
  sent by disposing the input wrapper while the client remains alive. Await exit and client
  completion before inspecting the final transcript or disposing the client. Only a launch with
  no connected client drains stdout directly through its wrapper, concurrently with the exit wait.
  HTTP endpoint discovery also starts that drain. The transcript counts raw server bytes and retains
  every server line, including blank lines, errors and notifications; an empty result list alone
  never proves empty stdout.
- **Bounded diagnostic retention is not a drain limit.** The owner drains stderr through EOF even
  after it has retained 200 selected lines. Each selected line is limited to 2,000 characters;
  oversized lines carry a truncation marker. Lifetime-category lines, selector refusals, and the
  first line of fail/critical entries are retained after sensitive token values and owned paths
  are redacted. Retaining the selector-refusal line is an approved deviation from the original
  category-only selection policy: invalid-CLI tests need its full diagnostic assertion.
  A sensitive separator at the end of a token also redacts the next whitespace-separated token.
  A valid numeric loopback listening announcement completes endpoint discovery even
  when diagnostic retention is full.
- **An OpenCode server process extends the boundary, never weakens it.** A fixture that starts one
  sets every variable and configuration root that could steer it at the developer's data inside
  the fixture's run root, refuses the launch while machine-wide configuration that no variable
  moves exists, keeps the run root where no repository or project configuration sits above it, and
  confirms that the server honoured the boundary before any test reaches it.
- **An owned process never outlives its fixture.** Teardown ends every process its test started and
  waits for each to be finished before it removes the run root: a process still running under a
  removed root recreates it and keeps running unowned. On Windows, `HasExited` can be true before a
  killed child has closed its files, so the public `Process.WaitForExitAsync(CancellationToken)`
  wait starts while the child runs, before any root deletion. The owner awaits it and keeps the
  `Process` instance alive until disposal, so its exit status remains readable. It uses no
  raw-handle wait. Teardown runs in
  `finally` or asynchronous disposal, after assertion failures too; it ends only owned children,
  finishes their readers and releases their files. Killing a process is a cleanup fallback, never
  an end-of-input success, and a cleanup assertion never runs after the ownership evidence it rests
  on is lost.
  HTTP process tests force termination in teardown only; endpoint refusal proves listener release,
  not graceful HTTP shutdown. EOF exit has a 45-second outer hang guard around the host's
  30-second shutdown default and SDK reader cleanup. Forced cleanup has its own 15-second guard.
- **The event decides a verdict, never the clock.** The verdict rests on an event: the call result,
  the exit after end of input, the refused connection after stop, the healthy resource. A test
  whose verdict rests on a product bound passing, or not passing, runs that bound on a test-owned
  `TimeProvider` that moves only when the test moves it, and moves it only after the event the
  verdict rests on. Every other upper bound in a test is a hang guard: it decides no verdict, and
  only a hang reaches it.
- **A wrapper's deadline stays above the bound it wraps.** When a test helper wraps an operation
  that has its own named bound, the wrapper's deadline stays above that bound, so the named failure
  is the one reported. An outer guard exceeds the owned operation guards it contains.
- **Fail fast, never skip.** A missing build output, a native path that does not exist or is a
  managed assembly, or a missing configured package feed is an instructive error, not a skipped
  test and never a fallback to another form of the product.

### AppHost ownership

`OwnedAppHost` accepts the actual testing builder and an `IFileSystem`. It owns one product run,
not a substitute MCP server. Its separate test module has no reference to the product-test
executable. The graph test inspects the unmodified entry-point model: project names are exactly
`["mcp"]`, with generated metadata and the declared HTTP wiring. Bootstrap tests disable the
dashboard and observe actual resource readiness before using the discovered authority. The hidden
`mcp-rebuilder` stays in the upstream graph and must never start.

`OwnedAppHost` alone owns and disposes the testing builder. Its product environment overrides only
the home, config, cache and temp isolation groups, `NO_PROXY`, and the supplied `ASPIRE_HOME`;
other ambient variables remain inherited, unlike the direct process owner's full scrub.

Start the resource watcher before application startup. Select the actual product resource's named
`executable.pid` property, ignore its not-yet-assigned zero value, retain each positive PID's
process handle, and begin its exit wait while it is alive. The watcher is owned and joined, not
fire-and-forget. Dispose MCP clients before disposing AppHost and its builder. Wait for product
exits and the watcher, close the retained handles, then delete only the owner's root. Startup
failure preserves the original error before any cleanup error; forced termination of a recorded
product is a failed cleanup, not successful AppHost disposal.
Give app disposal, builder disposal and observer joining independent bounds, snapshot recorded
processes and exit waits under the ownership lock, attempt cleanup of every recorded product
independently, and await exits under a fresh 30-second bound that earlier timeouts cannot cancel.
Before a fallback kill, check the retained handle's exit state and compare the current PID's start identity
with the one retained at observation; kill only that process, never its tree. Root deletion failures
join the ordered failure list instead of replacing earlier failures. If exit waits fail, retain and
name the owned root instead of deleting it. Later disposal does not repeat a cleanup failure already
reported with a startup failure.

Cleanup tests hold independent process handles and capture the owner's safe handles before
disposal. Assert `HasExited` before waiting, then assert the captured handles are closed, a fresh
connection is refused, the owned root is absent, and the sibling sentinel's contents are unchanged.
A completed no-op disposal must fail at an assertion, not an exit timeout. Test backstops release
their own evidence after those assertions, including during mutation runs. Real request failure,
resource-ready callback failure, explicit cancellation and repeated disposal each exercise cleanup.

AppHost phase hang guards are: entry-point builder resolution 360 seconds; DCP/application startup
180 seconds; resource health and ready callbacks 120 seconds; MCP connection 45 seconds; each
HTTP/MCP request 30 seconds; app disposal 60 seconds; builder disposal 30 seconds; observer join
10 seconds; fallback product exits a fresh 30 seconds; whole test 20 minutes. Each guard names the
event it awaits. HTTP clients have infinite timeouts, so request tokens own the request bounds;
the modern discovery probe uses the existing infinite-probe setting with a bounded connection.
The whole-test token links the phase guards without changing the parameterless test signatures.

DCP process exit is not asserted: the owner retains product processes only.
The cancellation test cancels before the AppHost starts; cancellation during a running startup is
not covered.
The tests launch the product's Release apphost directly through the testing builder's executable
annotation rather than `dotnet run`; the `tools/aspire.ps1` lifecycle check covers the `dotnet run`
path, and the maintainer accepts this exact-ownership trade-off.

## 7. Anti-patterns (never)

- Inline data dumps (§3), or the same literal appearing in two test methods.
- Real file I/O in a unit or contract test; raw `System.IO` anywhere in tests.
- `Skip`. This document sanctions no skip mechanism; a missing prerequisite is an instructive
  failure (§6). A platform-gated leg is not a skip: it asserts the arm the platform can reach,
  names the branch it took, and both branches assert.
- Retrying, widening a bound for, or quarantining a flaky test. A flaky test is fixed at its root
  cause; a test is deleted only with evidence that its subject is gone or that another test proves
  the same thing.
- A second filesystem fake, a resurrected retired helper, or infrastructure kept "just
  in case" — deleted code is recoverable from git.
- Testing library internals (the MCP C# SDK's protocol handling, Aspire, TUnit itself, the BCL)
  — test our rules at our boundaries.

## 8. Mutation proofs

A change that adds or fixes a critical rule carries one mutation proof per rule. Invert or remove
the rule in product code, show that a named test fails for the asserted reason, then restore it. A
proof counts only when:

1. the slice is committed, or the mutated file holds no other uncommitted edit; the restore is by a
   saved copy or `git checkout HEAD --`, and the diff is byte-identical afterwards;
2. the mutated tree builds, and the record says whether it builds under the full analyzer build or
   only with `RunAnalyzers=false`. A mutant the full build rejects for a semantic diagnostic (dead
   code, a tautology, an unused member, a nullable error) is recorded as unreachable through that
   rule ID, not as a kill. A mutant rejected only for its spelling is re-spelled in an analyzer-clean
   form;
3. every binary the test exercises was rebuilt, including the product build a process test
   launches, the native payload a native test runs and the packages a package test inspects;
4. it fails in every transport mode the rule applies to;
5. a test run that prints no total, or a total of 0, is not a result.

This product's critical rules are the transport-selector refusals, the loopback-only HTTP
endpoints, protocol-only standard output in stdio mode, the native-only package mapping with no
`any` fallback, and whole-payload digest equality across a native payload's declared files.

Mutation-tool scores are advisory. A tool that compiles mutants without the analyzer wall reports
kills that the real build would reject, so its results are replayed through the full build before
anyone relies on them.
