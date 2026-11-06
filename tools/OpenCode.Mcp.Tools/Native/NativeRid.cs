namespace OpenCode.Mcp.Tools.Native;

internal sealed record NativeRid(string Value)
{
    public bool IsWindows => Value.StartsWith("win-", StringComparison.Ordinal);
    public bool IsLinux => Value.StartsWith("linux-", StringComparison.Ordinal);
    public bool IsMacOS => Value.StartsWith("osx-", StringComparison.Ordinal);
    public string CommandName => IsWindows ? "opencode-mcp.exe" : "opencode-mcp";
    public string PublishedName => IsWindows ? "OpenCode.Mcp.exe" : "OpenCode.Mcp";
    public string ArchiveExtension => IsWindows ? ".zip" : ".tar.gz";
    public string AssetsPath { get; } = "src/OpenCode.Mcp/obj/project.assets.json";
}
