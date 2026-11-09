namespace OpenCode.Mcp.Tools.Native.Abstractions;

internal sealed record NativeProcessRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment, TimeSpan Timeout)
{
    public bool InheritEnvironment { get; init; } = true;
    public NativeContainerOwner? Container { get; init; }
}
