using System.IO.Compression;
using System.Security.Cryptography;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeReceiptValidatorTests
{
    [Test]
    [Timeout(30_000)]
    public async Task ValidateAsync_Should_Report_The_Entire_Ordered_Missing_Receipt_Set(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        await Assert.That(await new NativeReceiptValidator(scenario.Files, scenario.Modes).ValidateAsync(request.Manifest, scenario.Output, token))
            .IsEquivalentTo(["Missing receipt: win-x64", "Missing receipt: win-arm64", "Missing receipt: linux-x64", "Missing receipt: linux-arm64",
                "Missing receipt: osx-x64", "Missing receipt: osx-arm64"], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("changed", "win-x64: Changed payload file: LICENSE")]
    [Arguments("missing", "win-x64: Missing payload file: LICENSE")]
    [Arguments("extra", "win-x64: Unexpected payload file: unexpected")]
    [Arguments("archive", "win-x64 archive: Missing payload file: LICENSE")]
    public async Task ValidateAsync_Should_Reject_Tampered_Payloads_And_Archives(string defect, string diagnostic, CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var published = await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token);
        await scenario.AddStagingInputsAsync(token);
        var receipt = await new NativeStager(scenario.Files, scenario.Runner, scenario.Modes).StageAsync(request, published.PublishDirectory, token);
        var payload = scenario.Files.Path.Combine(scenario.Output, receipt.PayloadDirectory ?? throw new InvalidOperationException());
        var license = scenario.Files.Path.Combine(payload, "LICENSE");
        if (defect == "changed")
        {
            await scenario.Files.File.WriteAllTextAsync(license, "tampered", token);
        }
        else if (defect == "missing")
        {
            scenario.Files.File.Delete(license);
        }
        else if (defect == "extra")
        {
            await scenario.Files.File.WriteAllTextAsync(scenario.Files.Path.Combine(payload, "unexpected"), "extra", token);
        }
        else
        {
            var archivePath = scenario.Files.Path.Combine(scenario.Output, receipt.Archive ?? throw new InvalidOperationException());
            using var bytes = new MemoryStream();
            await bytes.WriteAsync(await scenario.Files.File.ReadAllBytesAsync(archivePath, token), token);
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Update, leaveOpen: true))
            {
                (archive.GetEntry("LICENSE") ?? throw new InvalidOperationException()).Delete();
            }
            var tampered = bytes.ToArray();
            await scenario.Files.File.WriteAllBytesAsync(archivePath, tampered, token);
            receipt = receipt with { ArchiveSha256 = Convert.ToHexStringLower(SHA256.HashData(tampered)) };
            await new NativeEvidenceStore(scenario.Files).WriteAsync(scenario.Files.Path.Combine(scenario.Output, "receipt.json"), receipt, token);
        }
        await Assert.That(await new NativeReceiptValidator(scenario.Files, scenario.Modes).ValidateAsync(request.Manifest, scenario.Output, token))
            .IsEquivalentTo(["Missing receipt: win-arm64", "Missing receipt: linux-x64", "Missing receipt: linux-arm64", "Missing receipt: osx-x64",
                "Missing receipt: osx-arm64", diagnostic], CollectionOrdering.Matching);
    }
}
