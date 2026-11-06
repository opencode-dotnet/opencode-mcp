namespace OpenCode.Mcp.Tools.Native;

internal sealed record NativeCommand(string Operation, string? Rid, string? Output, string? PublishDirectory, string? Path, string? Receipts,
    string Manifest = "packaging/native-manifest.json");
