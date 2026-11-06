# OpenCode MCP

> **Unofficial.** This project is not affiliated with or endorsed by the
> [opencode](https://opencode.ai) team.

`opencode-mcp` is an MCP server for [OpenCode](https://github.com/anomalyco/opencode). An MCP
client, such as a coding assistant or an editor, connects to it over the Model Context Protocol
and uses its MCP tools. It is a thin adapter over the OpenCode .NET SDK, built to ship as a native
executable that needs no installed .NET runtime.

The server's contract is to:

- run over standard input and output by default, or as a loopback-only Streamable HTTP endpoint on
  request;
- speak MCP revision `2026-07-28`;
- offer one MCP tool, `bootstrap_ping`, a connectivity probe that returns `pong opencode-mcp <version>` and checks only
  the connection between the MCP client and this server, not OpenCode or a model provider;
- ship as a NuGet MCP tool package and as native executables for Windows, Linux, and macOS on x64
  and Arm64.

[`docs/ROADMAP.md`](docs/ROADMAP.md) records availability, current state and planned work.

## Run from source

From the repository root, build Release. Run every build and test command with an owned
`ASPIRE_HOME` under your temp folder, and keep it until the tests finish: without it, the AppHost
SDK installs the Aspire bundle into the shared `%USERPROFILE%/.aspire` directory.
Set `ASPIRE_HOME` to an owned directory named `opencode-mcp-aspire-home` under your system temp
folder before the first build.

```text
dotnet build OpenCode.Mcp.slnx --configuration Release
```

### Standard input and output

Launch the built assembly directly, not through `dotnet run`, so build output cannot enter the
protocol stream:

```text
dotnet ./src/OpenCode.Mcp/bin/Release/net10.0/OpenCode.Mcp.dll --transport stdio
```

Configure a local MCP client's command as `dotnet` and its arguments as the absolute path to that
`OpenCode.Mcp.dll`, followed by `--transport` and `stdio`. Standard input and output belong to the
client; diagnostic logs go to standard error. Omitting `--transport` also selects stdio.
Standard output contains protocol messages only: no banner, log line, or prompt. When the client
closes standard input, the stdio session ends, the host shuts down, and the process exits with
code 0. No extra shutdown request is required.

The client must support MCP revision `2026-07-28`: discover with `server/discover`, list tools, then
call `bootstrap_ping` with no arguments. Its result is exactly `pong opencode-mcp <version>`, where
`<version>` is the product assembly's informational version without source metadata. This verifies
connectivity only; it does not contact OpenCode or a model provider.

### Local Streamable HTTP

```text
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

## Build and run a local native executable

You can build a local native payload from source with the
repository-selected .NET SDK. Keep the owned `ASPIRE_HOME` from the source-build instructions.
On Windows x64, install Visual Studio or Build Tools with **Desktop development with C++**, MSVC
x64 tools and a Windows SDK first. The tool checks the standard Installer `vswhere.exe`, component,
SDK libraries, linker and export inspector; it adds the Installer folder only to its publish
child's `PATH`, not your machine settings. It installs no prerequisite.

From the repository root on Windows x64:

```text
dotnet run tools/native.cs -- verify --rid win-x64
dotnet run tools/native.cs -- stage --rid win-x64 --publish-directory artifacts/native/win-x64/publish
```

Use the lowercase `tools/native.cs` spelling. `verify` publishes and
tests the native product; `stage` tests the normalized payload and its extracted ZIP. Both must
exit zero. Receipts, logs, symbols and the payload inventory are build evidence outside the
runtime payload. The archive is `artifacts/native/win-x64/opencode-mcp-win-x64.zip`; extract all
its files together, including license, notices and metadata. Launch the extracted `opencode-mcp.exe`
directly with `--transport stdio`, or with
`--transport http --urls http://127.0.0.1:0 --environment Development` for HTTP.

For a local MCP client, set the command to the absolute extracted `opencode-mcp.exe` path and the
arguments to `--transport`, `stdio`. Native execution needs no installed .NET runtime; building
and verification still need the SDK. The transports, protocol and HTTP restrictions are the same
as above. A source-built Windows payload is not six-platform support or an official release.

## Developing

Building the repository needs the .NET SDK version that [`global.json`](global.json) selects.

For orchestrated HTTP development, use the repository's pinned Aspire local tool from the root.
Give its bundle, CLI state and dashboard data an owned directory before the first build:
set `ASPIRE_HOME` as above and `ASPIRE_DASHBOARD_DATA_DIRECTORY` to its `dashboard` subdirectory.

```text
dotnet tool restore
dotnet tool run aspire start --apphost src/OpenCode.Mcp.AppHost/OpenCode.Mcp.AppHost.csproj --isolated --format Json --non-interactive
dotnet tool run aspire wait mcp --apphost src/OpenCode.Mcp.AppHost/OpenCode.Mcp.AppHost.csproj --status healthy --timeout 120 --non-interactive
dotnet tool run aspire describe mcp --apphost src/OpenCode.Mcp.AppHost/OpenCode.Mcp.AppHost.csproj --format Json --non-interactive
```

Use the discovered `http` authority plus `/mcp`; no port is fixed. This starts only the MCP product,
not OpenCode or a model provider. Stop the run you started when finished:

```text
dotnet tool run aspire stop --apphost src/OpenCode.Mcp.AppHost/OpenCode.Mcp.AppHost.csproj --non-interactive
```

Do not start this exact AppHost path over somebody else's run. The local tool pins the CLI paired
with the AppHost SDK rather than using a global Aspire installation. `--isolated` isolates ports,
not the machine profile: hosting still uses the real `.aspire/cli/bch` and `.aspire/dev-certs` paths
and can use the developer-certificate store. The full state boundary is documented in
[hosting and packaging](docs/architecture/hosting-and-packaging.md#aspire-apphost-and-servicedefaults).
Keep dashboard stores and login URLs private.

Before a pull request opens, run the local check that
[`docs/engineering/quality-gates.md`](docs/engineering/quality-gates.md) describes; it has the
exact commands. A Native AOT publish on Windows has extra machine prerequisites, listed in
[`docs/architecture/hosting-and-packaging.md`](docs/architecture/hosting-and-packaging.md).

Contributors and coding agents start at [`AGENTS.md`](AGENTS.md), which routes each kind of work to
its canon; [`CONTEXT.md`](CONTEXT.md) defines the terms this repository uses.

See [Contributing](.github/CONTRIBUTING.md) for issues, review and pull requests. CI must
check managed Release builds and all test modules on Windows, Linux and macOS; these checks do
not prove native or package support. [Quality gates](docs/engineering/quality-gates.md#continuous-integration)
own the CI contract, and the roadmap records hosted verification and protection status.

## License

Licensed under MIT, see [LICENSE](LICENSE) for the full text. Content derived from the OpenCode .NET
SDK repository carries its own notice in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
