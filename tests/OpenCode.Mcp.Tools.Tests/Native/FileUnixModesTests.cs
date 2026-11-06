using OpenCode.Mcp.Tools.Native;
using Testably.Abstractions.Testing;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class FileUnixModesTests
{
    [Test]
    public async Task Write_Should_Refuse_Windows_And_Read_No_Mode_There()
    {
        var files = new MockFileSystem();
        var path = files.Path.Combine(files.Path.GetTempPath(), "command");
        files.Directory.CreateDirectory(files.Path.GetTempPath());
        files.Initialize().WithFile(path);
        var modes = new FileUnixModes(files);
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(modes.Read(path)).IsNull();
            await Assert.That(() => modes.Write(path, (UnixFileMode)493)).ThrowsExactly<InvalidOperationException>()
                .WithMessage("Unix file modes are unavailable on Windows.", StringComparison.Ordinal);
        }
        else
        {
            modes.Write(path, (UnixFileMode)493);
            await Assert.That(modes.Read(path)).IsEqualTo((UnixFileMode)493);
        }
    }
}
