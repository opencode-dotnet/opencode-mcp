namespace OpenCode.Mcp.Tools.Native;

internal sealed record NativeCommand(string Operation, string? Rid, string? Output, string? PublishDirectory, string? Path, string? Receipts,
    string Manifest = "packaging/native-manifest.json", string? GithubOutput = null, string? Evidence = null, string? Feed = null, string? Version = null,
    string? Assets = null, string? Commit = null);
