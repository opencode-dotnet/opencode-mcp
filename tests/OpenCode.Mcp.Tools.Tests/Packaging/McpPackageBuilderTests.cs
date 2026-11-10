using System.IO.Abstractions;
using NSubstitute;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Packaging;
using OpenCode.Mcp.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Packaging;

public sealed class McpPackageBuilderTests
{
    private const string PackFailureDiagnostic = "Child pack exited 7:\npack failed\n";
    private const string ChangedPayloadDiagnostic = "win-x64: Changed payload file: required.dat";

    [Test]
    public async Task PackChildAsync_Should_Preserve_A_Foreign_Packages_Directory(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.Native.PrepareAsync(token);
        var sentinel = builder.Files.Path.Combine(request.Output, "packages", "foreign.txt");
        builder.Files.Directory.CreateDirectory(builder.Files.Path.GetDirectoryName(sentinel) ?? throw new InvalidOperationException());
        await builder.Files.File.WriteAllTextAsync(sentinel, "foreign content", token);
        var start = builder.Native.Runner.Calls.Count;
        await Assert.That(async () => { await new McpPackageBuilder(builder.Files, builder.Native.Runner, builder.Native.Modes).PackChildAsync(request, token); })
            .ThrowsExactly<InvalidOperationException>().WithMessage("Output directory is not owned by the native tool: " + request.Output, StringComparison.Ordinal);
        await Assert.That(await builder.Files.File.ReadAllTextAsync(sentinel, token)).IsEqualTo("foreign content");
        await Assert.That(builder.Native.Runner.Calls.Skip(start)).IsEmpty();
    }

    [Test]
    public async Task PackChildAsync_Should_Not_Mistake_An_Output_Path_For_A_Warning(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.PrepareStagedAsync(token);
        builder.PackResult = new(0, "Created C:/warning/child.nupkg", "");
        string? path = null;
        await Assert.That(async () => { path = await new McpPackageBuilder(builder.Files, builder.Native.Runner, builder.Native.Modes).PackChildAsync(request, token); }).ThrowsNothing();
        await Assert.That(path).IsEqualTo(builder.Files.Path.Combine(request.Output, "packages", PackagingData.ChildFileName));
    }

    [Test]
    public async Task PackChildAsync_Should_Refuse_A_Package_That_Fails_Inspection_Policy(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.PrepareStagedAsync(token);
        builder.Metadata.Element(builder.Metadata.Name.Namespace + "id")?.SetValue("wrong");
        await AssertRefusalAsync(builder, request, PackagingData.PackageIdDiagnostic, token);
    }

    [Test]
    [Arguments("Rid", "Child packing requires a receipt for win-x64.")]
    [Arguments("PayloadDirectory", "win-x64: Missing staging evidence.")]
    [Arguments("SdkVersion", "Child packing requires the receipt's executed SDK.")]
    [Arguments("SourceCommit", PackagingData.InputDriftDiagnostic)]
    [Arguments("CommonInputsSha256", PackagingData.InputDriftDiagnostic)]
    [Arguments("rids", PackagingData.ToolRidsDiagnostic)]
    public async Task PackChildAsync_Should_Refuse_Identity_Drift_Before_Packing(string field, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.PrepareStagedAsync(token);
        var receipt = builder.Receipt;
        await builder.WriteAsync(token, builder.Files.Path.Combine(request.Output, "packages", "stale.nupkg"));
        receipt = field switch
        {
            "Rid" => receipt with { Rid = "win-arm64" },
            "PayloadDirectory" => receipt with { PayloadDirectory = null },
            "SdkVersion" => receipt with { SdkVersion = "different-sdk" },
            "SourceCommit" => receipt with { SourceCommit = "different-commit" },
            "CommonInputsSha256" => receipt with { CommonInputsSha256 = "different-inputs" },
            _ => receipt,
        };
        if (field == "rids")
        {
            builder.Identity = builder.Identity with { RuntimeIdentifiers = [.. builder.Identity.RuntimeIdentifiers.Reverse()] };
        }
        await new NativeEvidenceStore(builder.Files).WriteAsync(builder.Files.Path.Combine(request.Output, "receipt.json"), receipt, token);
        var start = builder.Native.Runner.Calls.Count;
        await AssertRefusalAsync(builder, request, diagnostic, token);
        await Assert.That(builder.Native.Runner.Calls.Skip(start).Where(static call => call.Arguments[0] == "pack")).IsEmpty();
    }

