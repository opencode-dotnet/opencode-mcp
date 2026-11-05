# Slop Review

Date: 2026-11-05

What slop means in this repository, how to recognize it, and the rubric that every change and
every review applies. It complements `coding-style.md` and `testing-style.md`: they say how to
write code and tests, and this file says how to recognize text that should not have been written.

## Definition

**Slop** is code, a test, configuration or prose whose cost to verify and maintain exceeds the
value it adds. It compiles, passes the gate and looks plausible, yet it has at least one of these
properties:

- **removable:** deleting or simplifying it loses no behaviour and no information;
- **unrequested:** no stated purpose of the change needs it;
- **out of place:** it ignores an existing API, seam, convention or canon rule;
- **unproven:** no test would fail if it were wrong, or its author cannot explain it.

Every line that reaches the default branch needs someone who can vouch for it.

Slop differs from technical debt. Debt is a deliberate, priced trade-off that announces itself
through friction. Slop is usually nobody's decision. It looks clean and green, and it grows with
volume and with parallel work that cannot see its own prior art. It is prevented at the gate and
swept continuously; it is not paid down later.

## Not slop here

These look like slop to a generic pass and are not:

- `ConfigureAwait(false)` in product code: the analyzer policy requires it.
- A one-implementation internal interface that tests substitute. It is a seam (`coding-style.md`
  §2), though R2 below still applies when it duplicates a BCL abstraction.
- A sealed instance MCP tool type whose method touches no instance state: the MCP C# SDK constructs
  tool types through dependency injection (`coding-style.md` §2).
- A suppression that follows the suppression policy below.

## Catalog

Each pattern has an ID that reviews and findings cite. Severity follows impact: **H** hides wrong
behaviour or makes a gate lie, **M** costs maintainability or consistency across slices, and **L**
is noise.

### Production code

| ID | Pattern | Sev |
|---|---|---|
| SLOP-01 | A parallel abstraction for a concern that an existing API or seam already covers | M |
| SLOP-02 | A near-duplicate helper or idiom in another slice | M |
| SLOP-03 | A speculative abstraction: an interface, factory or options type with no named reason | M |
| SLOP-04 | A one-call wrapper that adds no policy, or a half-applied abstraction | L–M |
| SLOP-05 | Misplaced defence: guards on internal invariants, missing guards at real boundaries | L / H |
| SLOP-06 | Error masking: catch and continue, catch and return a default, a comment-only catch | H |
| SLOP-07 | A silent fallback chain that turns a failure into a valid-looking result | H |
| SLOP-08 | A null-forgiving `!` with no stated guarantee | M |
| SLOP-09 | Comment slop: restating, history narration, plan residue, a stale rationale | L / M when stale |
| SLOP-10 | Dead code and leftovers from an abandoned approach | M |
| SLOP-11 | Scope creep: a drive-by rewrite, an unrequested option or feature | M |
| SLOP-12 | `CancellationToken.None` or `default` passed to get past a rule | M |
| SLOP-13 | Background work with no owner and no fault path | M–H |
| SLOP-14 | A policy literal repeated instead of one named constant | L–M |
| SLOP-15 | Private-method soup: stateful chains spread over many small members | M |
| SLOP-16 | Suppression sprawl: thresholds raised, one rule silenced through several channels | H |
| SLOP-17 | An unverified claim: a knob, flag, rule ID or number nobody checked | M–H |
| SLOP-18 | Generic naming and boolean parameters that hide intent | L |
| SLOP-19 | Reinventing what the BCL or a referenced library already provides | L–M |
| SLOP-20 | Async elision applied inconsistently | L |
| SLOP-22 | A process, handle or disposable with no named owner | H |
| SLOP-23 | Suppression rot: an exemption that no longer suppresses anything, or whose reason is false | M |

### Tests

| ID | Pattern | Sev |
|---|---|---|
| SLOP-T01 | An expected value taken from running our own implementation | H |
| SLOP-T02 | A test weakened or edited to go green, or production code special-cased for a test | H |
| SLOP-T03 | A hollow or boolean-collapsed assertion, or an untested branch | M–H |
| SLOP-T04 | Copy-pasted arrange instead of a builder | M |
| SLOP-T05 | Redundant tests and oversized test classes | M |
| SLOP-T06 | Asserting implementation detail instead of the contract | L–M |
| SLOP-T07 | A verdict decided by the clock | M–H |
| SLOP-T08 | A test for a case the public contract cannot produce | L |
| SLOP-T09 | Snapshot auto-accept | H |

### Prose and configuration

| ID | Pattern | Sev |
|---|---|---|
| SLOP-P01 | A copied canon rule that drifts from its owner | M |
| SLOP-P02 | Configuration appended instead of merged: duplicate globs, repeated reasons | L |
| SLOP-P03 | Filler in commits, pull requests and ADRs | L |
| SLOP-P04 | An XML summary that only restates the member name | L |
| SLOP-P05 | Mechanism narrated in a public `<summary>`, where it reads as contract | M |

