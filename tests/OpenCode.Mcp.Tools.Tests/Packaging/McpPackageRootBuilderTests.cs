using System.IO.Abstractions;
using NSubstitute;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Packaging;
using OpenCode.Mcp.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Packaging;

public sealed class McpPackageRootBuilderTests
{
    [Test]
    public async Task PackRootAsync_Should_Join_The_Evidence_And_Use_Only_The_Ordered_Root_Requests(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var request = await builder.PrepareCompleteAsync(token);
        var start = builder.Native.Runner.Calls.Count;
        var result = await new McpPackageBuilder(builder.Files, builder.Native.Runner, builder.Native.Modes).PackRootAsync(request, builder.Feed, TextWriter.Null, token);
        await Assert.That(result).IsEquivalentTo(new[] { builder.PackageFileName }
            .Concat(request.Manifest.Runtimes.Select(runtime => builder.Identity.Id + "." + runtime.Rid + "." + PackagingData.BuildVersion + ".nupkg"))
            .Select(name => builder.Files.Path.Combine(builder.Feed, name)), CollectionOrdering.Matching);
        var calls = builder.Native.Runner.Calls.Skip(start).ToArray();
        await Assert.That(calls.Select(static call => call.FileName)).IsEquivalentTo(["dotnet", "git", "git", "git", "dotnet", "dotnet"], CollectionOrdering.Matching);
        await Assert.That(calls[0].Arguments).IsEquivalentTo(["--version"], CollectionOrdering.Matching);
        await Assert.That(calls[1].Arguments).IsEquivalentTo(["rev-parse", "HEAD"], CollectionOrdering.Matching);
        await Assert.That(calls[2].Arguments).IsEquivalentTo(["status", "--porcelain=v1", "--untracked-files=all"], CollectionOrdering.Matching);
        await Assert.That(calls[3].Arguments).IsEquivalentTo(["ls-files", "--cached", "--others", "--exclude-standard", "-z"], CollectionOrdering.Matching);
        await Assert.That(calls[4].Arguments).IsEquivalentTo(["msbuild", "src/OpenCode.Mcp/OpenCode.Mcp.csproj", "-p:Configuration=Release", "-nodeReuse:false", "-p:UseSharedCompilation=false",
            "-getProperty:PackageId,ToolCommandName,Version,ToolPackageRuntimeIdentifiers"], CollectionOrdering.Matching);
        await Assert.That(calls[5].Arguments).IsEquivalentTo(["pack", "src/OpenCode.Mcp/OpenCode.Mcp.csproj", "-c", "Release", "-o", builder.Feed, "-nodeReuse:false", "-p:UseSharedCompilation=false"], CollectionOrdering.Matching);
        await Assert.That(calls.Select(static call => call.WorkingDirectory).Distinct(StringComparer.Ordinal)).IsEquivalentTo([request.Root], CollectionOrdering.Matching);
        await Assert.That(calls.Select(static call => call.Timeout).Distinct()).IsEquivalentTo([TimeSpan.FromMinutes(35)], CollectionOrdering.Matching);
        await Assert.That(await new McpPackageFeed(builder.Files).ValidateAsync(builder.Feed, request.Manifest, builder.Identity, token)).IsEmpty();
        foreach (var child in builder.Children)
        {
            foreach (var name in new[] { "receipt.json", "payload-manifest.json" })
            {
                await Assert.That(await builder.Files.File.ReadAllBytesAsync(builder.Files.Path.Combine(builder.Feed, "evidence", child.Receipt.Rid, name), token))
                    .IsEquivalentTo(await builder.Files.File.ReadAllBytesAsync(builder.Files.Path.Combine(request.Output, child.Receipt.Rid, name), token), CollectionOrdering.Matching);
            }
        }
    }

