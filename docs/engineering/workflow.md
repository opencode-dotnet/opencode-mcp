# Repository Workflow

Date: 2026-11-14

Canonical working agreements for research, implementation, temporary work, verification, and Git
history.

## Collaboration

- Be direct, practical, and clear. Challenge decisions with mechanisms and sources rather than
  convention; do not agree your way into a weaker architecture.
- A question asks for an answer, not an unrequested mutation. When a request is ambiguous, present
  the options, recommend one, and ask.
- Read the canon and the code a change touches before writing. When the request leaves the structure
  open, take the shape the surrounding code already uses and name it in your summary. Prefer the
  smallest correct change over a broad refactor.
- When implementation contradicts current canon, stop the affected work and follow
  `deviation-protocol.md`; never silently code around the contradiction.

## Repository artifacts and temporary work

All repository-authored artifacts are written in English.

Everything temporary - prototypes, scratch scripts, probes, and working notes - lives under the
fully gitignored root `.scratchpad/` directory. Nothing permanent references it. Keep its minimal
`Directory.Build.props` stub as an empty project that disables central package management, so
scratch projects do not inherit the repository's strict build infrastructure. Validated outcomes
move into code or canonical documentation.

## Verification

Verification requirements live in `quality-gates.md`, including the local check a change needs before
its pull request and the full gate CI runs on it. Run the local check, confirm the CI result before
claiming completion, and state honestly what was not run.

## Findings

A finding that the change at hand does not fix gets a record before the work moves on. A product,
test, or documentation finding becomes an issue in this repository. A finding about process alone
is recorded outside this repository. A declined finding is recorded too, with its reason, in its
issue or as an issue closed as not planned. Scratch notes, session messages, and review reports are
evidence, not records.

## Dependency refresh

Package versions live in `Directory.Packages.props`, the .NET SDK in `global.json`, the Aspire
AppHost SDK in the AppHost project's SDK reference, and repository tooling in `dotnet-tools.json`.
The Aspire CLI pin in `dotnet-tools.json` and the AppHost SDK version in `src/OpenCode.Mcp.AppHost/OpenCode.Mcp.AppHost.csproj` move together in the product-dependencies commit.
A refresh takes the latest stable versions; a prerelease arrives only with the work that needs it.
It lands as one pull request with one commit per class, in this order, so a class can be reverted
alone, and the pull request passes the full gate:

1. **.NET SDK band and servicing** - the newest servicing patch of the selected feature band in
   `global.json`, with `Microsoft.CodeAnalysis.NetAnalyzers` and `Microsoft.SourceLink.GitHub`,
   which follow that patch. A move to another feature band is an explicit change under
   `../architecture/hosting-and-packaging.md`, not a refresh. A Native AOT payload embeds the
   runtime patch of the SDK that built it, so the CHANGELOG names the security fixes a raised patch
   carries into the shipped product.
2. **Product dependencies** - the MCP C# SDK; the Aspire AppHost SDK, `Aspire.Hosting.Testing` and
   the Aspire CLI, which move together; and the OpenTelemetry, resilience, and service-discovery
   packages of the standard ServiceDefaults, at the versions of the Aspire release's own
   ServiceDefaults template. The CHANGELOG names every raised product dependency and the security
   fixes it carries. A pin bump does not move the MCP protocol revision; that is a change of
   `../architecture/server-and-transports.md`.
3. **Analyzers** - the analyzer packages and Slopwatch. Every new diagnostic or Slopwatch finding
   is decided per rule under the analyzer policy in `quality-gates.md`, never by a blanket
   suppression. The NetAnalyzers diagnostics that class 1 brings are decided the
   same way.
4. **Test stack** - last, so a test-host regression is the last commit: TUnit with the
   Microsoft.Testing.Platform hang-dump extension on the platform line TUnit brings, Testably, and
   NSubstitute with its analyzer.

A NuGetAudit advisory fails restore; its remedy is a prompt pin bump through this procedure, never a
suppression or an audit override (`quality-gates.md`).

## Publication procedure

Settings and public identities require the maintainer's approval before the release lane is enabled.
Every feed publication and GitHub Release requires his own word with the exact target, candidate
commit, Native run ID, version, filenames and digests. Approval of a candidate or dry run authorizes
no publication.

Promote GitHub Packages first, make all seven packages public with the maintainer's explicit
irreversible-visibility approval, and verify that channel. Ask separately for NuGet.org publication
with the successful GitHub Packages verification run ID. Ask separately for the GitHub Release with
its tag, asset digests, release-notes text and successful NuGet verification run ID.

The lane dispatches the workflow after the word; the maintainer approves the waiting `release` job
in GitHub. The lane never approves deployments, publishes releases or changes package visibility
with its own credentials.

Hold workflow-tree changes from candidate selection until release publication. Partial publication
blocks promotion. Record push responses and the verified present/missing sets; ask for exactly the
missing-file recovery or abandonment. Never retry a push automatically or reuse rebuilt bytes under
a published version. Expired candidate artifacts require abandonment and a new version. The `files`
input recovers feed pushes only; a partial draft release is left for the maintainer's decision, not
automatically resumed or cleaned up.

## Documentation sessions

Research and decision sessions end with a documentation pass. Current canon or ADR updates when a
decision changes, and operational state in the roadmap, land here as one coherent commit when
the maintainer asks for it. The chronological research log (question -> finding -> decision) and
topic evidence are kept outside this repository, and canon cites them by date and title
(`documentation.md`).

Documentation maintenance follows `documentation.md`, especially its lossless-relocation and
single-owner rules.

## Commits and CI

Whether the CI workflow, the merge queue, and the protection of `master` are in place yet is
operational state in `../ROADMAP.md`; the rules below are the contract they implement.

- Commit only when the maintainer asks for it or inside the agreed development loop where
  committing is part of the flow. A request to commit is that approval: commit without asking again.
- The development loop: commits and pushes on feature branches need no per-commit approval. A
  merge to `master` needs the maintainer's approval, and so does a package-candidate commit
  (`../architecture/hosting-and-packaging.md`); approving a candidate commit authorizes no
  publication.
- Use Conventional Commits: `feat`, `fix`, `perf`, `docs`, `test`, `refactor`, `build`, `ci`, or
  `chore`. Commits and pull request descriptions carry no AI-attribution trailers or footers
  (session links, co-author lines, "generated with" notes), even when a tool adds them by default.
- `perf` is distinct from `refactor` because performance work requires before/after measurement
  evidence.
- A pull request opens once its change is reviewed and, where `quality-gates.md` asks for the
  local check, has passed it, so the CI the pull request triggers runs on finished work.
- Everything lands on `master` through the merge queue, release changes included. `master`
  requires the queue and the `CI result` check, for administrators too.
- A pull request joins the queue on the maintainer's approval, once the `CI result` check of its
  own head is green. The queue tests it again on top of current `master` and the entries ahead of
  it, and merges only on that green: a green result from an older base is not enough. After the
  merge, the work is done when the CI the merge commit triggers on `master` is green; a merge of
  only Markdown or `LICENSE` triggers none, and its queue result stands.
- CI runs on every pull request and queue entry. A change that touches only Markdown or `LICENSE`
  passes without the build and test jobs. Never put `[skip ci]` in a pull request's commits: the
  required check would not report, and the pull request could not join the queue.
