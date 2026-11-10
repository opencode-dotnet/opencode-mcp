using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Packaging;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using OpenCode.Mcp.Tools.Tests.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Packaging;

public sealed class McpPackageFeedTests
{
    [Test]
    public async Task ValidateAsync_Should_Accept_A_Complete_Coherent_Closed_Feed(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var request = await builder.PrepareCompleteAsync(token);
        await builder.WriteFeedAsync(token);
        await Assert.That(await new McpPackageFeed(builder.Files).ValidateAsync(builder.Feed, request.Manifest, builder.Identity, token)).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(FileSetCases))]
    public async Task ValidateAsync_Should_Require_The_Exact_File_Set(string fault, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var request = await builder.PrepareCompleteAsync(token);
        await builder.WriteFeedAsync(token);
        var relative = fault switch
        {
            "root" => builder.PackageFileName,
            "child" => PackagingData.ChildFileName,
            "receipt" => "evidence/win-x64/receipt.json",
            "payload manifest" => "evidence/win-x64/payload-manifest.json",
            "extra evidence" => "evidence/win-x64/extra.txt",
            _ => "extra.txt",
        };
        var path = builder.Files.Path.Combine(builder.Feed, relative);
        if (fault is "extra" or "extra evidence")
        {
            await builder.Files.File.WriteAllTextAsync(path, "extra", token);
        }
        else
        {
            builder.Files.File.Delete(path);
        }
        await Assert.That(await new McpPackageFeed(builder.Files).ValidateAsync(builder.Feed, request.Manifest, builder.Identity, token))
            .IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
    }

    public static IEnumerable<object[]> FileSetCases() =>
    [
        ["root", PackagingData.MissingFeedFile + "OpenCodeDotNet.Mcp." + PackagingData.BuildVersion + ".nupkg"],
        ["child", PackagingData.MissingFeedFile + PackagingData.ChildFileName],
        ["receipt", PackagingData.MissingFeedFile + "evidence/win-x64/receipt.json"],
        ["payload manifest", PackagingData.MissingFeedFile + "evidence/win-x64/payload-manifest.json"],
        ["extra", PackagingData.UnexpectedFeedFile + "extra.txt"],
        ["extra evidence", PackagingData.UnexpectedFeedFile + "evidence/win-x64/extra.txt"],
    ];

    [Test]
    [Arguments("empty", "Unexpected feed directory: empty")]
    [Arguments("evidence/win-x64/empty", "Unexpected feed directory: evidence/win-x64/empty")]
    [Arguments("nonempty", PackagingData.UnexpectedFeedFile + "nonempty/extra.txt\nUnexpected feed directory: nonempty")]
    public async Task ValidateAsync_Should_Refuse_An_Extra_Directory(string relative, string diagnostic, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var request = await builder.PrepareCompleteAsync(token);
        await builder.WriteFeedAsync(token);
        builder.Files.Directory.CreateDirectory(builder.Files.Path.Combine(builder.Feed, relative));
        if (relative == "nonempty")
        {
            await builder.Files.File.WriteAllTextAsync(builder.Files.Path.Combine(builder.Feed, relative, "extra.txt"), "extra", token);
        }
        await Assert.That(await new McpPackageFeed(builder.Files).ValidateAsync(builder.Feed, request.Manifest, builder.Identity, token))
            .IsEquivalentTo(diagnostic.Split('\n'), CollectionOrdering.Matching);
    }

    [Test]
    public async Task ValidateAsync_Should_Normalize_Only_The_Platform_Directory_Separator(CancellationToken token)
    {
        var files = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Linux));
        var manifest = await new NativeManifestStore(files).ReadAsync(await new NativeManifestFixture().WriteAsync(files, token), token);
        var identity = new PackageIdentity("OpenCodeDotNet.Mcp", PackagingData.BuildVersion, "opencode-mcp", [.. manifest.Runtimes.Select(static runtime => runtime.Rid)]);
        var feed = files.Path.Combine(files.Path.GetTempPath(), "feed");
        files.Directory.CreateDirectory(feed);
        files.Directory.CreateDirectory(files.Path.Combine(feed, "evidence\\win-arm64"));
        await files.File.WriteAllTextAsync(files.Path.Combine(feed, "evidence\\win-x64\\receipt.json"), "extra", token);
        string[] expected = [PackagingData.MissingFeedFile + identity.Id + "." + identity.Version + ".nupkg",
            .. manifest.Runtimes.Select(runtime => PackagingData.MissingFeedFile + identity.Id + "." + runtime.Rid + "." + identity.Version + ".nupkg"),
            .. manifest.Runtimes.SelectMany(static runtime => new[] { PackagingData.MissingFeedFile + "evidence/" + runtime.Rid + "/receipt.json",
                PackagingData.MissingFeedFile + "evidence/" + runtime.Rid + "/payload-manifest.json" }),
            PackagingData.UnexpectedFeedFile + "evidence\\win-x64\\receipt.json",
            "Unexpected feed directory: evidence\\win-arm64"];
        await Assert.That(await new McpPackageFeed(files).ValidateAsync(feed, manifest, identity, token))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("SourceCommit", "Mixed receipt field: SourceCommit")]
    [Arguments("CommonInputsSha256", "Mixed receipt field: CommonInputsSha256")]
    [Arguments("SdkVersion", "Mixed receipt field: SdkVersion")]
    [Arguments("DependenciesSha256", "Mixed receipt field: DependenciesSha256")]
    [Arguments("payload", "win-x64: Payload manifest differs from receipt.")]
    public async Task ValidateAsync_Should_Require_Coherent_Evidence(string fault, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var request = await builder.PrepareCompleteAsync(token);
        await builder.WriteFeedAsync(token);
        var child = builder.Children.Single(static child => child.Receipt.Rid == "win-x64");
        var receipt = fault switch
        {
            "SourceCommit" => child.Receipt with { SourceCommit = "different" },
            "CommonInputsSha256" => child.Receipt with { CommonInputsSha256 = "different" },
            "SdkVersion" => child.Receipt with { SdkVersion = "different" },
            "DependenciesSha256" => child.Receipt with { DependenciesSha256 = "different" },
            _ => child.Receipt,
        };
        var store = new NativeEvidenceStore(builder.Files);
        await store.WriteAsync(builder.Files.Path.Combine(builder.Feed, "evidence", "win-x64", "receipt.json"), receipt, token);
        if (fault == "payload")
        {
            await store.WriteAsync(builder.Files.Path.Combine(builder.Feed, "evidence", "win-x64", "payload-manifest.json"), Array.Empty<PayloadFile>(), token);
        }
        await Assert.That(await new McpPackageFeed(builder.Files).ValidateAsync(builder.Feed, request.Manifest, builder.Identity, token))
            .IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ValidateAsync_Should_Refuse_Six_Equal_But_Wrong_Receipt_Versions(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var request = await builder.PrepareCompleteAsync(token);
        await builder.WriteFeedAsync(token);
        foreach (var child in builder.Children)
        {
            await new NativeEvidenceStore(builder.Files).WriteAsync(builder.Files.Path.Combine(builder.Feed, "evidence", child.Receipt.Rid, "receipt.json"), child.Receipt with { Version = "wrong" }, token);
        }
        await Assert.That(await new McpPackageFeed(builder.Files).ValidateAsync(builder.Feed, request.Manifest, builder.Identity, token))
            .IsEquivalentTo(request.Manifest.Runtimes.Select(runtime => runtime.Rid + ": Receipt version must be " + PackagingData.BuildVersion + "."), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("root", PackagingData.RootTypesDiagnostic)]
    [Arguments("child", PackagingData.ChildTypesDiagnostic)]
    [Arguments("bytes", PackagingData.ManifestsDifferDiagnostic)]
    [Arguments("payload", PackagingData.ChangedNoticesDiagnostic)]
    public async Task ValidateAsync_Should_Inspect_Packages_And_Compare_Manifest_Bytes(string fault, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var request = await builder.PrepareCompleteAsync(token);
        var child = builder.Children.Single(static child => child.Receipt.Rid == "win-x64");
        if (fault is "root" or "child")
        {
            var target = fault == "root" ? builder : child;
            target.Metadata.Descendants(target.Metadata.Name.Namespace + "packageType").First().SetAttributeValue("name", "wrong");
        }
        else if (fault == "bytes")
        {
            child.ManifestSize = 19999;
        }
        else
        {
            child.Payload["THIRD-PARTY-NOTICES.md"] = "NOTICES";
        }
        await builder.WriteFeedAsync(token);
        var name = fault == "root" ? builder.PackageFileName : child.PackageFileName;
        var expected = fault == "bytes" ? diagnostic : name + ": " + diagnostic;
        await Assert.That(await new McpPackageFeed(builder.Files).ValidateAsync(builder.Feed, request.Manifest, builder.Identity, token))
            .IsEquivalentTo([expected], CollectionOrdering.Matching);
    }
}
