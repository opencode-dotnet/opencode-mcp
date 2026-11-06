using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using Testably.Abstractions.Testing;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeManifestStoreTests
{
    [Test]
    [Timeout(30_000)]
    public async Task ReadAsync_Should_Name_A_Malformed_Schema_Field_Type(CancellationToken token)
    {
        var fixture = new NativeManifestFixture();
        fixture.Document["schemaVersion"] = "1";
        var files = new MockFileSystem();
        var path = await fixture.WriteAsync(files, token);
        await Assert.That(async () => await new NativeManifestStore(files).ReadAsync(path, token)).ThrowsExactly<InvalidOperationException>()
            .WithMessage("Manifest: schemaVersion must be 1.", StringComparison.Ordinal);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("order", "Manifest: runtimes must have the approved ordered RID/runner mapping.")]
    [Arguments("image", "Manifest: builderImage must be digest-qualified (sha256).")]
    [Arguments("unknown", "Manifest: unknown field surprise.")]
    public async Task ReadAsync_Should_Refuse_A_Malformed_Manifest(string defect, string diagnostic, CancellationToken token)
    {
        var fixture = new NativeManifestFixture();
        if (defect == "order")
        {
            var runtimes = fixture.Document["runtimes"]?.AsArray() ?? throw new InvalidOperationException("Fixture runtimes missing.");
            var last = runtimes[5];
            runtimes.RemoveAt(5);
            runtimes.Insert(0, last);
        }
        else if (defect == "image")
        {
            fixture.Document["builderImage"] = "registry.test/builder:latest";
        }
        else
        {
            fixture.Document["surprise"] = true;
        }
        var files = new MockFileSystem();
        var path = await fixture.WriteAsync(files, token);
        await Assert.That(async () => await new NativeManifestStore(files).ReadAsync(path, token)).ThrowsExactly<InvalidOperationException>()
            .WithMessage(diagnostic, StringComparison.Ordinal);
    }
}
