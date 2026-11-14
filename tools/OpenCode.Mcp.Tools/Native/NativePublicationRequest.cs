namespace OpenCode.Mcp.Tools.Native;

internal sealed record NativePublicationRequest(NativeManifest Manifest, string Receipts, string Feed, string Assets, string Version, string Commit, string Installers);
