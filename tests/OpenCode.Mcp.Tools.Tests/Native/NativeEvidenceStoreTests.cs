using System.IO.Abstractions;
using NSubstitute;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeEvidenceStoreTests
{
    [Test]
    public async Task ReadReceiptLocationsAsync_Should_Preserve_Ordinal_Discovery_Order(CancellationToken token)
    {
        var files = new MockFileSystem();
        var store = new NativeEvidenceStore(files);
        var root = files.Path.Combine(files.Path.GetTempPath(), "receipts");
        var last = files.Path.Combine(root, "z", "nested");
        var first = files.Path.Combine(root, "a");
        var middle = files.Path.Combine(root, "m");
        await store.WriteAsync(files.Path.Combine(last, "receipt.json"), ReceiptFixture.Create(new("win-x64", "windows-2025")), token);
        await store.WriteAsync(files.Path.Combine(first, "receipt.json"), ReceiptFixture.Create(new("osx-arm64", "macos-26")), token);
        await store.WriteAsync(files.Path.Combine(middle, "receipt.json"), ReceiptFixture.Create(new("linux-x64", "ubuntu-24.04")), token);
        var decorated = DecorateDiscovery(files, [files.Path.Combine(last, "receipt.json"), files.Path.Combine(first, "receipt.json"), files.Path.Combine(middle, "receipt.json")]);
        var result = await new NativeEvidenceStore(decorated).ReadReceiptLocationsAsync(root, token);
        await Assert.That(result.Select(static location => location.Receipt.Rid)).IsEquivalentTo(["osx-arm64", "linux-x64", "win-x64"], CollectionOrdering.Matching);
        await Assert.That(result.Select(static location => location.Directory)).IsEquivalentTo([first, middle, last], CollectionOrdering.Matching);
    }

    private static IFileSystem DecorateDiscovery(MockFileSystem files, IEnumerable<string> paths)
    {
        var directory = Substitute.For<IDirectory>();
        directory.Exists(Arg.Any<string>()).Returns(call => files.Directory.Exists(call.Arg<string>()));
        directory.EnumerateFiles(Arg.Any<string>(), "receipt.json", SearchOption.AllDirectories).Returns(paths);
        var decorated = Substitute.For<IFileSystem>();
        decorated.Directory.Returns(directory);
        decorated.DirectoryInfo.Returns(files.DirectoryInfo);
        decorated.DriveInfo.Returns(files.DriveInfo);
        decorated.File.Returns(files.File);
        decorated.FileInfo.Returns(files.FileInfo);
        decorated.FileStream.Returns(files.FileStream);
        decorated.FileSystemWatcher.Returns(files.FileSystemWatcher);
        decorated.Path.Returns(files.Path);
        return decorated;
    }
}