    [Test]
    [Arguments("native", "Missing receipt: win-x64")]
    [Arguments("sdk", "Root packing requires the receipt's executed SDK.")]
    [Arguments("source", PackagingData.InputDriftDiagnostic)]
    [Arguments("inputs", PackagingData.InputDriftDiagnostic)]
    [Arguments("rids", PackagingData.ToolRidsDiagnostic)]
    [Arguments("child missing", "count")]
    [Arguments("child extra file", "count")]
    [Arguments("child empty directory", "count")]
    [Arguments("child policy", PackagingData.ChildTypesDiagnostic)]
    public async Task PackRootAsync_Should_Not_Pack_After_A_Refused_Precondition(string fault, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var request = await builder.PrepareCompleteAsync(token);
        var child = builder.Children.Single(static child => child.Receipt.Rid == "win-x64");
        var packages = builder.Files.Path.Combine(request.Output, "win-x64", "packages");
        if (fault == "native")
        {
            builder.Files.File.Delete(builder.Files.Path.Combine(request.Output, "win-x64", "receipt.json"));
        }
        else if (fault is "sdk" or "source" or "inputs")
        {
            foreach (var entry in builder.Children)
            {
                var receipt = fault switch
                {
                    "sdk" => entry.Receipt with { SdkVersion = "different" },
                    "source" => entry.Receipt with { SourceCommit = "different" },
                    _ => entry.Receipt with { CommonInputsSha256 = "different" },
                };
                await new NativeEvidenceStore(builder.Files).WriteAsync(builder.Files.Path.Combine(request.Output, entry.Receipt.Rid, "receipt.json"), receipt, token);
            }
        }
        else if (fault == "rids")
        {
            builder.Identity = builder.Identity with { RuntimeIdentifiers = [.. builder.Identity.RuntimeIdentifiers.Reverse()] };
        }
        else if (fault == "child missing")
        {
            builder.Files.File.Delete(builder.Files.Path.Combine(packages, child.PackageFileName));
        }
        else if (fault == "child extra file")
        {
            await builder.Files.File.WriteAllTextAsync(builder.Files.Path.Combine(packages, "extra.txt"), "extra", token);
        }
        else if (fault == "child empty directory")
        {
            builder.Files.Directory.CreateDirectory(builder.Files.Path.Combine(packages, "empty"));
        }
        else
        {
            child.Metadata.Descendants(child.Metadata.Name.Namespace + "packageType").Single().SetAttributeValue("name", "wrong");
            await child.WriteAsync(token, builder.Files.Path.Combine(packages, child.PackageFileName));
        }
        if (diagnostic == "count")
        {
            diagnostic = "win-x64: Expected exactly one child package named " + PackagingData.ChildFileName + ".";
        }
        else if (fault == "child policy")
        {
            diagnostic = "win-x64: " + diagnostic;
        }
        var start = builder.Native.Runner.Calls.Count;
        await AssertRefusalAsync(builder, request, diagnostic, token);
        await Assert.That(builder.Native.Runner.Calls.Skip(start).Where(static call => call.Arguments[0] == "pack")).IsEmpty();
    }

    [Test]
    [Arguments("exit")]
    [Arguments("warning")]
    [Arguments("missing")]
    [Arguments("extra")]
    [Arguments("invalid")]
    public async Task PackRootAsync_Should_Delete_The_Feed_After_Each_Post_Pack_Refusal(string fault, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var request = await builder.PrepareCompleteAsync(token);
        var diagnostic = "";
        switch (fault)
        {
            case "exit": builder.PackResult = new(7, "pack failed", ""); diagnostic = PackagingData.RootPackFailureDiagnostic; break;
            case "warning": builder.PackResult = new(0, PackagingData.PackWarning, ""); diagnostic = "Root pack warning: " + PackagingData.PackWarning; break;
            case "missing": builder.PackageCount = 0; diagnostic = PackagingData.MissingFeedFile + builder.PackageFileName; break;
            case "extra": builder.PackageCount = 2; diagnostic = PackagingData.UnexpectedFeedFile + "extra.nupkg"; break;
            case "invalid": builder.Command.SetAttributeValue("Runner", "dotnet"); diagnostic = builder.PackageFileName + ": " + PackagingData.RootRunnerDiagnostic; break;
        }
        await AssertRefusalAsync(builder, request, diagnostic, token);
    }

