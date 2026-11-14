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
        var result = await store.ReadReceiptLocationsAsync(root, token);
        await Assert.That(result.Select(static location => location.Receipt.Rid)).IsEquivalentTo(["osx-arm64", "linux-x64", "win-x64"], CollectionOrdering.Matching);
        await Assert.That(result.Select(static location => location.Directory)).IsEquivalentTo([first, middle, last], CollectionOrdering.Matching);
    }
}
