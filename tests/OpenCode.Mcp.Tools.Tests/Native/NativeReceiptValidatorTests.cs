using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeReceiptValidatorTests
{
    [Test]
    [Timeout(30_000)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ValidateAsync_Should_Check_Unix_Tar_Modes_On_A_Windows_Host_Without_Writing_Them(bool changedMode, CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var runtime = request.Manifest.Runtimes.Single(static runtime => runtime.Rid == "linux-x64");
        var manifest = request.Manifest with { Runtimes = [runtime] };
        var store = new NativeEvidenceStore(scenario.Files);
        var manifestPath = scenario.Files.Path.Combine(request.Root, "manifest.json");
        await store.WriteAsync(manifestPath, manifest, token);
        var payload = scenario.Files.Path.Combine(scenario.Output, "payload");
        scenario.Files.Directory.CreateDirectory(payload);
        var receipt = ReceiptFixture.Create(runtime);
        var inventory = new List<PayloadFile>();
        foreach (var file in receipt.Payload)
        {
            var bytes = Encoding.UTF8.GetBytes(file.Path);
            await scenario.Files.File.WriteAllBytesAsync(scenario.Files.Path.Combine(payload, file.Path), bytes, token);
            inventory.Add(file with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Size = bytes.LongLength, UnixMode = 493 });
        }
        receipt = receipt with { Payload = inventory, Archive = "payload.tar.gz", ExecutableSha256 = inventory.Single(static file => file.Path == "opencode-mcp").Sha256 };
        await store.WriteAsync(scenario.Files.Path.Combine(scenario.Output, "payload-manifest.json"), inventory, token);
        var archivePath = scenario.Files.Path.Combine(scenario.Output, receipt.Archive);
        var archiveInventory = inventory.Select(file => changedMode && file.Path == "opencode-mcp" ? file with { UnixMode = 420 } : file).ToArray();
        await new PayloadArchive(scenario.Files, scenario.Modes, new NativeRid(runtime.Rid)).CreateAsync(payload, archivePath, archiveInventory, token);
        receipt = receipt with { ArchiveSha256 = await store.DigestAsync(archivePath, token) };
        await store.WriteAsync(scenario.Files.Path.Combine(scenario.Output, "receipt.json"), receipt, token);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Runner, new FixtureHostReader(request.Host with { WorkingDirectory = request.Root }), scenario.Modes, output, errors);
        var result = await application.RunAsync(["validate", "--receipts", scenario.Output, "--manifest", manifestPath], token);
        await Assert.That(errors.ToString().TrimEnd('\r', '\n')).IsEqualTo(changedMode ? "linux-x64 archive: Changed payload file: opencode-mcp" : "");
        await Assert.That(output.ToString().TrimEnd('\r', '\n')).IsEqualTo(changedMode ? "" : "Native receipt set is complete.");
        await Assert.That(result).IsEqualTo(changedMode ? 2 : 0);
        await Assert.That(scenario.Modes.Written).IsEmpty();
    }

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
