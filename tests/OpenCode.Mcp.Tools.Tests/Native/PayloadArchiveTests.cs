using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class PayloadArchiveTests
{
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
        var archive = new PayloadArchive(fixture.Files);
        await archive.CreateAsync(fixture.Root, path, fixture.Inventory, token);
        await archive.ExtractAsync(path, extracted, token);
        await Assert.That(fixture.Files.Directory.EnumerateFiles(extracted).Select(path => fixture.Files.Path.GetFileName(path) ?? "").Order(StringComparer.Ordinal))
            .IsEquivalentTo(["LICENSE", "z-asset"], CollectionOrdering.Matching);
        await Assert.That(await fixture.Files.File.ReadAllTextAsync(fixture.Files.Path.Combine(extracted, "LICENSE"), token)).IsEqualTo("license");
        await Assert.That(await fixture.Files.File.ReadAllTextAsync(fixture.Files.Path.Combine(extracted, "z-asset"), token)).IsEqualTo("asset");
    }
}
