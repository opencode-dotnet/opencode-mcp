# OpenCode MCP

Unofficial. This project is not affiliated with or endorsed by the OpenCode team.

`opencode-mcp` is an MCP server for OpenCode, a thin adapter over the OpenCode .NET SDK.
An MCP client connects to it over the Model Context Protocol and uses its MCP tools.

The server runs over standard input and output by default, or as a loopback-only
Streamable HTTP endpoint on request. It offers one MCP tool, `bootstrap_ping`, a
connectivity probe that checks only the connection between the MCP client and this
server, not OpenCode or a model provider.

The native server needs no installed .NET runtime.

This is a pre-release.

Install the command for one version and start it:

    dotnet tool install -g OpenCodeDotNet.Mcp --version <version>
    opencode-mcp --transport stdio

Or let an MCP client start it on demand, with nothing installed beforehand:

    dotnet dnx -v minimal OpenCodeDotNet.Mcp@<version> --yes -- --transport stdio

This package selects the native package for the machine's runtime; the installed command runs the
native executable directly and does not need the .NET runtime to run.
