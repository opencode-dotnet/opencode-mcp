using System.Text.Json.Nodes;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Support;

public sealed class PrivateSdkCopyTests
{
    [Test]
    public async Task PrepareAsync_Should_Copy_Only_The_Selected_Sdk_And_Required_Core_Runtime(CancellationToken token)
    {
        var files = new MockFileSystem();
        var installation = files.Path.GetFullPath("installed-sdk");
        string[] selected = [OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet", "sdk/10.0.403/.version", "sdk/10.0.403/dotnet.runtimeconfig.json",
            "shared/Microsoft.NETCore.App/10.0.14/coreclr.dll", "host/fxr/10.0.14/hostfxr.dll"];
        var runtime = new JsonObject
        {
            ["runtimeOptions"] = new JsonObject
            {
                ["framework"] = new JsonObject { ["name"] = "Microsoft.NETCore.App", ["version"] = "10.0.14" },
            },
        };
        foreach (var relative in selected.Concat(["shared/Microsoft.AspNetCore.App/10.0.14/aspnet.dll", "host/fxr/11.0.0/hostfxr.dll", "sdk/11.0.100/dotnet.dll"]))
        {
            var path = files.Path.Combine(installation, relative.Replace('/', files.Path.DirectorySeparatorChar));
            files.Directory.CreateDirectory(files.Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
            await files.File.WriteAllTextAsync(path, relative.EndsWith(".json", StringComparison.Ordinal) ? runtime.ToJsonString() : relative, token);
        }
        await using var owner = new PrivateSdkCopy(files);
        await owner.PrepareAsync(installation, "10.0.403", token);
        await Assert.That(files.Directory.EnumerateFiles(owner.Root, "*", SearchOption.AllDirectories)
            .Select(path => files.Path.GetRelativePath(owner.Root, path).Replace('\\', '/')).Order(StringComparer.Ordinal))
            .IsEquivalentTo(selected.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
        await Assert.That(files.Directory.Exists(files.Path.Combine(owner.Root, "shared", "Microsoft.AspNetCore.App"))).IsFalse();
        foreach (var relative in selected)
        {
            await Assert.That(await files.File.ReadAllBytesAsync(files.Path.Combine(owner.Root, relative), token))
                .IsEquivalentTo(await files.File.ReadAllBytesAsync(files.Path.Combine(installation, relative), token), CollectionOrdering.Matching);
            await Assert.That(files.FileInfo.New(files.Path.Combine(owner.Root, relative)).LinkTarget).IsNull();
        }
        await owner.DisposeAsync();
        await Assert.That(files.Directory.Exists(owner.Root)).IsFalse();
        await Assert.That(files.File.Exists(files.Path.Combine(installation, selected[0]))).IsTrue();
    }
}
