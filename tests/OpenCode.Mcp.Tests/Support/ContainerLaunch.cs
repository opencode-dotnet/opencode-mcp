namespace OpenCode.Mcp.Tests.Support;

/// <summary>Identifies the exact container and immutable payload transferred to the process owner.</summary>
internal sealed record ContainerLaunch(string Name, string Image, string PayloadDirectory, string Executable);
