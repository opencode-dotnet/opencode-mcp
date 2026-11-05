# OpenCode MCP

> **Unofficial.** This project is not affiliated with or endorsed by the
> [opencode](https://opencode.ai) team.

`opencode-mcp` is an MCP server for [OpenCode](https://github.com/anomalyco/opencode). An MCP
client, such as a coding assistant or an editor, connects to it over the Model Context Protocol
and uses its MCP tools. It is a thin adapter over the OpenCode .NET SDK, built to ship as a native
executable that needs no installed .NET runtime.

## Status

**Pre-release bootstrap. Nothing is installable yet**: no package, binary, or installer is
published, and there is no launch command to give an MCP client.

The bootstrap delivers an MCP server that is designed to:

- run over standard input and output by default, or as a loopback-only Streamable HTTP endpoint on
  request;
- speak MCP revision `2026-07-28`;
- offer one MCP tool, `bootstrap_ping`, a connectivity probe that returns `pong` and checks only
  the connection between the MCP client and this server, not OpenCode or a model provider;
- ship as a NuGet MCP tool package and as native executables for Windows, Linux, and macOS on x64
  and Arm64.

Tools that operate on OpenCode follow the bootstrap. [`docs/ROADMAP.md`](docs/ROADMAP.md) has the
current state and what comes next.

## Developing

Building the repository needs the .NET SDK version that [`global.json`](global.json) selects.

Before a pull request opens, run the local check that
[`docs/engineering/quality-gates.md`](docs/engineering/quality-gates.md) describes; it has the
exact commands. A Native AOT publish on Windows has extra machine prerequisites, listed in
[`docs/architecture/hosting-and-packaging.md`](docs/architecture/hosting-and-packaging.md).

Contributors and coding agents start at [`AGENTS.md`](AGENTS.md), which routes each kind of work to
its canon; [`CONTEXT.md`](CONTEXT.md) defines the terms this repository uses.

## License

Licensed under MIT, see [LICENSE](LICENSE) for the full text. Content derived from the OpenCode .NET
SDK repository carries its own notice in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
