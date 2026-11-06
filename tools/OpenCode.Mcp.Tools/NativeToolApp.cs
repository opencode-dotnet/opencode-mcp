using OpenCode.Mcp.Tools.Native;
using Testably.Abstractions;

namespace OpenCode.Mcp.Tools;

/// <summary>Composes the native verification tool without changing the product's composition.</summary>
public static class NativeToolApp
{
    /// <summary>Runs a native tool command with explicit console ownership and cancellation.</summary>
    public static Task<int> RunAsync(string[] arguments, TextWriter output, TextWriter errors, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(errors);
        return new NativeApplication(new RealFileSystem(), new SystemNativeProcessRunner(), new NativeHostReader(), output, errors).RunAsync(arguments, token);
    }
}
