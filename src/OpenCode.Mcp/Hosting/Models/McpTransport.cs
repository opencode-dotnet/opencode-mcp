namespace OpenCode.Mcp.Hosting.Models;

/// <summary>Identifies the protocol channel used for one MCP server launch.</summary>
public enum McpTransport
{
    /// <summary>Uses standard input and output without an HTTP listener.</summary>
    Stdio,

    /// <summary>Uses a local Streamable HTTP endpoint.</summary>
    Http,
}
