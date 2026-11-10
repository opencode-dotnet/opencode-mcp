using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Packaging;
using OpenCode.Mcp.Tools.Tests.Support;
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
    [Arguments("root")]
    [Arguments("child")]
    [Arguments("receipt")]
    [Arguments("payload manifest")]
    [Arguments("extra")]
    public async Task ValidateAsync_Should_Require_The_Exact_File_Set(string fault, CancellationToken token)
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
            _ => "extra.txt",
        };
        var path = builder.Files.Path.Combine(builder.Feed, relative);
        if (fault == "extra")
        {
            await builder.Files.File.WriteAllTextAsync(path, "extra", token);
        }
        else
        {
            builder.Files.File.Delete(path);
        }
        await Assert.That(await new McpPackageFeed(builder.Files).ValidateAsync(builder.Feed, request.Manifest, builder.Identity, token))
            .IsEquivalentTo([(fault == "extra" ? PackagingData.UnexpectedFeedFile : PackagingData.MissingFeedFile) + relative], CollectionOrdering.Matching);
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
