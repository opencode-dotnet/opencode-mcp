using OpenCode.Mcp.Hosting.Models;

namespace OpenCode.Mcp.Hosting.Abstractions;

/// <summary>Creates the selected production host after transport validation.</summary>
public interface IMcpHostFactory
{
    /// <summary>Creates an unstarted host for the selected transport.</summary>
    /// <param name="options">The validated transport and remaining host arguments.</param>
    /// <returns>A host whose caller owns execution and disposal.</returns>
    public IHost Create(LaunchOptions options);
}
