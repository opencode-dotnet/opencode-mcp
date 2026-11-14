using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeReleaseAssetsTests
{
    private static readonly string[] Rids = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];
    private static readonly string[] Suffixes = ["win-x64.zip", "win-arm64.zip", "linux-x64.tar.gz", "linux-arm64.tar.gz", "osx-x64.tar.gz", "osx-arm64.tar.gz"];

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BuildAsync_Should_Copy_The_Exact_Seven_Asset_Set_In_Manifest_Order(bool emptyOutput, CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        if (emptyOutput)
        {
            scenario.Files.Directory.CreateDirectory(scenario.Output);
        }
        var receiptBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var rid in Rids)
        {
            receiptBytes.Add(rid, await scenario.Files.File.ReadAllBytesAsync(scenario.Files.Path.Combine(scenario.Directory(rid), "receipt.json"), token));
        }
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        await new NativeReleaseAssets(scenario.Files, scenario.Builder.Native.Modes).BuildAsync(scenario.Request, output, token);
        var names = Suffixes.Select(suffix => "opencode-mcp-" + scenario.Version + "-" + suffix).ToArray();
        await Assert.That(scenario.Files.Directory.GetFileSystemEntries(scenario.Output).Select(path => scenario.Files.Path.GetFileName(path) ?? throw new InvalidOperationException()).Order(StringComparer.Ordinal))
            .IsEquivalentTo(names.Append("SHA256SUMS").Order(StringComparer.Ordinal), CollectionOrdering.Matching);
        for (var index = 0; index < Rids.Length; index++)
        {
            await Assert.That(await scenario.Files.File.ReadAllBytesAsync(scenario.Files.Path.Combine(scenario.Output, names[index]), token))
                .IsEquivalentTo(scenario.Archives[Rids[index]], CollectionOrdering.Matching);
            await Assert.That(await scenario.Files.File.ReadAllBytesAsync(scenario.Archive(Rids[index]), token))
                .IsEquivalentTo(scenario.Archives[Rids[index]], CollectionOrdering.Matching);
            await Assert.That(await scenario.Files.File.ReadAllBytesAsync(scenario.Files.Path.Combine(scenario.Directory(Rids[index]), "receipt.json"), token))
                .IsEquivalentTo(receiptBytes[Rids[index]], CollectionOrdering.Matching);
        }
        var checksums = string.Concat(Rids.Select((rid, index) => scenario.Digest(rid) + "  " + names[index] + "\n"));
        await Assert.That(await scenario.Files.File.ReadAllBytesAsync(scenario.Files.Path.Combine(scenario.Output, "SHA256SUMS"), token))
            .IsEquivalentTo(Encoding.UTF8.GetBytes(checksums), CollectionOrdering.Matching);
        var expected = new[] { scenario.Output }.Concat(Rids.Select((rid, index) => names[index] + " SHA-256: " + scenario.Digest(rid)))
            .Append("SHA256SUMS SHA-256: " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(checksums))));
        await Assert.That(output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)).IsEqualTo(string.Join('\n', expected) + "\n");
    }

    [Test]
    public async Task BuildAsync_Should_Use_Manifest_Order_Not_Receipt_Or_Filename_Order(CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        var rows = scenario.Manifest.Runtimes;
        scenario.Manifest = scenario.Manifest with { Runtimes = [rows[2], rows[0], rows[5], rows[1], rows[4], rows[3]] };
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        await new NativeReleaseAssets(scenario.Files, scenario.Builder.Native.Modes).BuildAsync(scenario.Request, output, token);
        var prefix = "opencode-mcp-" + scenario.Version + "-";
        var checksums = scenario.Digest("linux-x64") + "  " + prefix + "linux-x64.tar.gz\n"
            + scenario.Digest("win-x64") + "  " + prefix + "win-x64.zip\n"
            + scenario.Digest("osx-arm64") + "  " + prefix + "osx-arm64.tar.gz\n"
            + scenario.Digest("win-arm64") + "  " + prefix + "win-arm64.zip\n"
            + scenario.Digest("osx-x64") + "  " + prefix + "osx-x64.tar.gz\n"
            + scenario.Digest("linux-arm64") + "  " + prefix + "linux-arm64.tar.gz\n";
        await Assert.That(await scenario.Files.File.ReadAllTextAsync(scenario.Files.Path.Combine(scenario.Output, "SHA256SUMS"), token)).IsEqualTo(checksums);
        var expected = scenario.Output + "\n" + prefix + "linux-x64.tar.gz SHA-256: " + scenario.Digest("linux-x64") + "\n"
            + prefix + "win-x64.zip SHA-256: " + scenario.Digest("win-x64") + "\n"
            + prefix + "osx-arm64.tar.gz SHA-256: " + scenario.Digest("osx-arm64") + "\n"
            + prefix + "win-arm64.zip SHA-256: " + scenario.Digest("win-arm64") + "\n"
            + prefix + "osx-x64.tar.gz SHA-256: " + scenario.Digest("osx-x64") + "\n"
            + prefix + "linux-arm64.tar.gz SHA-256: " + scenario.Digest("linux-arm64") + "\n"
            + "SHA256SUMS SHA-256: " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(checksums))) + "\n";
        await Assert.That(output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)).IsEqualTo(expected);
    }

    [Test]
    public async Task BuildAsync_Should_Read_Detached_Receipts_And_Map_Evidence_By_Rid(CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        await scenario.DetachReceiptsAsync(token);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        await new NativeReleaseAssets(scenario.Files, scenario.Builder.Native.Modes).BuildAsync(scenario.Request, output, token);
        var names = Suffixes.Select(suffix => "opencode-mcp-" + scenario.Version + "-" + suffix).ToArray();
        await Assert.That(scenario.Files.Directory.GetFiles(scenario.Receipts, "*", SearchOption.AllDirectories).Select(path => scenario.Files.Path.GetExtension(path) ?? throw new InvalidOperationException()))
            .IsEquivalentTo(Enumerable.Repeat(".json", 6), CollectionOrdering.Matching);
        for (var index = 0; index < Rids.Length; index++)
        {
            await Assert.That(await scenario.Files.File.ReadAllBytesAsync(scenario.Files.Path.Combine(scenario.Output, names[index]), token))
                .IsEquivalentTo(scenario.Archives[Rids[index]], CollectionOrdering.Matching);
        }
        var checksums = string.Concat(Rids.Select((rid, index) => scenario.Digest(rid) + "  " + names[index] + "\n"));
        await Assert.That(await scenario.Files.File.ReadAllTextAsync(scenario.Files.Path.Combine(scenario.Output, "SHA256SUMS"), token)).IsEqualTo(checksums);
        await Assert.That(scenario.Files.Directory.GetFileSystemEntries(scenario.Output).Select(path => scenario.Files.Path.GetFileName(path) ?? throw new InvalidOperationException()).Order(StringComparer.Ordinal))
            .IsEquivalentTo(names.Append("SHA256SUMS").Order(StringComparer.Ordinal), CollectionOrdering.Matching);
        var expected = new[] { scenario.Output }.Concat(Rids.Select((rid, index) => names[index] + " SHA-256: " + scenario.Digest(rid)))
            .Append("SHA256SUMS SHA-256: " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(checksums))));
        await Assert.That(output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)).IsEqualTo(string.Join('\n', expected) + "\n");
    }

    [Test]
    public async Task BuildAsync_Should_Require_The_Requested_Version_For_Every_Receipt(CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        await Assert.That(() => new NativeReleaseAssets(scenario.Files, scenario.Builder.Native.Modes).BuildAsync(scenario.Request with { Version = "different" }, output, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("win-x64: Receipt version must be different.\nwin-arm64: Receipt version must be different.\n"
                + "linux-x64: Receipt version must be different.\nlinux-arm64: Receipt version must be different.\nosx-x64: Receipt version must be different.\nosx-arm64: Receipt version must be different.", StringComparison.Ordinal);
        await Assert.That(output.ToString()).IsEmpty();
        await Assert.That(scenario.Files.Directory.Exists(scenario.Output)).IsFalse();
        foreach (var rid in Rids)
        {
            await Assert.That(await scenario.Files.File.ReadAllBytesAsync(scenario.Archive(rid), token)).IsEquivalentTo(scenario.Archives[rid], CollectionOrdering.Matching);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BuildAsync_Should_Require_The_Canonical_Staged_Archive_Name(bool missingName, CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        await scenario.WriteReceiptAsync(scenario.Receipt("win-x64") with { Archive = missingName ? null : "other.zip" }, token);
        await AssertInputRefusalAsync(scenario, "win-x64: Staged archive name must be opencode-mcp-win-x64.zip.", token);
    }

    [Test]
    [Arguments("directory", "win-x64: Missing payload manifest.\nwin-x64: Missing payload directory.\nwin-x64: Missing archive.")]
    [Arguments("payload", "win-x64: Changed payload file: LICENSE")]
    [Arguments("archive", "win-x64: Archive digest differs from receipt.")]
    [Arguments("contents", "win-x64 archive: Missing payload file: LICENSE")]
    [Arguments("combined", "win-x64: Missing payload manifest.\nwin-x64: Missing payload directory.\nwin-x64: Missing archive.\nlinux-arm64: Changed payload file: LICENSE\nosx-x64: Archive digest differs from receipt.")]
    public async Task BuildAsync_Should_Preserve_All_Artifact_Validation_Refusals(string fault, string diagnostic, CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        var receipt = scenario.Receipt("win-x64");
        var directory = scenario.Directory("win-x64");
        if (fault is "directory" or "combined")
        {
            scenario.Files.Directory.Delete(directory, recursive: true);
        }
        if (fault is "payload" or "combined")
        {
            await scenario.Files.File.WriteAllTextAsync(scenario.Files.Path.Combine(scenario.Directory(fault == "combined" ? "linux-arm64" : "win-x64"), "payload", "LICENSE"), "altered", token);
        }
        if (fault is "archive" or "combined")
        {
            await scenario.Files.File.WriteAllTextAsync(scenario.Archive(fault == "combined" ? "osx-x64" : "win-x64"), "altered", token);
        }
        if (fault == "contents")
        {
            await new PayloadArchive(scenario.Files, scenario.Builder.Native.Modes, new NativeRid(receipt.Rid)).CreateAsync(
                scenario.Files.Path.Combine(directory, "payload"), scenario.Archive(receipt.Rid), [.. receipt.Payload.Where(static file => file.Path != "LICENSE")], token);
            var digest = Convert.ToHexStringLower(SHA256.HashData(await scenario.Files.File.ReadAllBytesAsync(scenario.Archive(receipt.Rid), token)));
            await scenario.WriteReceiptAsync(receipt with { ArchiveSha256 = digest }, token);
        }
        await scenario.DetachReceiptsAsync(token);
        await AssertInputRefusalAsync(scenario, diagnostic, token);
    }

    [Test]
    [Arguments("missing", "Missing receipt: win-x64")]
    [Arguments("duplicate", "Duplicate receipt: win-arm64")]
    [Arguments("unexpected", "Unexpected receipt: other-x64")]
    [Arguments("mixed", "Mixed receipt field: SourceCommit\nMixed receipt field: SdkVersion\nMixed receipt field: CommonInputsSha256\nMixed receipt field: Version\nMixed receipt field: DependenciesSha256")]
    [Arguments("combined", "Missing receipt: win-x64\nDuplicate receipt: win-arm64\nUnexpected receipt: a-other\nUnexpected receipt: z-other\nMixed receipt field: SourceCommit\nMixed receipt field: SdkVersion\nMixed receipt field: CommonInputsSha256\nMixed receipt field: Version\nMixed receipt field: DependenciesSha256")]
    public async Task BuildAsync_Should_Refuse_The_Complete_Receipt_Diagnostic_List(string fault, string diagnostic, CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        var store = new NativeEvidenceStore(scenario.Files);
        if (fault is "missing" or "combined")
        {
            scenario.Files.File.Delete(scenario.Files.Path.Combine(scenario.Directory("win-x64"), "receipt.json"));
        }
        if (fault is "duplicate" or "combined")
        {
            await store.WriteAsync(scenario.Files.Path.Combine(scenario.Receipts, "duplicate", "receipt.json"), scenario.Receipt("win-arm64"), token);
        }
        if (fault is "unexpected" or "combined")
        {
            foreach (var rid in fault == "combined" ? new[] { "z-other", "a-other" } : ["other-x64"])
            {
                await store.WriteAsync(scenario.Files.Path.Combine(scenario.Receipts, rid, "receipt.json"), scenario.Receipt("linux-x64") with { Rid = rid }, token);
            }
        }
        if (fault is "mixed" or "combined")
        {
            await scenario.WriteReceiptAsync(scenario.Receipt("linux-x64") with { SourceCommit = "different", SdkVersion = "different", CommonInputsSha256 = "different", Version = "different", DependenciesSha256 = "different" }, token);
        }
        await AssertInputRefusalAsync(scenario, diagnostic, token);
    }

    [Test]
    [Arguments("file")]
    [Arguments("foreign file")]
    [Arguments("empty child")]
    public async Task BuildAsync_Should_Preserve_A_Foreign_Output(string kind, CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        var sentinel = kind == "file" ? scenario.Output : scenario.Files.Path.Combine(scenario.Output, "foreign");
        if (kind != "file")
        {
            scenario.Files.Directory.CreateDirectory(scenario.Output);
        }
        if (kind == "empty child")
        {
            scenario.Files.Directory.CreateDirectory(sentinel);
        }
        else
        {
            await scenario.Files.File.WriteAllTextAsync(sentinel, "foreign", token);
        }
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        await Assert.That(() => new NativeReleaseAssets(scenario.Files, scenario.Builder.Native.Modes).BuildAsync(scenario.Request, output, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Release assets directory must be absent or empty: " + scenario.Output, StringComparison.Ordinal);
        await Assert.That(output.ToString()).IsEmpty();
        if (kind != "file")
        {
            await Assert.That(scenario.Files.Directory.GetFileSystemEntries(scenario.Output)).IsEquivalentTo([sentinel], CollectionOrdering.Matching);
        }
        if (kind == "empty child")
        {
            await Assert.That(scenario.Files.Directory.GetFileSystemEntries(sentinel)).IsEmpty();
        }
        else
        {
            await Assert.That(await scenario.Files.File.ReadAllTextAsync(sentinel, token)).IsEqualTo("foreign");
        }
        foreach (var rid in Rids)
        {
            await Assert.That(await scenario.Files.File.ReadAllBytesAsync(scenario.Archive(rid), token)).IsEquivalentTo(scenario.Archives[rid], CollectionOrdering.Matching);
        }
    }

    private static async Task AssertInputRefusalAsync(NativeReleaseAssetsScenario scenario, string diagnostic, CancellationToken token)
    {
        var paths = scenario.Files.Directory.GetFiles(scenario.Evidence, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var before = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            before.Add(path, await scenario.Files.File.ReadAllBytesAsync(path, token));
        }
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        await Assert.That(() => new NativeReleaseAssets(scenario.Files, scenario.Builder.Native.Modes).BuildAsync(scenario.Request, output, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage(diagnostic, StringComparison.Ordinal);
        await Assert.That(output.ToString()).IsEmpty();
        await Assert.That(scenario.Files.Directory.Exists(scenario.Output)).IsFalse();
        await Assert.That(scenario.Files.Directory.GetFiles(scenario.Evidence, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            .IsEquivalentTo(paths, CollectionOrdering.Matching);
        foreach (var path in paths)
        {
            await Assert.That(await scenario.Files.File.ReadAllBytesAsync(path, token)).IsEquivalentTo(before[path], CollectionOrdering.Matching);
        }
        await scenario.Files.File.WriteAllTextAsync(scenario.Output, "foreign", token);
        await Assert.That(() => new NativeReleaseAssets(scenario.Files, scenario.Builder.Native.Modes).BuildAsync(scenario.Request, output, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage(diagnostic, StringComparison.Ordinal);
        await Assert.That(await scenario.Files.File.ReadAllTextAsync(scenario.Output, token)).IsEqualTo("foreign");
        await Assert.That(output.ToString()).IsEmpty();
    }

    [Test]
    public async Task BuildAsync_Should_Reject_A_Changed_Copied_Archive(CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        await Assert.That(() => new NativeReleaseAssets(scenario.DecorateWrites("changed"), scenario.Builder.Native.Modes).BuildAsync(scenario.Request, output, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("linux-x64: Release archive digest differs from receipt.", StringComparison.Ordinal);
        await Assert.That(scenario.Files.Directory.Exists(scenario.Output)).IsFalse();
        await Assert.That(output.ToString()).IsEmpty();
        foreach (var rid in Rids)
        {
            await Assert.That(await scenario.Files.File.ReadAllBytesAsync(scenario.Archive(rid), token)).IsEquivalentTo(scenario.Archives[rid], CollectionOrdering.Matching);
        }
    }

    [Test]
    [Arguments("archive", "fixture archive write failure")]
    [Arguments("checksum", "fixture checksum write failure")]
    public async Task BuildAsync_Should_Delete_Owned_Output_After_A_Write_Refusal(string fault, string diagnostic, CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        await Assert.That(() => new NativeReleaseAssets(scenario.DecorateWrites(fault), scenario.Builder.Native.Modes).BuildAsync(scenario.Request, output, token))
            .ThrowsExactly<IOException>().WithMessage(diagnostic, StringComparison.Ordinal);
        await Assert.That(scenario.Files.Directory.Exists(scenario.Output)).IsFalse();
        await Assert.That(output.ToString()).IsEmpty();
        foreach (var rid in Rids)
        {
            await Assert.That(await scenario.Files.File.ReadAllBytesAsync(scenario.Archive(rid), token)).IsEquivalentTo(scenario.Archives[rid], CollectionOrdering.Matching);
        }
    }
}