    [Test]
    public async Task PackRootAsync_Should_Preserve_A_Nonempty_Feed(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var request = await builder.PrepareCompleteAsync(token);
        builder.Files.Directory.CreateDirectory(builder.Feed);
        var sentinel = builder.Files.Path.Combine(builder.Feed, "foreign.txt");
        await builder.Files.File.WriteAllTextAsync(sentinel, "foreign", token);
        var start = builder.Native.Runner.Calls.Count;
        await Assert.That(async () => { await new McpPackageBuilder(builder.Files, builder.Native.Runner, builder.Native.Modes).PackRootAsync(request, builder.Feed, TextWriter.Null, token); })
            .ThrowsExactly<InvalidOperationException>().WithMessage("Feed directory must be absent or empty: " + builder.Feed, StringComparison.Ordinal);
        await Assert.That(await builder.Files.File.ReadAllTextAsync(sentinel, token)).IsEqualTo("foreign");
        await Assert.That(builder.Native.Runner.Calls.Skip(start).Where(static call => call.Arguments[0] == "pack")).IsEmpty();
    }

    [Test]
    public async Task PackRootAsync_Should_Keep_The_Original_Refusal_Before_A_Cleanup_Failure(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var request = await builder.PrepareCompleteAsync(token);
        builder.PackResult = new(7, "pack failed", "");
        var directory = Substitute.For<IDirectory>();
        directory.Exists(Arg.Any<string>()).Returns(call => builder.Files.Directory.Exists(call.Arg<string>()));
        directory.CreateDirectory(Arg.Any<string>()).Returns(call => builder.Files.Directory.CreateDirectory(call.Arg<string>()));
        directory.EnumerateFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(call => builder.Files.Directory.EnumerateFiles(call.ArgAt<string>(0), call.ArgAt<string>(1), call.Arg<SearchOption>()));
        directory.EnumerateFileSystemEntries(Arg.Any<string>()).Returns(call => builder.Files.Directory.EnumerateFileSystemEntries(call.Arg<string>()));
        var cleanup = new IOException("fixture cleanup failure");
        directory.When(value => value.Delete(builder.Feed, true)).Do(_ => throw cleanup);
        var files = Substitute.For<IFileSystem>();
        files.Directory.Returns(directory);
        files.File.Returns(builder.Files.File);
        files.Path.Returns(builder.Files.Path);
        var failure = await Assert.That(async () => { await new McpPackageBuilder(files, builder.Native.Runner, builder.Native.Modes).PackRootAsync(request, builder.Feed, TextWriter.Null, token); })
            .ThrowsExactly<AggregateException>() ?? throw new InvalidOperationException("Expected ordered failures.");
        await Assert.That(failure.InnerExceptions.Select(static error => error.Message)).IsEquivalentTo([PackagingData.RootPackFailureDiagnostic, cleanup.Message], CollectionOrdering.Matching);
        await Assert.That(failure.InnerExceptions[1]).IsSameReferenceAs(cleanup);
    }

    private static async Task AssertRefusalAsync(PackageArchiveBuilder builder, NativeBuildRequest request, string diagnostic, CancellationToken token)
    {
        await Assert.That(async () => { await new McpPackageBuilder(builder.Files, builder.Native.Runner, builder.Native.Modes).PackRootAsync(request, builder.Feed, TextWriter.Null, token); })
            .ThrowsExactly<InvalidOperationException>().WithMessage(diagnostic, StringComparison.Ordinal);
        await Assert.That(builder.Files.Directory.Exists(builder.Feed)).IsFalse();
    }
}
