# OpenCode MCP

> **Unofficial.** This project is not affiliated with or endorsed by the
> [opencode](https://opencode.ai) team.

`opencode-mcp` is an MCP server for [OpenCode](https://github.com/anomalyco/opencode). An MCP
client, such as a coding assistant or an editor, connects to it over the Model Context Protocol
and uses its MCP tools. It is a thin adapter over the OpenCode .NET SDK, built to ship as a native
executable that needs no installed .NET runtime.

## Status

**Pre-release bootstrap. Nothing is installable yet**: no package, binary, or installer is
published. A source build can run as a managed executable using the .NET SDK selected by
[`global.json`](global.json); this is not a native or packaged launch.

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

## Run from source

From the repository root, build Release:

```powershell
dotnet build OpenCode.Mcp.slnx --configuration Release
```

### Standard input and output

Launch the built assembly directly, not through `dotnet run`, so build output cannot enter the
protocol stream:

```powershell
dotnet ./src/OpenCode.Mcp/bin/Release/net10.0/OpenCode.Mcp.dll --transport stdio
```

Configure a local MCP client's command as `dotnet` and its arguments as the absolute path to that
`OpenCode.Mcp.dll`, followed by `--transport` and `stdio`. Standard input and output belong to the
client; diagnostic logs go to standard error. Omitting `--transport` also selects stdio.

The client must support MCP revision `2026-07-28`: discover with `server/discover`, list tools, then
call `bootstrap_ping` with no arguments. Its result is exactly `pong opencode-mcp <version>`, where
`<version>` is the product assembly's informational version without source metadata. This verifies
connectivity only; it does not contact OpenCode or a model provider.

### Local Streamable HTTP

```powershell
dotnet ./src/OpenCode.Mcp/bin/Release/net10.0/OpenCode.Mcp.dll --transport http --urls http://127.0.0.1:0 --environment Development
```

Port `0` requests an available port. Read the actual address from the `Now listening on:` lifetime
log on standard error and append `/mcp` for the client's Streamable HTTP endpoint. The endpoint is
stateless and uses the same protocol and tool as stdio. `/health` and `/alive` are available only in
Development; without that environment they return 404. Ctrl+C stops the host.

HTTP is loopback-only, without authentication or a CORS policy. Host and Origin checks apply to
every request; a missing Origin is allowed. Do not expose the endpoint through a public proxy or
tunnel. See [local HTTP exposure](docs/architecture/server-and-transports.md#local-http-exposure)
for accepted listening addresses and headers, defaults, and refusal responses.

An invalid transport selector writes a diagnostic naming `stdio` and `http` to standard error and
exits with code 2 before creating a host. Values are case-sensitive; use the separate-token
`--transport http` spelling, not `--transport=http`. Other startup failures are not selector refusals.

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
