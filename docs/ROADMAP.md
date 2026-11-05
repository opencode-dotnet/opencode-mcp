# Roadmap

Date: 2026-11-06

Operational state: what exists today, what is queued next, what is still open, and what is known to
be incomplete. This file is a summary and shrinks as work lands. `../AGENTS.md` routes to the
current architecture and engineering canon; decision records live in `adr/`. Work items are tracked
as GitHub Issues in this repository; there is no project board. No date is promised for any item.

## Status

**Pre-release bootstrap. Nothing is installable yet, and no package or binary is published.**

The engineering backbone, transport selector, shared production composition, and `bootstrap_ping`
are implemented. The product is a managed executable with a loopback-only Streamable HTTP endpoint,
product-owned Origin validation, Host filtering, and refusal of invalid selectors before host
creation. Real socket tests cover modern discovery/list/call, Development-only health, and refused
connections after shutdown; bounded recorder tests cover JSON and request-scoped SSE observations.
Source launch instructions are in the README. Owned managed-process tests cover default and
explicit stdio, protocol-only stdout, stderr lifetime logs, selector refusals, exit zero after EOF,
and cleanup after failures. HTTP process tests cover modern bootstrap, Development health,
Host/Origin controls and listener release after forced teardown. These are local Windows proofs,
not native or package evidence. The next bootstrap increment is the AppHost.

Until CI and the merge queue exist, feature branches merge with the maintainer's approval.

## Bootstrap, in order

1. **Engineering backbone** - implemented.
2. **Production host and tool** - the production composition with the standard ServiceDefaults, the
   server identity, and the `bootstrap_ping` connectivity probe
   (`architecture/server-and-transports.md`); implemented.
3. **HTTP proof** - the real executable, the loopback-only Streamable HTTP endpoint, and the
   refusal of an invalid mode before any host starts; implemented and locally tested.
4. **Stdio proof** - the real process over standard input and output, protocol-only standard output,
   and owned cleanup; implemented and locally tested on Windows.
5. **AppHost** - the one-resource Aspire AppHost and its closed-box lifecycle tests
   (`architecture/hosting-and-packaging.md`).
6. **CI** - the required CI workflow with its `CI result` check, dependency review, contribution
   guides, and then the merge queue and branch protection on `master`
   (`engineering/workflow.md`).
7. **Native AOT** - one verified native payload for each of the six RIDs, executed in both transport
   modes, with its runtime-independence proof.
8. **Packages** - the NuGet MCP tool package set built from those payloads, a complete local feed,
   and the first `CHANGELOG.md` entry.
9. **Acquisition** - isolated acquisition and packaged startup on each RID, then a package
   candidate verified from one approved commit.

## Later

- **Installers** - PowerShell and shell installers, WinGet, Homebrew, deb and rpm packages, a
  Windows MSI, and a macOS pkg. They need their own approved plan, including the MSI tool and its
  terms, before authoring starts.
- **Release** - publication, signing and notarization, registry identity, and package-manager
  submissions, each separately authorized.
- **Product tools** - MCP tools that operate on OpenCode through the published OpenCode .NET SDK,
  after the bootstrap.

## Known Gaps

- **No CI yet.** Until the CI workflow exists, the local check in
  `engineering/quality-gates.md` is the only gate, and no change can be called complete by a green
  `CI result`.
- **No native or package evidence yet.** No claim of native support, package acquisition, or
  installer support exists for any platform.
