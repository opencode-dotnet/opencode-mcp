# OpenCode MCP

Shared vocabulary for the OpenCode MCP adapter. These terms distinguish its MCP boundary, the upstream OpenCode domain and product delivery.

## Language

### SDKs and participants

**OpenCode .NET SDK**:
The typed .NET client library for the OpenCode API used by SDK-backed adapter capabilities.
_Avoid_: SDK (when ambiguous), .NET SDK (when referring to the OpenCode client library), MCP C# SDK (when referring to the OpenCode client library)

**.NET SDK**:
The .NET development and tool-acquisition toolchain, distinct from the OpenCode .NET SDK client library.
_Avoid_: OpenCode .NET SDK (when referring to the toolchain), .NET runtime (when referring to the SDK toolchain)

**MCP C# SDK**:
The official C# implementation of MCP used for the product's protocol boundary, distinct from the OpenCode API client library.
_Avoid_: SDK (when ambiguous), OpenCode .NET SDK (when referring to the MCP library)

**MCP server**:
The product's MCP-facing adapter, distinct from the upstream OpenCode server process and the development AppHost.
_Avoid_: server (when ambiguous), OpenCode server process (when referring to this adapter), AppHost (when referring to this adapter)

**MCP client**:
A consumer of the MCP server's protocol interface.
_Avoid_: client (when ambiguous), OpenCode client (when referring to an MCP caller)

**OpenCode client**:
A client of the OpenCode API through the OpenCode .NET SDK.
_Avoid_: client (when ambiguous), MCP client (when referring to an OpenCode API caller)

**OpenCode server process**:
A running OpenCode API service that hosts OpenCode instances, distinct from this MCP server.
_Avoid_: server (when ambiguous), OpenCode instance (when referring to the process), MCP server (when referring to the upstream service)

**OpenCode instance**:
The directory-scoped working context inside an OpenCode server process. It is not the server process itself.
_Avoid_: instance (when ambiguous), OpenCode server process (when referring to a directory's working context)

**OpenCode session**:
A persistent conversation managed by OpenCode, distinct from an MCP interaction or a server process's lifetime.
_Avoid_: session (when ambiguous), MCP session (when referring to an OpenCode conversation), connection (when referring to an OpenCode conversation)

**MCP interaction**:
A protocol exchange between an MCP client and an MCP server, distinct from a persistent OpenCode session.
_Avoid_: OpenCode session (when referring to MCP protocol communication), conversation (when referring to MCP protocol communication)

### Tools and development hosting

**MCP tool**:
A named capability offered by the MCP server to an MCP client.
_Avoid_: tool (when ambiguous), repository tooling (when referring to an MCP capability)

**Repository tooling**:
Development and maintenance utilities for this repository, distinct from capabilities exposed as MCP tools.
_Avoid_: tool (when ambiguous), MCP tool (when referring to a development utility)

**Connectivity probe**:
The diagnostic MCP tool `bootstrap_ping`, whose meaning is limited to connectivity between an MCP client and the MCP server. It is not an OpenCode-server or model-provider health indicator.
_Avoid_: OpenCode health check, provider readiness check, network ping

**AppHost**:
The Aspire development orchestrator for the product, distinct from the installed MCP server command and the upstream OpenCode server process.
_Avoid_: MCP server (when referring to the orchestrator), OpenCode server process (when referring to the orchestrator), installed product (when referring to the orchestrator)

### Product delivery

**Native payload**:
The complete product file set delivered for one native platform, including the executable, required assets, notices and payload metadata. Channel wrappers and build/debug evidence are not part of the native payload.
_Avoid_: executable (when referring to the whole file set), delivery artifact (when referring to the shared payload)

**Delivery artifact**:
A package, archive or installer that carries a native payload through a distribution channel.
_Avoid_: artifact (when ambiguous), native payload (when referring to its delivery wrapper), executable (when referring to a package or installer)

**Package set**:
The root NuGet package together with its matching native implementation packages.
_Avoid_: package (when referring to the whole set), native payload (when referring to the package set)

**Package candidate**:
A proposed product distribution set whose identity ties it to one source revision, recorded build inputs and matching native payloads. Its identity is distinct from a product version or a SemVer pre-release stage.
_Avoid_: release candidate (ambiguous with a SemVer pre-release stage), version number (when referring to candidate identity)

**Product version**:
The release label associated with the product and its matching packages, distinct from a package candidate's source-and-payload identity.
_Avoid_: source revision (when referring to the version label), package candidate (when referring to the version label)

**Package acquisition**:
Obtaining and preparing a product package for use, distinct from native execution or an OpenCode API operation.
_Avoid_: native execution (when referring to package acquisition), OpenCode integration (when referring only to package acquisition)

**Native execution**:
Running the native form of the MCP server, distinct from acquiring its package or running a managed development build.
_Avoid_: package acquisition (when referring to execution), managed execution (when claiming native execution)
