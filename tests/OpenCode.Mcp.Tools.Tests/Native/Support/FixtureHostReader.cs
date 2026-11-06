using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal sealed class FixtureHostReader(NativeHost host) : INativeHostReader
{
    public NativeHost Read() => host;
}
