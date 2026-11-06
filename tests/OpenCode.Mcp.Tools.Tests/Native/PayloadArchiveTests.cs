using System.Formats.Tar;
using System.IO.Compression;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class PayloadArchiveTests
{
    [Test]
    [Timeout(30_000)]
    public async Task ExtractAsync_Should_Write_No_Mode_For_A_Windows_Rid(CancellationToken token)
    {
        var fixture = new PayloadFixture();
        await fixture.PrepareAsync(token);
        var archive = new PayloadArchive(fixture.Files, fixture.Modes, fixture.Rid);
        var path = fixture.Files.Path.Combine(fixture.Files.Path.GetTempPath(), "payload.tar.gz");
        var extracted = fixture.Files.Path.Combine(fixture.Files.Path.GetTempPath(), "extracted");
        await archive.CreateAsync(fixture.Root, path, [.. fixture.Inventory.Select(static file => file with { UnixMode = 493 })], token);
        await archive.ExtractAsync(path, extracted, token);
        await Assert.That(fixture.Modes.Written).IsEmpty();
        await Assert.That(await fixture.Files.File.ReadAllTextAsync(fixture.Files.Path.Combine(extracted, "LICENSE"), token)).IsEqualTo("license");
    }

    [Test]
    [Timeout(30_000)]
    public async Task ExtractAsync_Should_Preserve_Executable_Unix_Mode_On_Any_Host(CancellationToken token)
    {
        var files = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Linux));
        var root = files.Path.Combine(files.Path.GetTempPath(), "payload");
        files.Directory.CreateDirectory(root);
        var command = files.Path.Combine(root, "opencode-mcp");
        await files.File.WriteAllTextAsync(command, "native", token);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        var modes = new FakeUnixFileModes();
        modes.Write(command, mode);
        var rid = new NativeRid("linux-x64");
        var inventory = await new PayloadInventory(files, modes, rid).CaptureAsync(root, token);
        await Assert.That(inventory.Single().UnixMode).IsEqualTo(493);
        var archive = new PayloadArchive(files, modes, rid);
        var path = files.Path.Combine(files.Path.GetTempPath(), "payload.tar.gz");
        var extracted = files.Path.Combine(files.Path.GetTempPath(), "extracted");
        await archive.CreateAsync(root, path, inventory, token);
        using var bytes = new MemoryStream(await files.File.ReadAllBytesAsync(path, token));
        using var compressed = new GZipStream(bytes, CompressionMode.Decompress);
        using var tar = new TarReader(compressed);
        var entry = await Assert.That(await tar.GetNextEntryAsync(cancellationToken: token)).IsNotNull();
        await Assert.That(entry.Name).IsEqualTo("opencode-mcp");
        await Assert.That(entry.Mode).IsEqualTo(mode);
        await archive.ExtractAsync(path, extracted, token);
        await Assert.That(modes.Read(files.Path.Combine(extracted, "opencode-mcp"))).IsEqualTo(mode);
        await Assert.That(await new PayloadInventory(files, modes, rid).CaptureAsync(extracted, token)).IsEquivalentTo(inventory, CollectionOrdering.Matching);
        await Assert.That(await files.File.ReadAllTextAsync(files.Path.Combine(extracted, "opencode-mcp"), token)).IsEqualTo("native");
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(".zip")]
    [Arguments(".tar.gz")]
    public async Task ExtractAsync_Should_Preserve_The_Entire_Archived_Payload(string extension, CancellationToken token)
    {
        var fixture = new PayloadFixture();
        await fixture.PrepareAsync(token);
        var path = fixture.Files.Path.Combine(fixture.Files.Path.GetTempPath(), "payload" + extension);
        var extracted = fixture.Files.Path.Combine(fixture.Files.Path.GetTempPath(), "extracted");
        var archive = new PayloadArchive(fixture.Files, fixture.Modes, fixture.Rid);
        await archive.CreateAsync(fixture.Root, path, fixture.Inventory, token);
        await archive.ExtractAsync(path, extracted, token);
        await Assert.That(fixture.Files.Directory.EnumerateFiles(extracted).Select(path => fixture.Files.Path.GetFileName(path) ?? "").Order(StringComparer.Ordinal))
            .IsEquivalentTo(["LICENSE", "z-asset"], CollectionOrdering.Matching);
        await Assert.That(await fixture.Files.File.ReadAllTextAsync(fixture.Files.Path.Combine(extracted, "LICENSE"), token)).IsEqualTo("license");
        await Assert.That(await fixture.Files.File.ReadAllTextAsync(fixture.Files.Path.Combine(extracted, "z-asset"), token)).IsEqualTo("asset");
    }
}