    [Test]
    [Arguments("manifest", "win-x64: Missing payload manifest.")]
    [Arguments("manifest content", "win-x64: Payload manifest differs from receipt.")]
    [Arguments("payload", "win-x64: Missing payload directory.")]
    [Arguments("payload content", ChangedPayloadDiagnostic)]
    [Arguments("archive", "win-x64: Missing archive.")]
    [Arguments("archive digest", "win-x64: Archive digest differs from receipt.")]
    [Arguments("archive content", "win-x64 archive: Missing payload file: required.dat")]
    public async Task PackChildAsync_Should_Refuse_Broken_Staging_Before_Packing(string fault, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.PrepareStagedAsync(token);
        await builder.BreakStagingAsync(request, fault, token);
        await builder.WriteAsync(token, builder.Files.Path.Combine(request.Output, "packages", "stale.nupkg"));
        var start = builder.Native.Runner.Calls.Count;
        await AssertRefusalAsync(builder, request, diagnostic, token);
        await Assert.That(builder.Native.Runner.Calls.Skip(start)).IsEmpty();
    }

    [Test]
    [Arguments("exit", PackFailureDiagnostic)]
    [Arguments("warning", PackagingData.PackWarningDiagnostic)]
    [Arguments("none", "count")]
    [Arguments("two", "count")]
    [Arguments("extra file", "count")]
    [Arguments("empty directory", "count")]
    [Arguments("stale", "count")]
    [Arguments("changed payload", ChangedPayloadDiagnostic)]
    [Arguments("wrong name", "count")]
    [Arguments("inspection", PackagingData.MissingManifestDiagnostic)]
    public async Task PackChildAsync_Should_Refuse_Failed_Or_Ineffective_Packs(string fault, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.PrepareStagedAsync(token);
        var packages = builder.Files.Path.Combine(request.Output, "packages");
        if (fault == "inspection")
        {
            builder.Omitted.Add(".mcp/server.json");
        }
        if (fault is "none" or "stale")
        {
            builder.PackageCount = 0;
        }
        else if (fault == "two")
        {
            builder.PackageCount = 2;
        }
        builder.PackResult = fault switch
        {
            "exit" => new(7, "pack failed", ""),
            "warning" => new(0, PackagingData.PackWarning, ""),
            _ => builder.PackResult,
        };
        if (fault == "stale")
        {
            await builder.WriteAsync(token, builder.Files.Path.Combine(packages, PackagingData.ChildFileName));
        }
        builder.AfterPack = async cancellation =>
        {
            if (fault == "changed payload")
            {
                await builder.Files.File.WriteAllTextAsync(builder.Files.Path.Combine(request.Output, "payload", "required.dat"), "REQUIRED ASSET", cancellation);
            }
            else if (fault == "wrong name")
            {
                builder.Files.File.Move(builder.Files.Directory.EnumerateFiles(packages).Single(), builder.Files.Path.Combine(packages, "wrong.nupkg"));
            }
            else if (fault == "extra file")
            {
                await builder.Files.File.WriteAllTextAsync(builder.Files.Path.Combine(packages, "extra.txt"), "extra", cancellation);
            }
            else if (fault == "empty directory")
            {
                builder.Files.Directory.CreateDirectory(builder.Files.Path.Combine(packages, "empty"));
            }
        };
        await AssertRefusalAsync(builder, request, diagnostic == "count" ? PackagingData.PackageCountDiagnostic : diagnostic, token);
    }

    [Test]
    public async Task PackChildAsync_Should_Keep_The_Refusal_Before_A_Cleanup_Failure(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.PrepareStagedAsync(token);
        builder.PackResult = new(7, "pack failed", "");
        var packages = builder.Files.Path.Combine(request.Output, "packages");
        var cleanup = new IOException("fixture cleanup failure");
        var directory = Substitute.For<IDirectory>();
        directory.Exists(Arg.Any<string>()).Returns(call => builder.Files.Directory.Exists(call.Arg<string>()));
        directory.CreateDirectory(Arg.Any<string>()).Returns(call => builder.Files.Directory.CreateDirectory(call.Arg<string>()));
        directory.EnumerateFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(call => builder.Files.Directory.EnumerateFiles(call.ArgAt<string>(0), call.ArgAt<string>(1), call.Arg<SearchOption>()));
        directory.When(value => value.Delete(packages, true)).Do(_ => throw cleanup);
        var files = Substitute.For<IFileSystem>();
        files.File.Returns(builder.Files.File);
        files.Path.Returns(builder.Files.Path);
        files.Directory.Returns(directory);
        var failure = await Assert.That(async () => { await new McpPackageBuilder(files, builder.Native.Runner, builder.Native.Modes).PackChildAsync(request, token); })
            .ThrowsExactly<AggregateException>() ?? throw new InvalidOperationException("Expected ordered failures.");
        await Assert.That(failure.InnerExceptions.Select(static error => error.Message)).IsEquivalentTo([PackFailureDiagnostic, cleanup.Message], CollectionOrdering.Matching);
        await Assert.That(failure.InnerExceptions[1]).IsSameReferenceAs(cleanup);
    }

