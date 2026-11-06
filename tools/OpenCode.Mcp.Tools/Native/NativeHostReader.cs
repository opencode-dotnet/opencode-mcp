using System.Runtime.InteropServices;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeHostReader : INativeHostReader
{
    public NativeHost Read()
    {
        var os = "unsupported";
        if (OperatingSystem.IsWindows())
        {
            os = "win";
        }
        else if (OperatingSystem.IsLinux())
        {
            os = "linux";
        }
        else if (OperatingSystem.IsMacOS())
        {
            os = "osx";
        }
        string[] names = ["PATH", "ProgramFiles(x86)", "RUNNER_NAME", "ImageOS", "ImageVersion", "GITHUB_EVENT_NAME", "GITHUB_SHA", "GITHUB_EVENT_PATH"];
        return new NativeHost(os, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            RuntimeInformation.OSDescription, names.ToDictionary(static name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal))
        {
            WorkingDirectory = Environment.CurrentDirectory,
        };
    }
}
