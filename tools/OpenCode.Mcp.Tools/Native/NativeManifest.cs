namespace OpenCode.Mcp.Tools.Native;

internal sealed record NativeManifest(int SchemaVersion, string BuilderImage, string RuntimeImage, IReadOnlyList<NativeRuntime> Runtimes);
