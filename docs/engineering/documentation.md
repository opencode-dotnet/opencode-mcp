# Documentation

Date: 2026-11-06

Canonical rules for documentation ownership, authority, lifecycle, references, and lossless
maintenance.

## Document roles

| Role | Homes | Meaning |
|---|---|---|
| Agent entry point | `AGENTS.md` | Universal operating rules and task-to-canon routing only |
| Current architecture | `docs/architecture/` | Normative current technical decisions |
| Current engineering practice | `docs/engineering/` | Normative current authoring, quality, workflow, and documentation rules |
| Decision rationale | `docs/adr/` | Accepted decision context, trade-offs, consequences, and reversal triggers |
| Domain vocabulary | `GLOSSARY.md` | Current terms and explicitly avoided synonyms |
| Operational state | `docs/ROADMAP.md` | What is done, next, open, and known to be incomplete; shrinks as work lands |
| Consumer documentation | `README.md`, `CHANGELOG.md` | How to use the MCP server, complete on its own (Consumer documentation, below) |

Plans, dated research, and session operations live outside this repository. Verification evidence
belongs in pull requests, not in documents. Canonical documentation is complete without task
histories and evidence narratives.

Dated research is never current policy without following its links to the current canonical sources.

Repository files and current canonical documents beat memory, dated research, transient plans, and
handoffs. If two current canonical sources appear to disagree, do not pick a winner silently; use
the deviation protocol.

## One canonical home

Every current fact has one canonical owner. Other appearances are short relays that link to the
owner instead of restating the full rule. Change-prone values are read from their mechanical source
instead of copied into prose.

Documentation refactors are lossless relocations, not summarization exercises. Preserve useful,
unique information in an appropriate canonical, rationale, evidence, or operational home. Delete a
passage only after proving it is an exact duplicate, obsolete without historical value, or retained
in a more authoritative source. Git history is not a substitute for accessible, useful canon.

Current-state documents contain durable rules and how-to, not task status, verification narratives
or amendment history. Keep status only in `docs/ROADMAP.md`, as short items. ADRs record decision
rationale, not work-queue state.

## Dates and lifecycle

Every hand-written document under `docs/` carries a `Date:` line.

- In current-state and operational documents, `Date:` is the latest substantive update.
- In ADRs, `Date:` is the date of the decision represented by the current record; a material
  decision revision moves it, while an editorial link correction does not.
- Where a `Date:` records a historical snapshot rather than a current state, an editorial
  correction does not rewrite it.

A sentence that must change when a task completes belongs in `docs/ROADMAP.md` or a temporary
handoff, not in evergreen architecture or engineering canon. Keep affected documentation current in
the same change as code; documentation is not follow-up work.

## References

References point one way: documentation may cite code, but code artifacts never cite documentation.
Comments in source, project files, `.editorconfig`, workflows, and scripts explain the status quo
locally; they do not point to movable prose or narrate decision history.

Canonical documents — architecture, engineering, ADRs, and `GLOSSARY.md` — never point at the paths
or numbering of material kept outside this repository (research questions, sessions, document
numbers) or at `docs/ROADMAP.md` content, which moves as work lands. Dated evidence is cited inline
by date and title, in the form: internal research, YYYY-MM-DD, "Title". Naming the ROADMAP as the
home of operational state, as the table above and `AGENTS.md` do, is routing, not a reference.

Audience decides placement. Knowledge for consumers of the MCP server belongs in the consumer
documentation; knowledge shared by maintainers and agents belongs in architecture, engineering,
ADR, domain, or operational documents; guidance needed only by coding agents belongs in
`AGENTS.md` or outside this repository.

ADRs are created lazily under the criteria in `docs/adr/README.md`. Session handoffs live outside
this repository; they are consumed against live Git and GitHub, then deleted when the follow-up
ships or a newer handoff supersedes them.

## Consumer documentation

`README.md` and `CHANGELOG.md` speak to people who run the MCP server and connect MCP clients to it.

- A change that alters what a consumer sees updates the README section that describes it, in the
  same pull request.
- From the first package on, `CHANGELOG.md` gets an entry under `[Unreleased]` for every
  user-facing change, in the same pull request. A released section is the delta from the previous
  published package: it states the status quo a consumer upgrading from that package meets, never
  the order of intermediate commits or development builds.
- Consumer documentation names OpenCode, the OpenCode .NET SDK, or the MCP C# SDK where their
  behaviour matters, but never sends a reader to their code, issues or documentation to learn how
  this MCP server behaves; what a consumer needs is stated here.
