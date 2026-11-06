namespace OpenCode.Mcp.Tools.Native;

internal sealed record NativeBuildRequest(string Root, string Output, string Rid, NativeHost Host, NativeManifest Manifest);
