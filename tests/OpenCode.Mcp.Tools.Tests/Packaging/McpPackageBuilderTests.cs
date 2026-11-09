using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Packaging;
using OpenCode.Mcp.Tools.Tests.Packaging.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Packaging;

public sealed class McpPackageBuilderTests
{
    [Test]
    public async Task PackChildAsync_Should_Not_Mistake_An_Output_Path_For_A_Warning(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.PrepareStagedAsync(token);
        builder.PackResult = new(0, "Created C:/warning/child.nupkg", "");
        string? path = null;
        await Assert.That(async () => { path = await new McpPackageBuilder(builder.Files, builder.Native.Runner, builder.Native.Modes).PackChildAsync(request, token); }).ThrowsNothing();
        await Assert.That(path).IsEqualTo(builder.Files.Path.Combine(request.Output, "packages", "OpenCodeDotNet.Mcp.win-x64.fixture-version.nupkg"));
    }

    [Test]
    public async Task PackChildAsync_Should_Refuse_A_Package_That_Fails_Inspection_Policy(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.PrepareStagedAsync(token);
        builder.Metadata.Element(builder.Metadata.Name.Namespace + "id")?.SetValue("wrong");
        await AssertRefusalAsync(builder, request, "Package ID must be OpenCodeDotNet.Mcp.win-x64.", token);
    }

    [Test]
    [Arguments("Rid", "Child packing requires a receipt for win-x64.")]
    [Arguments("PayloadDirectory", "win-x64: Missing staging evidence.")]
    [Arguments("SdkVersion", "Child packing requires the receipt's executed SDK.")]
    [Arguments("SourceCommit", "Source or common build inputs changed after verification.")]
    [Arguments("CommonInputsSha256", "Source or common build inputs changed after verification.")]
    [Arguments("rids", "ToolPackageRuntimeIdentifiers must match the native manifest in order.")]
    public async Task PackChildAsync_Should_Refuse_Identity_Drift_Before_Packing(string field, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.PrepareStagedAsync(token);
        var receipt = builder.Receipt;
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
    [Arguments("payload content", "win-x64: Changed payload file: required.dat")]
    [Arguments("archive", "win-x64: Missing archive.")]
    [Arguments("archive digest", "win-x64: Archive digest differs from receipt.")]
    [Arguments("archive content", "win-x64 archive: Missing payload file: required.dat")]
    public async Task PackChildAsync_Should_Refuse_Broken_Staging_Before_Packing(string fault, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.PrepareStagedAsync(token);
        await builder.BreakStagingAsync(request, fault, token);
        var start = builder.Native.Runner.Calls.Count;
        await AssertRefusalAsync(builder, request, diagnostic, token);
        await Assert.That(builder.Native.Runner.Calls.Skip(start)).IsEmpty();
    }

    [Test]
    [Arguments("exit", "Child pack exited 7:\npack failed\n")]
    [Arguments("warning", "Child pack warning: warning NU5100: fixture")]
    [Arguments("none", "Expected exactly one child package named OpenCodeDotNet.Mcp.win-x64.fixture-version.nupkg.")]
    [Arguments("two", "Expected exactly one child package named OpenCodeDotNet.Mcp.win-x64.fixture-version.nupkg.")]
    [Arguments("stale", "Expected exactly one child package named OpenCodeDotNet.Mcp.win-x64.fixture-version.nupkg.")]
    [Arguments("changed payload", "win-x64: Changed payload file: required.dat")]
    [Arguments("wrong name", "Expected exactly one child package named OpenCodeDotNet.Mcp.win-x64.fixture-version.nupkg.")]
    public async Task PackChildAsync_Should_Refuse_Failed_Or_Ineffective_Packs(string fault, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.PrepareStagedAsync(token);
        var packages = builder.Files.Path.Combine(request.Output, "packages");
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
            "warning" => new(0, "", "warning NU5100: fixture"),
            _ => builder.PackResult,
        };
        if (fault == "stale")
        {
            await builder.WriteAsync(token, builder.Files.Path.Combine(packages, "OpenCodeDotNet.Mcp.win-x64.fixture-version.nupkg"));
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
        };
        await AssertRefusalAsync(builder, request, diagnostic, token);
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
        await Assert.That(path).IsEqualTo(builder.Files.Path.Combine(request.Output, "packages", "OpenCodeDotNet.Mcp.win-x64.fixture-version.nupkg"));
        var package = await new McpPackageInspector(builder.Files).InspectAsync(path, token);
        await Assert.That(package.Payload).IsEquivalentTo(builder.Receipt.Payload, CollectionOrdering.Matching);
        await Assert.That(await new NativeReceiptValidator(builder.Files, builder.Native.Modes).ValidateArtifactsAsync(builder.Receipt, request.Output, token)).IsEmpty();
    }

    private static async Task AssertRefusalAsync(PackageArchiveBuilder builder, NativeBuildRequest request, string diagnostic, CancellationToken token) =>
        await Assert.That(async () => { await new McpPackageBuilder(builder.Files, builder.Native.Runner, builder.Native.Modes).PackChildAsync(request, token); })
            .ThrowsExactly<InvalidOperationException>().WithMessage(diagnostic, StringComparison.Ordinal);
}
