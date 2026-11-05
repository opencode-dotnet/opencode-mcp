# Coding Style — hand-written code

Date: 2026-11-05

Binding authoring style for every hand-written C# line in this repository — the MCP server, its
ServiceDefaults, the AppHost, and tests. The repository has no generated source; mechanical
formatting is governed by `.editorconfig`. This document governs what neither the formatter nor the
analyzers can see: how code is decomposed, composed, and shaped.

## 1. The central rule

**Private methods are for local mechanics and narrative flow. Code that carries
behavior, policy, an algorithm, or a concept that deserves a name is extracted into a
named collaborator class.**

Private methods are right for:

- **Narrative helpers** — keeping a public method readable as a short story.
- **Local invariants** — internal state handling no other class needs.
- **Small technical details** — repeated mechanics with no domain meaning.

Red flags that demand extraction:

1. Private methods calling each other in chains — a workflow engine hiding in a class.
2. A private method containing a business rule, a policy, or a branching algorithm.
3. Five or more parameters — the parameter list is a class asking to exist.
4. Mutating three or more fields — temporal coupling.
5. The urge to unit-test a private method independently — extract it and test the class.
6. Generic, meaningless names on privates: `Process`, `Handle`, `Do`, `Execute`, `Run`.

Extraction targets by what the code *is*: a rule → `*Policy` / `*Validator` / `*Selector`; an
algorithm → `*Classifier` / `*Calculator` / `*Normalizer`; a traversal → `*Walker` /
`*Projector`; an assembly job → `*Builder` / `*Composer` / `*Factory`. Name the concept, not the
mechanism. A class is a unit of *meaning* with a testable contract — not a namespace
for a pile of static steps.

## 2. Interfaces, sealing, and dependency injection

- **Interfaces exist at seams**: filesystem, process, network, console, clock,
  environment, host creation, any external system, and cross-module boundaries — the places a
  test substitutes (NSubstitute) or a composition swaps. A seam's interface lives in the owning
  slice's `Abstractions/` folder, next to what it abstracts (`Hosting/Abstractions/` for the host
  factory seam).
- **Everything else is a `sealed class`.** Pure logic a test can construct directly
  gets no interface ceremony — no `IThing` for a single implementation nobody
  substitutes. The analyzer wall already enforces sealed-by-default (MA0053).
- **One exception: stateless policy is a `static class`.** A policy that is a pure function over
  the data it is given — no I/O, no collaborator, no mutable state — is a static class with static
  methods, because CA1822 and S2325 make a member that touches no instance state static, and an
  instance the type would never use earns nothing. The transport selector, which turns the
  command-line arguments into launch options, is the case in point. A static policy is called
  directly and is never registered in the composition root. The moment a policy needs a
  collaborator, I/O or state, it becomes a sealed class that receives its collaborators through its
  constructor from the `ServiceCollection` the composition root builds.
- **An MCP tool type is not a policy.** The MCP C# SDK constructs a tool type through dependency
  injection for each invocation, and tools take collaborators, so a tool type is a sealed instance
  class with instance tool methods even when a method touches no instance state
  (`../architecture/server-and-transports.md` owns the reason). If an analyzer rule disagrees, the
  rule is settled under the suppression policy in `slop-review.md`, never by making the tool
  static.
- **Two homes are static because the language puts them there:** native interop declaration
  holders (the `LibraryImport`/`DllImport` entry points of one platform) and extension-method
  containers, such as the ServiceDefaults registration methods. They hold declarations, wiring and
  thin forwarding only; any policy or I/O around a native call lives in a sealed class behind a
  seam. A static class that performs I/O or takes a collaborator outside these two homes is a
  defect to fix, not a pattern to follow.
- **DI-first composition.** Every executable has exactly one composition root. The MCP server's is
  `McpApp`: `Configure` composes each host's container and applies caller overrides last;
  `CreateApplication` constructs the launcher directly from the host factory, carrying the optional
  override, and the error writer. No DI container exists outside the hosts: the launcher has two
  collaborators, and a second container would duplicate the hosts' registrations; ASP0000 flags
  building one in a web project. Commands, hosts
  and services receive dependencies through constructors — no service locators, no static mutable
  state, and no behavior on static classes beyond the stateless policies above. The static
  composition root is wiring only: it may construct and register concrete adapters but performs no
  I/O itself. The AppHost's composition (`AppHostComposition`) follows the same rule for the
  distributed application builder. The shape:

  ```csharp
  public static class McpApp
  {
      public static McpApplication CreateApplication(
          TextWriter errors, Action<IServiceCollection>? overrideServices = null)
      {
          ArgumentNullException.ThrowIfNull(errors);
          return new McpApplication(new McpHostFactory(overrideServices), errors);
      }

      public static void Configure(
          IHostApplicationBuilder builder, McpTransport mode, Action<IServiceCollection>? overrideServices = null)
      {
          ArgumentNullException.ThrowIfNull(builder);
          builder.Logging.ClearProviders();
          builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
          builder.AddServiceDefaults();
          var identity = new ProductIdentity();
          builder.Services.AddSingleton(identity);
          var mcp = builder.Services.AddMcpServer(options =>
          {
              options.ServerInfo = new Implementation { Name = identity.Name, Version = identity.Version };
              options.ProtocolVersion = "2026-07-28";
          }).WithTools<BootstrapTools>();
          if (mode == McpTransport.Stdio) { mcp.WithStdioServerTransport(); }
          else { mcp.WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless); }
          overrideServices?.Invoke(builder.Services);
      }
  }
  ```

