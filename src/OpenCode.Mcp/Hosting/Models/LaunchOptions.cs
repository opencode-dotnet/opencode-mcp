namespace OpenCode.Mcp.Hosting.Models;

/// <summary>Carries the selected protocol channel and the arguments left for host configuration.</summary>
/// <param name="Transport">The protocol channel for this launch.</param>
/// <param name="HostArguments">The host arguments in their original order.</param>
public sealed record LaunchOptions(McpTransport Transport, IReadOnlyList<string> HostArguments);