IDs are stable: a retired pattern's ID is not reused.

## Rubric

Apply the rubric per file or per slice, after the gate is green. Do not flag what the gate already
catches. Each item is pass, fail or not applicable, and a fail names its reason.

### Production code

- **R1. It earns its place.** Deleting or simplifying the construct would lose behaviour or
  information. A wrapper exists because it adds policy, such as error mapping that callers rely on.
- **R2. One abstraction per concern.** Look in the BCL, the referenced libraries (the MCP C# SDK,
  the .NET hosting stack) and the existing seams first. A substituted seam that duplicates
  `TimeProvider`, for example, fails this item even though it passes R3.
- **R3. Every interface names its reason:** a seam that tests substitute, a public extension
  point, or DI composition.
- **R4. Guard at boundaries, trust nullable annotations inside.** Public entry points guard their
  arguments. Internal code does not re-check what the annotations prove.
- **R5. No masked errors.** A catch rethrows, filters, or converts the error into a typed outcome
  the caller sees. It never returns a default. A best-effort swallow follows the suppression policy
  below.
- **R6. No silent fallback.** A degraded path is visible to the caller or in the result.
- **R7. Every `!` states its guarantee** in one sentence, or it goes.
- **R8. Cancellation flows.** `CancellationToken.None` appears only with a stated reason, for
  example a shutdown that must finish after the caller cancelled, or an entry point whose host
  lifetime owns cancellation.
- **R9. Comments say why, in the present tense.** No restating, no plan numbers, no history, no
  documentation paths (`documentation.md`). A rationale stays true for the code beside it.
- **R10. One source per policy constant,** including values that the MCP protocol revision fixes.
- **R11. Scope.** Every hunk traces to the stated purpose of the change.
- **R12. Suppressions follow the policy below.**
- **R13. Background work has an owner and a fault path.** A task that is started and not awaited
  is either stored and observed by its owner, or its comment states what owns it and where its
  faults go, and the code backs that claim.
- **R15. Every process, handle and disposable has a named owner.** No disposable is captured by a
  discarded task.

### Tests

- **T1. The oracle comes from outside evidence:** the MCP specification, the documented behaviour
  of the library or platform involved, or an observed frame kept as test data. It is never pasted
  from our own output.
- **T2. The test fails if the change is reverted.** A fix shows red, then green. Characterization
  tests that pin behaviour a rewrite keeps are proven by a named mutant instead of T2's
  red-then-green.
- **T3. Assertions state intent and print a useful failure.** A collection assertion reports the
  elements it saw; `Assert.That(x.Any(…)).IsTrue()` reports only `false`.
- **T4. Arrange goes through builders.** The second copy of a literal grows a builder verb.
- **T5. Existing assertions are not weakened** in a fix without a recorded decision and the outside
  evidence that the old expectation was wrong.
- **T6. Assert the contract,** not implementation detail.
- **T7. The event decides, never the clock** (`testing-style.md` §6).
- **T8. The case is reachable** through the public contract or the wire format.
- **T9. String-shaping code has boundary cases:** separators at both ends, and escape characters
  next to quotes.

### Prose and configuration

- **P1.** One canonical home, with relays elsewhere. No duplicated globs or conditions, and one
  shared reason instead of copies.
- **P2.** A `<summary>` says what the member is for, in more than its name, in about 40 words or
  fewer. Mechanism detail goes in `<remarks>`.

## Suppression policy

A suppression silences a gate, so it is rare, visible and counted:

- **It names the rule and its reason,** and the reason is true for the code beside it.
- **It has the maintainer's approval.** A new suppression is called out in the pull request body.
- **One channel per rule per file.** The channels are:
  - `[SlopwatchSuppress]` and `slopwatch-ignore`;
  - `#pragma warning disable`;
  - `[SuppressMessage]`;
  - `.editorconfig` severities, `.globalconfig` and `<NoWarn>`.
- **It is counted.** The pull request body lists every suppression the change adds, moves or
  removes, with its rule, file and channel; a suppression that no pull request body lists has no
  approval.
- **A best-effort swallow** is a failure the caller cannot act on, such as cleanup after the
  outcome is already decided. It uses a reason-bearing `[SlopwatchSuppress("SW003", "...")]` that
  says where the failure is observed. A discarded `Try…` result is not a substitute.
- **A suppression that no longer suppresses anything is removed,** and so is one whose reason is
  no longer true.

The preferred order is the code fix, then a narrow per-rule arbitration that names the winning rule
(`quality-gates.md`, Analyzer policy), then a suppression.

## How review uses this

- A reviewer judges independently before reading the pull request description, cites catalog IDs
  and rubric items, and flags only what is above a confidence bar.
- A reviewer from a different model family than the author's reviews every change that a model
  drafted.
- Every finding the change does not fix gets a record (`workflow.md`, Findings).
