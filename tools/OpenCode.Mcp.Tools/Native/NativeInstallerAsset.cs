using System.Collections.Immutable;

namespace OpenCode.Mcp.Tools.Native;

internal sealed record NativeInstallerAsset(string Name, ImmutableArray<byte> Bytes, string Sha256);