- **Composition is full-battery, not skeletal.** The single root owns configuration, every
  external seam (the host factory, the error writer, process and filesystem collaborators where
  they are needed), Microsoft.Extensions.Logging providers, the MCP server with its typed tools and
  its transport, and application services. Logging uses MEL end to end: structured `ILogger<T>`
  consumption, a console provider that writes every level to standard error so standard output
  carries protocol only, and the OpenTelemetry provider the standard ServiceDefaults add. Clearing
  the providers precedes the defaults so it never removes that provider. Tests start from the same
  production registration path and replace seams through the override that runs last; they never
  reproduce the registration list in a parallel test-only root.
- **The command line is read in one place.** The transport selector consumes the transport pair
  and hands every other token to the generic host's own configuration unchanged; no tool or service
  reads raw arguments. The composition root registers one refusal handler: `McpApplication` catches
  the selector's refusal before any host exists, writes it to the injected error writer and maps it
  to exit code 2. No other code catches a refusal, and an unexpected startup error fails rather than
  being swallowed. The generic host's lifetime owns Ctrl+C and SIGTERM for the server run: the entry
  point passes `CancellationToken.None` with that reason beside it, and the host's own stop path ends
  the run.
- **Statics never perform outside-world operations.** The composition root may instantiate
  and register adapters; actual filesystem, console, time, environment, process, and network
  work occurs only through injected seams. The entry point hands the console's error writer to the
  composition root and does nothing else with the console. The TestableIO analyzer enforces the
  filesystem half mechanically.

## 3. Signature hygiene

- **No tuple returns** across class boundaries — a result worth returning is worth a
  named record. Tuples are tolerated only inside a method body.
- **No concrete collection parameters** (`List<T>`, `Dictionary<K,V>`) on non-private
  members — accept `IReadOnlyList<T>` / `IReadOnlyDictionary<K,V>` /
  `IEnumerable<T>` or a domain type. Collaborators that own mutable private state return a snapshot
  or a read-only view appropriate to that ownership boundary.
- **A parameter cluster traveling together is a record asking to exist** — the same
  three values on four signatures means the domain grew a concept the code has not
  named yet.
- **Guard public inputs** with BCL throw-helpers; assert internal invariants with
  `Debug.Assert` - the repository's defensive-programming default
  (`quality-gates.md`).
- Immutability by default: records with `required`/`init`; name actual collection ownership
  rather than treating `IReadOnly*` as a deep-immutability guarantee. A type that must not observe
  later caller mutation takes a defensive read-only snapshot, as the launch options do with the host
  arguments; other types choose stronger ownership only when a concrete consumer or concurrency
  boundary requires it.
- **Regular expressions:** a regex whose pattern matches in linear time and whose input is
  repository-pinned or test-local passes `Timeout.Infinite` (`matchTimeoutMilliseconds:`), or
  `Regex.InfiniteMatchTimeout` where the API takes a `TimeSpan`. A regex over untrusted input, or
  one whose pattern can backtrack superlinearly, keeps a finite timeout with its reason beside it.
  The match timeout is wall-clock: on a linear pattern over pinned input it guards nothing and fails
  on a stalled runner.

## 4. Layout

- **Vertical feature slices as folders**: a feature's types live together under its
  folder (`Hosting/`, `Tools/Bootstrap/`, …), never in horizontal type-kind buckets spanning the
  project.
- **Within a slice, conventional groups**: `Models/` (or the slice's domain-record
  folder) for data shapes, `Abstractions/` for its seams, `Internal/` for the slice's
  implementation where the slice also has a public surface. Similar things sit together; a
  reader predicts where a type lives before opening the tree.
- **A test project mirrors the layout of the project under test**: the folder path of a
  SUT predicts the folder path of its tests (`testing-style.md` owns the rule).
- File = type (MA0048) and folder = namespace (IDE0130) are mechanical in every project, with no
  flat-namespace exception: moving a file moves its namespace. This document adds the *placement*
  discipline analyzers cannot see.
- **Split before it hurts:** a class approaching a few hundred lines, or a folder
  flattening into dozens of siblings, is a slice asking to be verticalized. Growth is
  handled by extraction and sub-foldering at the moment of pressure, not by a rewrite
  after the pain.

## 5. Simplicity, calibrated

Take from minimalist ecosystems the product discipline — small surface, few concepts,
no speculative abstraction. Do not take their function-soup idiom into C#: this
repository writes classicist .NET — classes and interfaces, DI, detailed tests against
seams. Both failure modes are named so neither wins:

- **Private-method soup**: one giant class, dozens of private/static steps, `List`
  parameters and tuple returns threading state through a hidden workflow — untestable
  except end-to-end. A known LLM-generation failure mode; review rejects it on sight.
- **Enterprise cosplay**: interfaces, factories, and layers wrapped around code with
  one caller and one shape. Extraction needs a *reason* from §1's red flags; a seam
  needs a *substitution* someone actually performs.

A framework tutorial's shape is not permission to weaken these rules: a sample that puts the whole
host in `Program.cs` is adapted to the composition root above, not copied.

## 6. Enforcement

The analyzer wall already carries part of this document: MA0051 (method length), CA1501, CA1502
and CA1505 (inheritance depth, cyclomatic complexity, maintainability),
MA0048/IDE0130 (file/folder discipline), MA0053 (sealed), CA1062 (guards), the TestableIO analyzer
(I/O seam). No rule measures class length; review enforces what this document
states and the wall does not measure — and in agent-driven development, the executing
agent's plan carries these rules as constraints, so violations are defects, not style
preferences.
