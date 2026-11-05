# Agent Instructions

Entry point for coding agents working in this repository. Keep this file focused on rules that
apply before task-specific context is known; detailed canon is loaded through the routing table
below. Current status, open questions, and known gaps live in `docs/ROADMAP.md`; the live work
queue is this repository's GitHub Issues.

## Purpose

Build `opencode-mcp`, an MCP server for OpenCode that ships as a Native AOT executable and a NuGet
MCP tool package. It is a thin adapter: its OpenCode-backed MCP tools consume the published
OpenCode .NET SDK package, never that SDK's source or internals.

## Universal Rules

- Repository-authored artifacts are written in English. Temporary work lives only under
  `.scratchpad/`; validated outcomes move into code or canonical documentation.
- Be direct and practical. Challenge decisions with mechanisms and sources rather than convention.
- A question asks for an answer, not an unrequested mutation. When intent is ambiguous, present the
  options, recommend one, and ask.
- Read the canon and the code a change touches before writing. When the request leaves the structure
  open, take the shape the surrounding code already uses and name it in your summary. Prefer the
  smallest correct change.
- If implementation contradicts current canon, stop the affected work and follow
  `docs/engineering/deviation-protocol.md`; never silently code around it or reopen a decision for
  convenience.
- Documentation refactors preserve useful, unique information. Relocate it to the right canonical,
  rationale, evidence, or operational home; do not discard it merely to shorten a file.
- Keep affected documentation current in the same change as code. Every current fact has one
  canonical owner; other mentions are brief relays.
- Write nothing a reviewer could not vouch for: no unrequested code, no parallel abstraction, no
  masked error, no unproven test (`docs/engineering/slop-review.md`).
- Before reporting completion, run the local check and confirm the CI result that
  `docs/engineering/quality-gates.md` requires. "Builds" and "works" are different claims.
- Commit only when the maintainer asks for it or inside the agreed development loop. A request to
  commit is that approval: commit without asking again. Commits and pull request bodies carry no
  AI-attribution trailers or session links, whatever a tool default says
  (`docs/engineering/workflow.md`).

Full collaboration, scratchpad, findings, commit, and CI agreements live in
`docs/engineering/workflow.md`; a finding the change at hand does not fix gets a record there
before the work moves on. Documentation ownership and lifecycle rules live in
`docs/engineering/documentation.md`.

## Task Routing

Read the canon relevant to the work before proposing or changing design. ADRs explain why a
decision exists.

| Task | Read first |
|---|---|
| Transport modes, command-line selection, host composition, MCP protocol, local HTTP exposure, or MCP tools | `docs/architecture/server-and-transports.md` plus relevant ADRs |
| Projects, packages, dependencies, versioning, Aspire AppHost and ServiceDefaults, Native AOT (including the Windows prerequisites), NuGet packaging, installers, release boundaries, or licensing | `docs/architecture/hosting-and-packaging.md` plus relevant ADRs; a dependency refresh also follows `docs/engineering/workflow.md` |
| Hand-written code structure, signatures, DI composition, or layout | `docs/engineering/coding-style.md` |
| Test design or test code | `docs/engineering/testing-style.md` |
| Analyzer, build, formatting, or completion gates | `docs/engineering/quality-gates.md` |
| Reviewing a change, or recognizing slop and suppressions | `docs/engineering/slop-review.md` |
| Documentation, ADRs, or repository workflow | `docs/engineering/documentation.md`, `docs/engineering/workflow.md`, and `docs/adr/README.md` as applicable |
| README or CHANGELOG | `docs/engineering/documentation.md` (Consumer documentation) |
| Domain terminology | `CONTEXT.md` |
| Current status, queue, open questions, or known gaps | `docs/ROADMAP.md` and the GitHub Issues; recording a finding follows `docs/engineering/workflow.md` |

## Authority

- `docs/architecture/` and `docs/engineering/` own current normative project rules.
- `docs/adr/` records accepted decision context, trade-offs, consequences, and reversal triggers.
- `docs/ROADMAP.md` is operational and expected to change and shrink as work lands.
- `CONTEXT.md` owns current domain vocabulary.
- Dated research, plans, and session handoffs live outside this repository. That material is
  non-normative history and reference; contradicting it does not override current canon, and the
  canon here is complete without it.

Repository files and current canon beat memory, transient plans, dated research, and handoffs. If
two current canonical sources appear to disagree, do not choose one silently; use the deviation
protocol.
