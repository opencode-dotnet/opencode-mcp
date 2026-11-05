using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenCode.Mcp.Hosting;

namespace OpenCode.Mcp.Tools.Bootstrap;

/// <summary>Provides a harmless MCP connectivity probe without external work.</summary>
/// <param name="identity">The server identity included in the connectivity response.</param>
public sealed class BootstrapTools(ProductIdentity identity)
{
    /// <summary>Returns the deterministic bootstrap response.</summary>
    /// <returns>The text <c>pong</c> followed by the server name and version.</returns>
    [McpServerTool(
        Name = "bootstrap_ping",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false)]
    [Description("Returns pong with this server's name and version to verify connectivity; performs no external work.")]
    public string Ping() => $"pong {identity.Name} {identity.Version}";
}
