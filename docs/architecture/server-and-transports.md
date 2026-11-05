# Server and Transports Architecture

Date: 2026-11-06

Canonical current rules for transport-mode selection, host composition, the MCP protocol
revision, local HTTP exposure, and the MCP tool surface. Projects, Aspire, Native AOT, and
packaging live in `hosting-and-packaging.md`. This document states the decided contract; which
parts are implemented is operational state in `../ROADMAP.md`.

## Mode selection

One executable, `opencode-mcp`, runs in exactly one transport mode, and it selects that mode from
its command-line arguments before it composes any host.

- `--transport stdio` runs the MCP server over standard input and output. It is the default: with
  no selector the server runs in stdio mode, so a package launch never opens an HTTP listener by
  accident.
- `--transport http` runs the MCP server as a local Streamable HTTP endpoint. The AppHost selects
  this mode (`hosting-and-packaging.md`).

The selector is the token `--transport` followed by exactly one value, `stdio` or `http`, both
matched ordinally. The selector refuses an unknown value (including a different casing such as
`HTTP`), a missing value, a repeated selector even when the values agree, and the
`--transport=<value>` spelling, rather than forwarding an invalid mode into generic-host
configuration. Every other argument passes to the host unchanged, as a read-only snapshot with the
transport pair removed.

A refused selection fails before any listener starts or any protocol input is read: the process
writes a diagnostic that names the permitted values to standard error, writes nothing to standard
output, and exits with code 2. Selection is a stateless policy (`TransportSelector`), a static class
that is not registered in the container (`../engineering/coding-style.md`).

## Host composition

Both modes use the same production composition: one wiring owner, `McpApp.Configure`, configures logging,
the standard ServiceDefaults, the server identity, the MCP server, and its tools for either mode.
Overrides apply after the production registrations, so a test exercises the actual root and
replaces only a real seam. A test-only delegate or in-memory server is no substitute for the
production registration, endpoint, and executable.

The entry point hands `Console.Error` to `McpApp.CreateApplication`, which constructs the launcher
directly from `McpHostFactory` (carrying the optional host override) and the error writer. There is
no launcher container. `McpApplication.RunAsync` parses before calling `IMcpHostFactory.Create`;
its `ArgumentException` catch covers only the selector, not factory creation or host execution.
Those failures propagate. The generic-host `RunAsync` extension owns shutdown and host disposal,
awaiting asynchronous disposal where supported; the launcher returns zero after it completes.

- **Logging.** The composition clears the default logging providers, adds the console provider with
  every level written to standard error, and only then adds the standard ServiceDefaults, so the
  clearing does not remove the OpenTelemetry provider. Neither mode writes a banner or a console
  exporter's output to standard output.
- **Identity.** The singleton `ProductIdentity` supplies the name `opencode-mcp` and the product
  assembly's informational version to the MCP server, with only an optional `+` source suffix
  removed and the prerelease portion preserved; a missing informational version fails explicitly.
- **Stdio mode** uses the generic host. It has no Kestrel listener and no web server registration.
  `McpHostFactory.Create` uses `Host.CreateApplicationBuilder` with the remaining arguments and
  calls the shared composition without applying HTTP endpoint policy.
  The MCP client owns standard input and output; standard output carries MCP protocol messages only
  (no banner, log line, or prompt). When the stdio session ends at end of input, the MCP C# SDK's
  hosted service stops the host and the process exits with code 0; there is no home-grown shutdown
  handshake.
- **HTTP mode** uses the slim web host (`WebApplication.CreateSlimBuilder`), which keeps the host
  compatible with Native AOT. It maps the stateless Streamable HTTP transport at `/mcp`; a response
  carries no session identifier.
- **Lifetime.** The generic host's lifetime owns Ctrl+C and SIGTERM for the server run in both
  modes; mode selection runs before any host exists.

## Local HTTP exposure

The HTTP mode is a local development and integration endpoint. It is never a public service.

