# Contributing to OpenCode MCP

Open an [issue](https://github.com/opencode-dotnet/opencode-mcp/issues) for a bug, question or idea.
Search existing issues and the [roadmap](../docs/ROADMAP.md) first. Agree on the approach before
starting work beyond a small fix; describe the use case before proposing an API.

## Develop from source

You need Git and the .NET SDK selected by [`global.json`](../global.json), on Windows, Linux or macOS.
Follow the [README's source-development route](../README.md#developing), including its owned
`ASPIRE_HOME` setup before the first build. The AppHost SDK acquires its paired Aspire CLI bundle
during the normal build; no global Aspire installation is needed. Bootstrap tests require no
OpenCode server, model provider, Bun, submodule or container.

Start at [`AGENTS.md`](../AGENTS.md) for the canon relevant to your change. Add tests for behavior
and keep affected documentation current in the same change. The
[testing style](../docs/engineering/testing-style.md) defines test placement, real-process ownership
and cleanup; the [coding style](../docs/engineering/coding-style.md) defines implementation shape.
Native publishing has separate prerequisites and is not part of managed source verification.

## Submit a pull request

1. Work on a feature branch and keep the change focused on its issue.
2. Run the [local check](../docs/engineering/quality-gates.md#completion-gate) before opening the
   pull request, unless the change is limited to Markdown and `LICENSE`. The full gate runs in CI;
   run it locally only when requested or reproducing a CI failure.
3. Apply the [slop-review rubric](../docs/engineering/slop-review.md). Do not weaken tests, retry
   failures or hide warnings to get green checks. List any suppression changes explicitly.
4. Use [Conventional Commits](https://www.conventionalcommits.org/) and fill in the
   [pull request template](PULL_REQUEST_TEMPLATE.md). Neither commits nor pull request bodies carry
   AI-attribution trailers, co-author lines or session links.
5. Address review feedback and report the checks you actually ran, with SDK, OS and test counts.
   Managed, AppHost, native and package evidence are separate claims.

The [workflow policy](../docs/engineering/workflow.md#commits-and-ci) owns approval and delivery:
maintainer approval and a green pull request permit queue entry, a green queue run permits the
merge, and a green `master` run completes delivery (the queue result stands for Markdown/`LICENSE`
changes whose push run is ignored). The maintainer also checks Dependency Review before queue
entry. The roadmap records whether the workflow, queue and branch protection are active; workflow
code alone is not hosted evidence.

## Report a bug

Use the [bug report form](https://github.com/opencode-dotnet/opencode-mcp/issues/new?template=bug_report.yml).
Include a minimal reproduction, expected and actual behavior, source commit or product version,
launch route, transport, MCP client and protocol versions, and environment details. Redact secrets
and private content from configuration, errors and logs; never attach a profile, Aspire dashboard
store, token or certificate. Reporters do not need to run the contributor test suite.

## License

By submitting a contribution, you license it under the project's [MIT License](../LICENSE).
There is no separate CLA.
