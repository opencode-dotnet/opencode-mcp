using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using Testably.Abstractions;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeManifestStoreTests
{
    [Test]
    [Timeout(30_000)]
    public async Task ReadAsync_Should_Refuse_An_Empty_Runtime_Set(CancellationToken token)
    {
        var fixture = new NativeManifestFixture();
        (fixture.Document["runtimes"]?.AsArray() ?? throw new InvalidOperationException()).Clear();
        var files = new MockFileSystem();
        var path = await fixture.WriteAsync(files, token);
        await Assert.That(async () => await new NativeManifestStore(files).ReadAsync(path, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Manifest: runtimes must contain at least one runtime.", StringComparison.Ordinal);
    }

    [Test]
    [Timeout(30_000)]
    public async Task ReadAsync_Should_Load_The_Committed_Approved_Rid_And_Runner_Order(CancellationToken token)
    {
        var files = new RealFileSystem();
        var root = files.Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var manifest = await new NativeManifestStore(files).ReadAsync(files.Path.Combine(root, "packaging", "native-manifest.json"), token);
        await Assert.That(manifest.Runtimes.Select(static runtime => runtime.Rid + "/" + runtime.Runner)).IsEquivalentTo(
            ["win-x64/windows-2025", "win-arm64/windows-11-arm", "linux-x64/ubuntu-24.04", "linux-arm64/ubuntu-24.04-arm",
                "osx-x64/macos-15-intel", "osx-arm64/macos-26"], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task ReadAsync_Should_Accept_Structural_Mappings_Without_A_Second_Production_Rid_List(CancellationToken token)
    {
        var fixture = new NativeManifestFixture();
        var runtimes = fixture.Document["runtimes"]?.AsArray() ?? throw new InvalidOperationException();
        runtimes.RemoveAt(0);
        (runtimes[0] ?? throw new InvalidOperationException())["runner"] = "fixture-runner";
        var files = new MockFileSystem();
        NativeManifest? manifest = null;
        var path = await fixture.WriteAsync(files, token);
        await Assert.That(async () => manifest = await new NativeManifestStore(files).ReadAsync(path, token)).ThrowsNothing();
        var selected = await Assert.That(manifest).IsNotNull();
        await Assert.That(selected.Runtimes.Select(static runtime => runtime.Rid + "/" + runtime.Runner)).IsEquivalentTo(
            ["win-arm64/fixture-runner", "linux-x64/ubuntu-24.04", "linux-arm64/ubuntu-24.04-arm", "osx-x64/macos-15-intel", "osx-arm64/macos-26"], CollectionOrdering.Matching);
    }

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
    [Arguments("duplicate", "Manifest: duplicate RID win-x64.")]
    [Arguments("rid", "Manifest: unknown RID shape win-any.")]
    [Arguments("runner", "Manifest: runner is required for win-x64.")]
    [Arguments("image", "Manifest: builderImage must be digest-qualified (sha256).")]
    [Arguments("runtimeImage", "Manifest: runtimeImage must be digest-qualified (sha256).")]
    [Arguments("runtimeField", "Manifest: unknown field surprise.")]
    [Arguments("unknown", "Manifest: unknown field surprise.")]
    public async Task ReadAsync_Should_Refuse_A_Malformed_Manifest(string defect, string diagnostic, CancellationToken token)
    {
        var fixture = new NativeManifestFixture();
        if (defect is "duplicate" or "rid" or "runner" or "runtimeField")
        {
            var runtimes = fixture.Document["runtimes"]?.AsArray() ?? throw new InvalidOperationException("Fixture runtimes missing.");
            var first = runtimes[0] ?? throw new InvalidOperationException();
            if (defect == "duplicate")
            {
                runtimes.Add(first.DeepClone());
            }
            else
            {
                first[defect switch { "rid" => "rid", "runner" => "runner", _ => "surprise" }] = defect == "rid" ? "win-any" : "";
            }
        }
        else if (defect is "image" or "runtimeImage")
        {
            fixture.Document[defect == "image" ? "builderImage" : "runtimeImage"] = "registry.test/builder:latest";
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
