namespace OpenCode.Mcp.Tools.Native.Abstractions;

internal sealed record NativeProcessRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment, TimeSpan Timeout);
