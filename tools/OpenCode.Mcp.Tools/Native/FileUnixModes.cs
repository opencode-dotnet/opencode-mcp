using System.IO.Abstractions;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class FileUnixModes(IFileSystem files) : IUnixFileModes
{
    public UnixFileMode? Read(string path) => OperatingSystem.IsWindows() ? null : files.File.GetUnixFileMode(path);

    public void Write(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Unix file modes are unavailable on Windows.");
        }
        files.File.SetUnixFileMode(path, mode);
    }
}
