namespace OpenCode.Mcp.Tests.Support;

internal static class HostData
{
    public static string ApplicationName { get; } = typeof(McpApp).Assembly.GetName().Name
        ?? throw new InvalidOperationException("The product assembly has no name.");
}
