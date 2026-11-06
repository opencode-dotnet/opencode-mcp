namespace OpenCode.Mcp.Tools.Native.Abstractions;

internal interface IUnixFileModes
{
    public UnixFileMode? Read(string path);
    public void Write(string path, UnixFileMode mode);
}