- **Loopback only.** The endpoint policy (`HttpEndpointPolicy`, a stateless static class) resolves
  the listening URL before Kestrel starts. The default is `http://127.0.0.1:0`, a dynamic loopback
  port. Numeric IPv4/IPv6 loopbacks retain their ports, and `localhost` is normalized to
  `127.0.0.1`. Semicolon-separated URL entries are validated in order; explicit blanks are refused.
  A wildcard or non-loopback host, HTTPS, user information, a non-root path, query, fragment, or
  malformed URL is refused. A `Kestrel:Endpoints` section with a value or descendants is refused
  before URL resolution, even when its endpoints are loopback. The boundary is enforced at startup;
  the factory configures Kestrel endpoint configuration with `reloadOnChange: false`.
- **Host filtering.** Requests are accepted only for the hosts `localhost`, `127.0.0.1`, and
  `[::1]`. The slim host's Host-filtering startup filter applies that allow-list, with
  `AllowEmptyHosts=false`; an untrusted or absent Host receives HTTP 400.
- **Origin-header validation.** The MCP transport specification requires a Streamable HTTP server
  to validate the `Origin` header; host filtering and the absence of a permissive CORS policy do
  not do that. A request with an untrusted `Origin` and an otherwise valid loopback `Host` is
  rejected by product middleware in `McpHostFactory`, before the MCP handler reads the body. Neither
  the MCP C# SDK's handler nor ASP.NET Core Host filtering supplies this Origin check. The pure
  `HttpEndpointPolicy.IsAllowedOrigin` decision accepts missing Origin for non-browser clients.
  A present Origin must be an absolute HTTP/HTTPS origin with exactly `localhost`, `127.0.0.1`, or
  `[::1]` as host, at any valid port; it need not share the listener's port or scheme. Empty, opaque
  `null`, list-valued, wildcard, remote, deceptive-suffix, malformed, credential-bearing, and
  non-origin URI values are refused with an empty HTTP 403. The check covers every request,
  including MCP path variants and the Development health endpoints.
  Accepting an Origin or its absence is not authentication.
- **No CORS.** The server registers no CORS policy and sends no permissive CORS header.
- **No production authentication** is part of the HTTP mode.
- **Health endpoints.** `/health` and `/alive` come from the standard ServiceDefaults and exist only
  in the Development environment; outside it they are absent.

## Protocol

The MCP server speaks MCP revision `2026-07-28` only, through the MCP C# SDK.

- A client uses modern discovery (`server/discover`), `tools/list`, and `tools/call`. This revision
  has no `initialize` handshake, and a legacy fallback never counts as proof of the modern
  protocol. An ordinary response carries `resultType` `complete`.
- Discovery is optional in the specification. This product's test contract requires it, and
  requires discovery to report the server identity, version, and capabilities.
- Out of scope: legacy SSE, stateful compatibility sessions, the Apps and Tasks extensions,
  sampling, roots, subscriptions, and production authentication.
- Protocol JSON uses the MCP C# SDK's generated serialization metadata. A product type that crosses
  the protocol later brings its own source-generated metadata through the effective serializer
  options; there is no reflection fallback.

## MCP tools

- Each tool type is registered by its type through `WithTools<T>()`, never by assembly scanning.
  Generic registration roots the type's members for trimming; it does not make the tool
  reflection-free.
- A tool type is a sealed instance class, even when it touches no state: the MCP C# SDK constructs
  tool types through dependency injection per invocation, and later tools take collaborators.
  `BootstrapTools.Ping` returns `pong` with the server name and version. It is an instance method
  because the MCP C# SDK constructs tools through DI and the tool takes `ProductIdentity`.
- Every tool carries a meaningful description and accurate annotations (read-only, idempotent,
  destructive, open-world).

### Connectivity probe

`bootstrap_ping` (`BootstrapTools.Ping`) is the connectivity probe: it takes no input and returns
the text `pong` followed by the server name and version (`pong {name} {version}`). It is read-only,
idempotent, non-destructive, and closed-world, reads no secret,
and does no outside-world work. It is a supported diagnostic that stays when OpenCode-backed tools
arrive, not scaffolding. It proves connectivity between an MCP client and the MCP server only: it
does not call the OpenCode .NET SDK, an OpenCode server process, or a model provider, and it is not
a health check of any of them.

### OpenCode-backed tools

An MCP tool that operates on OpenCode consumes the published OpenCode .NET SDK package, never that
SDK's source or internals, and arrives with its first consumer. It needs a reachable OpenCode
server process and integration tests of its own; how the MCP server reaches that process is not
decided here. Connectivity of the probe is not evidence of OpenCode integration.
