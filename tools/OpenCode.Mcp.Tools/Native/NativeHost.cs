namespace OpenCode.Mcp.Tools.Native;

internal sealed record NativeHost(string Os, string OsArchitecture, string ProcessArchitecture, string Description,
    IReadOnlyDictionary<string, string?> Environment)
{
    public string WorkingDirectory { get; init; } = "";
}
