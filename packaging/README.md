# OpenCode MCP

Unofficial. This project is not affiliated with or endorsed by the OpenCode team.

`opencode-mcp` is an MCP server for OpenCode, a thin adapter over the OpenCode .NET SDK.
An MCP client connects to it over the Model Context Protocol and uses its MCP tools.

The server runs over standard input and output by default, or as a loopback-only
Streamable HTTP endpoint on request. It offers one MCP tool, `bootstrap_ping`, a
connectivity probe that checks only the connection between the MCP client and this
server, not OpenCode or a model provider.

The native server needs no installed .NET runtime.

This is a pre-release. No package or binary is published.
