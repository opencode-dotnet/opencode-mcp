using System.Text.Json;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeMatrixTests
{
    [Test]
    [Timeout(30_000)]
    public async Task WriteAsync_Should_Include_The_Runtime_Image_For_Linux_Preparation(CancellationToken token)
    {
        var files = new MockFileSystem();
        var manifest = await new NativeManifestStore(files).ReadAsync(await new NativeManifestFixture().WriteAsync(files, token), token);
        var path = files.Path.Combine(files.Path.GetTempPath(), "github-output");
        files.Initialize().WithFile(path);
        await new NativeMatrix(files).WriteAsync(manifest, path, token);
        var line = (await files.File.ReadAllLinesAsync(path, token))[0].Split('=', 2)[1];
        using var linux = JsonDocument.Parse(line);
        await Assert.That(linux.RootElement.GetProperty("include").EnumerateArray()
            .Select(static entry => entry.TryGetProperty("runtimeImage", out var image) ? image.GetString() : null))
            .IsEquivalentTo(Enumerable.Repeat<string?>("registry.test/runtime@sha256:" + new string('b', 64), 2), CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task WriteAsync_Should_Project_Manifest_Order_Into_The_Linux_Native_And_Consumer_Matrices(CancellationToken token)
    {
        var files = new MockFileSystem();
        var manifest = await new NativeManifestStore(files).ReadAsync(await new NativeManifestFixture().WriteAsync(files, token), token);
        manifest = manifest with { Runtimes = [manifest.Runtimes[5], manifest.Runtimes[3], manifest.Runtimes[0], manifest.Runtimes[2], manifest.Runtimes[1], manifest.Runtimes[4]] };
        var path = files.Path.Combine(files.Path.GetTempPath(), "github-output");
        files.Initialize().WithFile(path);
        await new NativeMatrix(files).WriteAsync(manifest, path, token);
        var outputs = (await files.File.ReadAllLinesAsync(path, token)).Select(static line => line.Split('=', 2)).ToArray();
        await Assert.That(outputs.Select(static pair => pair[0])).IsEquivalentTo(["linux-matrix", "native-matrix", "consumer-matrix"], CollectionOrdering.Matching);
        using var linux = JsonDocument.Parse(outputs[0][1]);
        using var native = JsonDocument.Parse(outputs[1][1]);
        await Assert.That(linux.RootElement.EnumerateObject().Select(static property => property.Name)).IsEquivalentTo(["include"], CollectionOrdering.Matching);
        await Assert.That(native.RootElement.EnumerateObject().Select(static property => property.Name)).IsEquivalentTo(["include"], CollectionOrdering.Matching);
        await Assert.That(linux.RootElement.GetProperty("include").EnumerateArray().Select(static item => item.GetProperty("rid").GetString() + "/" + item.GetProperty("runner").GetString()))
            .IsEquivalentTo(["linux-arm64/ubuntu-24.04-arm", "linux-x64/ubuntu-24.04"], CollectionOrdering.Matching);
        foreach (var entry in linux.RootElement.GetProperty("include").EnumerateArray())
        {
            await Assert.That(entry.GetProperty("builderImage").GetString()).IsEqualTo("registry.test/builder@sha256:" + new string('a', 64));
        }
        await Assert.That(native.RootElement.GetProperty("include").EnumerateArray().Select(static item => item.GetProperty("rid").GetString() + "/" + item.GetProperty("runner").GetString()))
            .IsEquivalentTo(["osx-arm64/macos-26", "win-x64/windows-2025", "win-arm64/windows-11-arm", "osx-x64/macos-15-intel"], CollectionOrdering.Matching);
        using var consumer = JsonDocument.Parse(outputs[2][1]);
        await Assert.That(consumer.RootElement.EnumerateObject().Select(static property => property.Name)).IsEquivalentTo(["include"], CollectionOrdering.Matching);
        await Assert.That(consumer.RootElement.GetProperty("include").EnumerateArray().Select(static item => item.GetProperty("rid").GetString() + "/" + item.GetProperty("runner").GetString()))
            .IsEquivalentTo(["osx-arm64/macos-26", "linux-arm64/ubuntu-24.04-arm", "win-x64/windows-2025", "linux-x64/ubuntu-24.04", "win-arm64/windows-11-arm", "osx-x64/macos-15-intel"], CollectionOrdering.Matching);
        foreach (var entry in consumer.RootElement.GetProperty("include").EnumerateArray())
        {
            await Assert.That(entry.EnumerateObject().Select(static property => property.Name)).IsEquivalentTo(["rid", "runner"], CollectionOrdering.Matching);
        }
    }
}
