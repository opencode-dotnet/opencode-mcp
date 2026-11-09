using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeReceiptPolicyTests
{
    [Test]
    [Timeout(30_000)]
    [Arguments("win-x64", "missing")]
    [Arguments("osx-x64", "missing")]
    [Arguments("win-x64", "kind")]
    [Arguments("osx-x64", "kind")]
    [Arguments("win-x64", "glibc")]
    [Arguments("osx-x64", "glibc")]
    public async Task Diagnostics_Should_Require_Matching_Host_Proof_And_No_Glibc_Floor(string rid, string defect, CancellationToken token)
    {
        var manifest = await ManifestAsync(token);
        var receipts = manifest.Runtimes.Select(ReceiptFixture.Create).ToArray();
        var index = Array.FindIndex(receipts, receipt => receipt.Rid == rid);
        var receipt = receipts[index];
        var proof = receipt.RuntimeIndependence ?? throw new InvalidOperationException();
        receipts[index] = defect switch
        {
            "missing" => receipt with { RuntimeIndependence = null },
            "kind" => receipt with { RuntimeIndependence = proof with { Kind = "linux-clean-container" } },
            _ => receipt with { GlibcFloor = new NativeGlibcFloor("2.35", "GNU objdump fixture") },
        };
        var diagnostic = defect switch
        {
            "missing" => "Missing matching-host runtime-independence proof: " + rid,
            "kind" => rid + ": RuntimeIndependence.Kind must be matching-host-inspection.",
            _ => rid + ": GlibcFloor must be null.",
        };
        await Assert.That(NativeReceiptPolicy.Diagnostics(manifest, receipts)).IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("kind", "linux-x64: RuntimeIndependence.Kind must be linux-clean-container.")]
    [Arguments("tests", "linux-x64: RuntimeIndependence.Tests must be successful and nonzero.")]
    [Arguments("zero", "linux-x64: RuntimeIndependence.Tests must be successful and nonzero.")]
    [Arguments("image", "linux-x64: RuntimeIndependence.ImageReference differs from manifest.")]
    [Arguments("id", "linux-x64: RuntimeIndependence.ImageId is required.")]
    [Arguments("dotnet", "linux-x64: RuntimeIndependence.DotnetAbsent must be true.")]
    [Arguments("glibc", "linux-x64: GlibcFloor is required.")]
    [Arguments("glibc-version", "linux-x64: GlibcFloor requires a numeric Version and ToolVersion.")]
    [Arguments("glibc-tool", "linux-x64: GlibcFloor requires a numeric Version and ToolVersion.")]
    [Arguments("inspection", "win-x64: RuntimeIndependence.Inspection must contain the native AOT export.")]
    [Arguments("os", "linux-x64: Native execution needs a compatible linux-x64 host; observed win-x64 (process x64).")]
    [Arguments("architecture", "linux-x64: Native execution needs a compatible linux-x64 host; observed linux-arm64 (process x64).")]
    [Arguments("process", "linux-x64: Native execution needs a compatible linux-x64 host; observed linux-x64 (process arm64).")]
    public async Task Diagnostics_Should_Require_Complete_Platform_Proofs(string defect, string diagnostic, CancellationToken token)
    {
        var manifest = await ManifestAsync(token);
        var receipts = manifest.Runtimes.Select(ReceiptFixture.Create).ToArray();
        var receipt = receipts[2];
        var proof = receipt.RuntimeIndependence ?? throw new InvalidOperationException();
        receipts[2] = defect switch
        {
            "kind" => receipt with { RuntimeIndependence = proof with { Kind = "matching-host-inspection" } },
            "tests" => receipt with { RuntimeIndependence = proof with { Tests = null } },
            "zero" => receipt with { RuntimeIndependence = proof with { Tests = new NativeTestRun(0, 0, 0, 0) } },
            "image" => receipt with { RuntimeIndependence = proof with { ImageReference = "wrong" } },
            "id" => receipt with { RuntimeIndependence = proof with { ImageId = null } },
            "dotnet" => receipt with { RuntimeIndependence = proof with { DotnetAbsent = false } },
            "glibc" => receipt with { GlibcFloor = null },
            "glibc-version" => receipt with { GlibcFloor = new NativeGlibcFloor("invalid", "GNU objdump fixture") },
            "glibc-tool" => receipt with { GlibcFloor = new NativeGlibcFloor("2.35", "") },
            "os" => receipt with { Os = "win" },
            "architecture" => receipt with { OsArchitecture = "arm64" },
            "process" => receipt with { ProcessArchitecture = "arm64" },
            _ => receipt,
        };
        if (defect == "inspection")
        {
            receipts[0] = receipts[0] with { RuntimeIndependence = new NativeRuntimeIndependence("matching-host-inspection", Inspection: "main") };
        }
        await Assert.That(NativeReceiptPolicy.Diagnostics(manifest, receipts)).IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("LICENSE")]
    [Arguments("THIRD-PARTY-NOTICES.md")]
    [Arguments("opencode-mcp.exe")]
    [Arguments("payload.json")]
    public async Task Diagnostics_Should_Require_Each_Payload_Content(string missing, CancellationToken token)
    {
        var manifest = await ManifestAsync(token);
        var receipts = manifest.Runtimes.Select(ReceiptFixture.Create).ToArray();
        receipts[0] = receipts[0] with { Payload = [.. receipts[0].Payload.Where(file => file.Path != missing)] };
        await Assert.That(NativeReceiptPolicy.Diagnostics(manifest, receipts))
            .IsEquivalentTo(["win-x64: Missing required payload file: " + missing], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("executable", "win-x64: Payload command digest differs from verified executable.")]
    [Arguments("runner", "win-x64: RequestedRunner differs from manifest.")]
    [Arguments("builder", "linux-x64: BuilderImage differs from manifest.")]
    [Arguments("runtime", "linux-x64: RuntimeImage differs from manifest.")]
    public async Task Diagnostics_Should_Bind_Payload_And_Platform_Identity(string defect, string diagnostic, CancellationToken token)
    {
        var manifest = await ManifestAsync(token);
        var receipts = manifest.Runtimes.Select(ReceiptFixture.Create).ToArray();
        switch (defect)
        {
            case "executable": receipts[0] = receipts[0] with { ExecutableSha256 = "different" }; break;
            case "runner": receipts[0] = receipts[0] with { RequestedRunner = "different" }; break;
            case "builder": receipts[2] = receipts[2] with { BuilderImage = "different" }; break;
            case "runtime": receipts[2] = receipts[2] with { RuntimeImage = "different" }; break;
        }
        await Assert.That(NativeReceiptPolicy.Diagnostics(manifest, receipts)).IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("published", "Unsuccessful published tests: win-x64")]
    [Arguments("normalized", "Unsuccessful normalized tests: win-x64")]
    [Arguments("extracted", "Unsuccessful extracted tests: win-x64")]
    [Arguments("warnings", "Publish warnings: win-x64")]
    [Arguments("properties", "win-x64: IlcTreatWarningsAsErrors must be true.")]
    [Arguments("archive", "Missing archive identity: win-x64")]
    [Arguments("payload", "Missing payload inventory: win-x64")]
    [Arguments("publish", "Unsuccessful publish: win-x64")]
    public async Task Diagnostics_Should_Reject_Unfinished_Native_Evidence(string defect, string diagnostic, CancellationToken token)
    {
        var manifest = await ManifestAsync(token);
        var receipts = manifest.Runtimes.Select(ReceiptFixture.Create).ToArray();
        var receipt = receipts[0];
        receipts[0] = defect switch
        {
            "published" => receipt with { PublishedTests = new NativeTestRun(0, 0, 0, 0) },
            "normalized" => receipt with { NormalizedTests = null },
            "extracted" => receipt with { ExtractedTests = new NativeTestRun(0, 11, 1, 0) },
            "warnings" => receipt with { Warnings = ["IL3050: fixture"] },
            "properties" => receipt with { Properties = new Dictionary<string, string>(receipt.Properties, StringComparer.Ordinal) { ["IlcTreatWarningsAsErrors"] = "false" } },
            "archive" => receipt with { ArchiveSha256 = null },
            "payload" => receipt with { Payload = [] },
            "publish" => receipt with { PublishExitCode = 1 },
            _ => throw new InvalidOperationException("Unknown fixture defect."),
        };
        await Assert.That(NativeReceiptPolicy.Diagnostics(manifest, receipts)).IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task Diagnostics_Should_Reject_Mixed_Versions_And_Resolved_Dependencies(CancellationToken token)
    {
        var manifest = await ManifestAsync(token);
        var receipts = manifest.Runtimes.Reverse().Select(ReceiptFixture.Create).ToArray();
        receipts[0] = receipts[0] with { Version = "other-version", DependenciesSha256 = "other-dependencies" };
        await Assert.That(NativeReceiptPolicy.Diagnostics(manifest, receipts)).IsEquivalentTo(
            ["Mixed receipt field: Version", "Mixed receipt field: DependenciesSha256"], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task Diagnostics_Should_Reject_Each_Missing_Rid(CancellationToken token)
    {
        var manifest = await ManifestAsync(token);
        foreach (var missing in manifest.Runtimes)
        {
            var receipts = manifest.Runtimes.Reverse().Where(runtime => runtime != missing).Select(ReceiptFixture.Create).ToArray();
            await Assert.That(NativeReceiptPolicy.Diagnostics(manifest, receipts))
                .IsEquivalentTo(["Missing receipt: " + missing.Rid], CollectionOrdering.Matching);
        }
    }

    [Test]
    [Timeout(30_000)]
    public async Task Diagnostics_Should_Reject_Duplicate_And_Unexpected_Rids(CancellationToken token)
    {
        var manifest = await ManifestAsync(token);
        var receipts = manifest.Runtimes.Reverse().Select(ReceiptFixture.Create).ToList();
        receipts.Insert(0, receipts[0] with { Rid = "unexpected" });
        receipts.Add(ReceiptFixture.Create(manifest.Runtimes[0]));
        await Assert.That(NativeReceiptPolicy.Diagnostics(manifest, receipts))
            .IsEquivalentTo(["Duplicate receipt: win-x64", "Unexpected receipt: unexpected"], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task Diagnostics_Should_Reject_Mixed_Source_Sdk_And_Common_Inputs(CancellationToken token)
    {
        var manifest = await ManifestAsync(token);
        var receipts = manifest.Runtimes.Reverse().Select(ReceiptFixture.Create).ToArray();
        receipts[0] = receipts[0] with { SourceCommit = "other-source", SdkVersion = "other-sdk", CommonInputsSha256 = "other-inputs" };
        await Assert.That(NativeReceiptPolicy.Diagnostics(manifest, receipts))
            .IsEquivalentTo(["Mixed receipt field: SourceCommit", "Mixed receipt field: SdkVersion", "Mixed receipt field: CommonInputsSha256"],
                CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task Diagnostics_Should_Reject_A_Linux_Receipt_Without_Runtime_Independence(CancellationToken token)
    {
        var manifest = await ManifestAsync(token);
        var receipts = manifest.Runtimes.Reverse().Select(ReceiptFixture.Create).Select(static receipt =>
            receipt.Rid == "linux-arm64" ? receipt with { RuntimeIndependence = null } : receipt).ToArray();
        await Assert.That(NativeReceiptPolicy.Diagnostics(manifest, receipts))
            .IsEquivalentTo(["Missing Linux runtime-independence proof: linux-arm64"], CollectionOrdering.Matching);
    }

    private static async Task<NativeManifest> ManifestAsync(CancellationToken token)
    {
        var files = new MockFileSystem();
        return await new NativeManifestStore(files).ReadAsync(await new NativeManifestFixture().WriteAsync(files, token), token);
    }
}