    [Test]
    public async Task PackChildAsync_Should_Use_Only_The_Approved_Ordered_Requests_And_Staged_Content(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.PrepareStagedAsync(token);
        var start = builder.Native.Runner.Calls.Count;
        var path = await new McpPackageBuilder(builder.Files, builder.Native.Runner, builder.Native.Modes).PackChildAsync(request, token);
        await Assert.That(builder.Files.File.Exists(path)).IsTrue();
        var calls = builder.Native.Runner.Calls.Skip(start).ToArray();
        await Assert.That(calls.Select(static call => call.FileName)).IsEquivalentTo(["dotnet", "git", "git", "git", "dotnet", "dotnet"], CollectionOrdering.Matching);
        await Assert.That(calls[0].Arguments).IsEquivalentTo(["--version"], CollectionOrdering.Matching);
        await Assert.That(calls[1].Arguments).IsEquivalentTo(["rev-parse", "HEAD"], CollectionOrdering.Matching);
        await Assert.That(calls[2].Arguments).IsEquivalentTo(["status", "--porcelain=v1", "--untracked-files=all"], CollectionOrdering.Matching);
        await Assert.That(calls[3].Arguments).IsEquivalentTo(["ls-files", "--cached", "--others", "--exclude-standard", "-z"], CollectionOrdering.Matching);
        await Assert.That(calls[4].Arguments).IsEquivalentTo(["msbuild", "src/OpenCode.Mcp/OpenCode.Mcp.csproj", "-p:Configuration=Release", "-nodeReuse:false", "-p:UseSharedCompilation=false",
            "-getProperty:PackageId,ToolCommandName,Version,ToolPackageRuntimeIdentifiers"], CollectionOrdering.Matching);
        await Assert.That(calls[5].Arguments).IsEquivalentTo(["pack", "src/OpenCode.Mcp/OpenCode.Mcp.csproj", "-c", "Release", "-r", "win-x64", "--no-build", "--no-restore", "-p:IsPublishable=false",
            "-p:PublishDir=" + builder.Files.Path.Combine(request.Output, "payload") + builder.Files.Path.DirectorySeparatorChar, "-p:ToolEntryPoint=opencode-mcp.exe", "-o", builder.Files.Path.Combine(request.Output, "packages"),
            "-nodeReuse:false", "-p:UseSharedCompilation=false"], CollectionOrdering.Matching);
        await Assert.That(calls.Select(static call => call.WorkingDirectory).Distinct(StringComparer.Ordinal)).IsEquivalentTo([request.Root], CollectionOrdering.Matching);
        await Assert.That(calls.Select(static call => call.Timeout).Distinct()).IsEquivalentTo([TimeSpan.FromMinutes(35)], CollectionOrdering.Matching);
        await Assert.That(path).IsEqualTo(builder.Files.Path.Combine(request.Output, "packages", PackagingData.ChildFileName));
        var package = await new McpPackageInspector(builder.Files).InspectAsync(path, token);
        await Assert.That(package.Payload).IsEquivalentTo(builder.Receipt.Payload, CollectionOrdering.Matching);
        await Assert.That(await new NativeReceiptValidator(builder.Files, builder.Native.Modes).ValidateArtifactsAsync(builder.Receipt, request.Output, token)).IsEmpty();
    }

    private static async Task AssertRefusalAsync(PackageArchiveBuilder builder, NativeBuildRequest request, string diagnostic, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        await Assert.That(async () => { await new McpPackageBuilder(builder.Files, builder.Native.Runner, builder.Native.Modes).PackChildAsync(request, token); })
            .ThrowsExactly<InvalidOperationException>().WithMessage(diagnostic, StringComparison.Ordinal);
        await Assert.That(builder.Files.Directory.Exists(builder.Files.Path.Combine(request.Output, "packages"))).IsFalse();
    }
}
