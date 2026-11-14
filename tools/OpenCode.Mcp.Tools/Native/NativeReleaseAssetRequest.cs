namespace OpenCode.Mcp.Tools.Native;

internal sealed record NativeReleaseAssetRequest(NativeManifest Manifest, string Receipts, string Evidence, string Output, string Version);
