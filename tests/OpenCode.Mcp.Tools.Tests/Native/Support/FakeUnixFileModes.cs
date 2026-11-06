using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal sealed class FakeUnixFileModes : IUnixFileModes
{
    private readonly Dictionary<string, UnixFileMode> _modes = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, UnixFileMode> Written => _modes;
    public UnixFileMode? Read(string path) => _modes.TryGetValue(path, out var mode) ? mode : null;
    public void Write(string path, UnixFileMode mode) => _modes[path] = mode;
}
