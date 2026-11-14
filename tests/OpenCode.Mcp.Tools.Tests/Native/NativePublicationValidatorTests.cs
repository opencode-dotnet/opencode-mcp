using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativePublicationValidatorTests
{
    private static readonly string[] Rids = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];

    [Test]
    public async Task ValidateAsync_Should_Bind_Every_Receipt_To_The_Candidate(CancellationToken token)
    {
        var scenario = await NativePublicationScenario.CreateAsync(token);
        foreach (var child in scenario.Candidate.Builder.Children)
        {
            await scenario.Candidate.WriteReceiptAsync(child.Receipt with { SourceStatus = " M development" }, token);
        }
        var request = scenario.Request with { Version = "different-version", Commit = "different-commit" };
        var result = await new NativePublicationValidator(scenario.Files, scenario.Candidate.Builder.Native.Modes).ValidateAsync(request, token);
        await Assert.That(result).IsEquivalentTo(Rids
            .SelectMany(static rid => new[] { rid + ": Receipt version must be different-version.", rid + ": Receipt source commit must be different-commit.",
                rid + ": Candidate receipt source status must be empty." }), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("native")]
    [Arguments("feed")]
    public async Task ValidateAsync_Should_Preserve_The_Complete_Native_And_Feed_Refusals(string stage, CancellationToken token)
    {
        var scenario = await NativePublicationScenario.CreateAsync(token);
        string[] expected;
        if (stage == "native")
        {
            scenario.Files.File.Delete(scenario.Files.Path.Combine(scenario.Candidate.Directory("win-arm64"), "payload-manifest.json"));
            scenario.Files.File.Delete(scenario.Candidate.Archive("win-arm64"));
            scenario.Files.File.Delete(scenario.Files.Path.Combine(scenario.Candidate.Directory("osx-x64"), "payload", "LICENSE"));
            expected = ["osx-x64: Missing payload file: LICENSE", "win-arm64: Missing payload manifest.", "win-arm64: Missing archive."];
        }
        else
        {
            scenario.Files.File.Delete(scenario.Files.Path.Combine(scenario.Feed, scenario.Candidate.Builder.PackageFileName));
            await scenario.Files.File.WriteAllTextAsync(scenario.Files.Path.Combine(scenario.Feed, "z-extra"), "extra", token);
            await scenario.Files.File.WriteAllTextAsync(scenario.Files.Path.Combine(scenario.Feed, "a-extra"), "extra", token);
            scenario.Files.Directory.CreateDirectory(scenario.Files.Path.Combine(scenario.Feed, "extra-directory"));
            expected = ["Missing feed file: " + scenario.Candidate.Builder.PackageFileName, "Unexpected feed file: a-extra", "Unexpected feed file: z-extra", "Unexpected feed directory: extra-directory"];
        }
        scenario.Files.File.Delete(scenario.Checksums);
        await AssertRefusalAsync(scenario, expected, token);
    }

    [Test]
    [Arguments("receipt.json")]
    [Arguments("payload-manifest.json")]
    public async Task ValidateAsync_Should_Require_Byte_Identical_Feed_Evidence(string name, CancellationToken token)
    {
        var scenario = await NativePublicationScenario.CreateAsync(token);
        foreach (var rid in new[] { "osx-arm64", "win-x64" })
        {
            var path = scenario.FeedEvidence(rid, name);
            await scenario.Files.File.WriteAllTextAsync(path, await scenario.Files.File.ReadAllTextAsync(path, token) + " \n", token);
        }
        var description = name == "receipt.json" ? "receipt" : "payload manifest";
        await AssertRefusalAsync(scenario, ["win-x64: Feed " + description + " differs from completed evidence.",
            "osx-arm64: Feed " + description + " differs from completed evidence."], token);
    }

    [Test]
    public async Task ValidateAsync_Should_Complete_The_Feed_Stage_Before_Refusing(CancellationToken token)
    {
        var scenario = await NativePublicationScenario.CreateAsync(token);
        scenario.Files.File.Delete(scenario.Files.Path.Combine(scenario.Feed, scenario.Candidate.Builder.PackageFileName));
        scenario.Files.File.Delete(scenario.Checksums);
        var retained = scenario.FeedEvidence("win-x64", "receipt.json");
        await scenario.Files.File.WriteAllTextAsync(retained, await scenario.Files.File.ReadAllTextAsync(retained, token) + " \n", token);
        await AssertRefusalAsync(scenario, ["Missing feed file: " + scenario.Candidate.Builder.PackageFileName,
            "win-x64: Feed receipt differs from completed evidence."], token);
    }

    [Test]
    public async Task ValidateAsync_Should_Require_The_Exact_Release_Asset_Set(CancellationToken token)
    {
        var scenario = await NativePublicationScenario.CreateAsync(token);
        scenario.Files.File.Delete(scenario.Asset("win-x64.zip"));
        scenario.Files.File.Delete(scenario.Asset("linux-arm64.tar.gz"));
        scenario.Files.File.Delete(scenario.Checksums);
        foreach (var name in new[] { "z-extra", "a-extra" })
        {
            await scenario.Files.File.WriteAllTextAsync(scenario.Files.Path.Combine(scenario.Request.Assets, name), "extra", token);
        }
        scenario.Files.Directory.CreateDirectory(scenario.Files.Path.Combine(scenario.Request.Assets, "z-directory"));
        scenario.Files.Directory.CreateDirectory(scenario.Files.Path.Combine(scenario.Request.Assets, "a-directory"));
        await scenario.Files.File.WriteAllTextAsync(scenario.Files.Path.Combine(scenario.Request.Assets, "a-directory", "nested"), "extra", token);
        await AssertRefusalAsync(scenario, ["Missing release asset: opencode-mcp-" + scenario.Request.Version + "-win-x64.zip",
            "Missing release asset: opencode-mcp-" + scenario.Request.Version + "-linux-arm64.tar.gz", "Missing release asset: SHA256SUMS",
            "Unexpected release asset: a-directory/nested", "Unexpected release asset: a-extra", "Unexpected release asset: z-extra",
            "Unexpected release directory: a-directory", "Unexpected release directory: z-directory"], token);
    }

    [Test]
    public async Task ValidateAsync_Should_Compare_Archives_With_Receipts_Not_Only_Checksums(CancellationToken token)
    {
        var scenario = await NativePublicationScenario.CreateAsync(token);
        await scenario.ChangeArchiveAndChecksumsAsync(token);
        await AssertRefusalAsync(scenario, ["linux-x64: Release archive digest differs from receipt.",
            "SHA256SUMS differs from the ordered release checksums."], token);
    }

    [Test]
    [Arguments("order")]
    [Arguments("digest")]
    [Arguments("filename")]
    [Arguments("BOM")]
    [Arguments("CRLF")]
    [Arguments("self")]
    [Arguments("extra")]
    [Arguments("script order")]
    [Arguments("script digest")]
    public async Task ValidateAsync_Should_Require_Receipt_Ordered_Checksum_Bytes(string fault, CancellationToken token)
    {
        var scenario = await NativePublicationScenario.CreateAsync(token);
        await scenario.RewriteChecksumsAsync(fault, token);
        await AssertRefusalAsync(scenario, ["SHA256SUMS differs from the ordered release checksums."], token);
    }

    [Test]
    public async Task ValidateAsync_Should_Require_Both_Release_Installers(CancellationToken token)
    {
        var scenario = await NativePublicationScenario.CreateAsync(token);
        scenario.Files.File.Delete(scenario.Files.Path.Combine(scenario.Request.Assets, "install.ps1"));
        scenario.Files.File.Delete(scenario.Files.Path.Combine(scenario.Request.Assets, "install.sh"));
        await AssertRefusalAsync(scenario, ["Missing release asset: install.sh", "Missing release asset: install.ps1"], token);
    }

    [Test]
    [Arguments("install.sh")]
    [Arguments("install.ps1")]
    public async Task ValidateAsync_Should_Require_Committed_Installer_Bytes(string name, CancellationToken token)
    {
        var scenario = await NativePublicationScenario.CreateAsync(token);
        var path = scenario.Files.Path.Combine(scenario.Request.Assets, name);
        var changed = await scenario.Files.File.ReadAllTextAsync(path, token) + "# altered\n";
        await scenario.Files.File.WriteAllTextAsync(path, changed, token);
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await scenario.Files.File.ReadAllBytesAsync(path, token)));
        var text = await scenario.Files.File.ReadAllTextAsync(scenario.Checksums, token);
        await scenario.Files.File.WriteAllBytesAsync(scenario.Checksums, System.Text.Encoding.UTF8.GetBytes(
            text.Replace(scenario.Candidate.ScriptDigest(name), digest, StringComparison.Ordinal)), token);
        await AssertRefusalAsync(scenario, [name + ": Release script differs from committed installer.",
            "SHA256SUMS differs from the ordered release checksums."], token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ValidateAsync_Should_Require_Committed_Default_Versions(bool missing, CancellationToken token)
    {
        var scenario = await NativePublicationScenario.CreateAsync(token);
        foreach (var name in new[] { "install.ps1", "install.sh" })
        {
            var path = scenario.Files.Path.Combine(scenario.Candidate.Installers, name);
            if (missing)
            {
                scenario.Files.File.Delete(path);
            }
            else
            {
                await scenario.Files.File.WriteAllTextAsync(path, (await scenario.Files.File.ReadAllTextAsync(path, token))
                    .Replace(scenario.Request.Version, "different", StringComparison.Ordinal), token);
            }
        }
        await AssertRefusalAsync(scenario, missing ? ["Missing installer script: install.sh.", "Missing installer script: install.ps1."]
            : ["Installer default version must be " + scenario.Request.Version + ": install.sh.",
                "Installer default version must be " + scenario.Request.Version + ": install.ps1."], token);
    }

    private static async Task AssertRefusalAsync(NativePublicationScenario scenario, IReadOnlyList<string> expected, CancellationToken token)
    {
        var before = await scenario.CaptureAsync(token);
        await Assert.That(await scenario.ValidateAsync(token)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await scenario.AssertUnchangedAsync(before, token);
    }
}
