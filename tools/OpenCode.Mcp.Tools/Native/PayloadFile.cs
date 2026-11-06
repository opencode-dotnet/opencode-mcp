namespace OpenCode.Mcp.Tools.Native;

internal sealed record PayloadFile(string Path, string Sha256, long Size, int? UnixMode);
