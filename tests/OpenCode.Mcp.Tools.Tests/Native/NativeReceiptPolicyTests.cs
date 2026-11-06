using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeReceiptPolicyTests
{
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
