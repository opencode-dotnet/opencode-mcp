using System.IO.Abstractions;
using System.Text.Json.Nodes;
using NSubstitute;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Support;

public sealed class PrivateSdkCopyTests
{
    [Test]
    public async Task DisposeAsync_Should_Join_The_Copy_Before_Deleting_The_Root(CancellationToken token)
    {
        var files = new MockFileSystem();
        var installation = await SeedInstallationAsync(files, token);
        var decorated = GateFirstRead(files, out var entered, out var release);
        await using var owner = new PrivateSdkCopy(decorated);
        var copy = owner.PrepareAsync(installation, "10.0.403", token);
        await entered.Task.WaitAsync(token);
        var disposal = owner.DisposeAsync().AsTask();
        release.SetResult();
        await disposal.WaitAsync(token);
        await copy.WaitAsync(token); // A non-joining disposal cannot win a race against the final assertion.
        await Assert.That(files.Directory.Exists(owner.Root)).IsFalse();
    }

    [Test]
    public async Task PrepareAsync_Should_Cancel_Only_The_Waiter_Not_The_Shared_Copy(CancellationToken token)
    {
        var files = new MockFileSystem();
        var installation = await SeedInstallationAsync(files, token);
        var decorated = GateFirstRead(files, out var entered, out var release);
        await using var owner = new PrivateSdkCopy(decorated);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var first = owner.PrepareAsync(installation, "10.0.403", cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(token);
            await cancellation.CancelAsync();
            await Assert.That(() => first.WaitAsync(token)).Throws<OperationCanceledException>();
        }
        finally
        {
            release.SetResult();
        }
        await owner.PrepareAsync(installation, "10.0.403", token);
        await Assert.That(files.File.Exists(files.Path.Combine(owner.Root, "sdk", "10.0.403", "dotnet.runtimeconfig.json"))).IsTrue();
    }

    private static IFileSystem GateFirstRead(MockFileSystem files, out TaskCompletionSource entered, out TaskCompletionSource release)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        entered = started;
        release = finish;
        var file = Substitute.For<IFile>();
        file.ReadAllTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.SetResult();
            await finish.Task.WaitAsync(call.Arg<CancellationToken>());
            return await files.File.ReadAllTextAsync(call.Arg<string>(), call.Arg<CancellationToken>());
        });
        file.When(value => value.Copy(Arg.Any<string>(), Arg.Any<string>())).Do(call => files.File.Copy(call.ArgAt<string>(0), call.ArgAt<string>(1)));
        if (!OperatingSystem.IsWindows())
        {
            file.GetUnixFileMode(Arg.Any<string>()).Returns(call => !OperatingSystem.IsWindows()
                ? files.File.GetUnixFileMode(call.Arg<string>()) : throw new PlatformNotSupportedException());
            file.When(value =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    value.SetUnixFileMode(Arg.Any<string>(), Arg.Any<UnixFileMode>());
                }
            }).Do(call =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    files.File.SetUnixFileMode(call.Arg<string>(), call.Arg<UnixFileMode>());
                }
            });
        }
        var decorated = Substitute.For<IFileSystem>();
        decorated.File.Returns(file);
        decorated.Directory.Returns(files.Directory);
        decorated.Path.Returns(files.Path);
        decorated.FileInfo.Returns(files.FileInfo);
        return decorated;
    }

    private static async Task<string> SeedInstallationAsync(MockFileSystem files, CancellationToken token)
    {
        var installation = files.Path.GetFullPath("installed-sdk");
        foreach (var relative in new[] { OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet", "sdk/10.0.403/dotnet.runtimeconfig.json",
            "shared/Microsoft.NETCore.App/10.0.14/coreclr.dll", "host/fxr/10.0.14/hostfxr.dll" })
        {
            var path = files.Path.Combine(installation, relative);
            files.Directory.CreateDirectory(files.Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
            var runtime = new JsonObject
            {
                ["runtimeOptions"] = new JsonObject
                {
                    ["framework"] = new JsonObject { ["name"] = "Microsoft.NETCore.App", ["version"] = "10.0.14" },
                },
            };
            await files.File.WriteAllTextAsync(path, relative.EndsWith(".json", StringComparison.Ordinal) ? runtime.ToJsonString() : relative, token);
        }
        return installation;
    }

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
