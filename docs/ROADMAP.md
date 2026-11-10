# Roadmap

Date: 2026-11-10

Operational state: what exists today, what is queued next, what is still open, and what is known to
be incomplete. This file is a summary and shrinks as work lands. `../AGENTS.md` routes to the
current architecture and engineering canon; decision records live in `adr/`. Work items are tracked
as GitHub Issues in this repository; there is no project board. No date is promised for any item.

## Status

**Pre-release bootstrap. Packages can be acquired from the complete local feed; no package or binary is published.**

The managed server, transports, connectivity probe and one-resource Aspire AppHost are implemented.
Native AOT payloads for the six RIDs build and pass their checks in the hosted native workflow,
including the Linux clean-container proof. Verification evidence belongs in pull requests.

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
   (`architecture/hosting-and-packaging.md`); implemented.
6. **CI** - the required CI workflow with its `CI result` check, dependency review, and the merge
   queue on a protected `master` (`engineering/workflow.md`); implemented and active.
7. **Native AOT** - one verified native payload per RID from the manifest-driven native workflow,
   with the Linux builder image, the clean-container runtime-independence proof and the measured
   glibc floor (`architecture/hosting-and-packaging.md`); implemented and green on the six hosted
   runners.
8. **Packages** - the NuGet MCP tool package set built from those payloads, a complete local feed,
   and the first `CHANGELOG.md` entry; implemented, built and inspected in the hosted native
   workflow.
9. **Acquisition** - cold DNX and owned tool-path acquisition and packaged startup are verified on the
   six hosted RIDs. A package candidate verified from one approved commit is still required.

## Later

- **Installers** - PowerShell and shell installers, WinGet, Homebrew, deb and rpm packages, a
  Windows MSI, and a macOS pkg. They need their own approved plan, including the MSI tool and its
  terms, before authoring starts.
- **Release** - publication, signing and notarization, registry identity, and package-manager
  submissions, each separately authorized.
- **Product tools** - MCP tools that operate on OpenCode through the published OpenCode .NET SDK,
  after the bootstrap.

## Known Gaps

- **No installer support or final package candidate is verified.** Native receipts and
  package-acquisition results remain development evidence until a package candidate is verified from
  one approved commit.
- **AppHost cancellation coverage is incomplete.** Cancellation during running startup is not covered.
