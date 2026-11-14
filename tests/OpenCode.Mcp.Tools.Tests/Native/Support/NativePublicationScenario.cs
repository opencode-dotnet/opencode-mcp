using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using OpenCode.Mcp.Tools.Native;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal sealed class NativePublicationScenario(NativeReleaseAssetsScenario assets)
{
    public NativeReleaseAssetsScenario Candidate { get; } = assets;
    public IFileSystem Files => Candidate.Files;
    public string Feed => Candidate.Builder.Feed;
    public string Commit => Candidate.Builder.Receipt.SourceCommit;
    public NativePublicationRequest Request => new(Candidate.Manifest, Candidate.Receipts, Feed, Candidate.Output, Candidate.Version, Commit);
    public string Checksums => Files.Path.Combine(Candidate.Output, "SHA256SUMS");

    public static async Task<NativePublicationScenario> CreateAsync(CancellationToken token)
    {
        var candidate = await NativeReleaseAssetsScenario.CreateAsync(token);
        foreach (var child in candidate.Builder.Children)
        {
            await candidate.WriteReceiptAsync(child.Receipt with { SourceStatus = "" }, token);
        }
        await candidate.Builder.WriteFeedAsync(token);
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        await new NativeReleaseAssets(candidate.Files, candidate.Builder.Native.Modes).BuildAsync(candidate.Request, output, token);
        return new(candidate);
    }

    public Task<IReadOnlyList<string>> ValidateAsync(CancellationToken token) =>
        new NativePublicationValidator(Files, Candidate.Builder.Native.Modes).ValidateAsync(Request, token);

    public string Asset(string suffix) => Files.Path.Combine(Candidate.Output, "opencode-mcp-" + Candidate.Version + "-" + suffix);
    public string FeedEvidence(string rid, string name) => Files.Path.Combine(Feed, "evidence", rid, name);

    public async Task RewriteChecksumsAsync(string fault, CancellationToken token)
    {
        var text = await Files.File.ReadAllTextAsync(Checksums, token);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        text = fault switch
        {
            "order" => string.Join('\n', lines.Reverse()) + "\n",
            "digest" => new string('0', 64) + text[64..],
            "filename" => text.Replace("-win-x64.zip", "-wrong.zip", StringComparison.Ordinal),
            "BOM" => "\uFEFF" + text,
            "CRLF" => text.Replace("\n", "\r\n", StringComparison.Ordinal),
            "self" => text + new string('0', 64) + "  SHA256SUMS\n",
            "extra" => text + "\n",
            _ => text,
        };
        await Files.File.WriteAllBytesAsync(Checksums, Encoding.UTF8.GetBytes(text), token);
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> CaptureAsync(CancellationToken token)
    {
        var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Files.Directory.GetFiles(Candidate.Build.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            snapshot.Add(path, await Files.File.ReadAllBytesAsync(path, token));
        }
        return snapshot;
    }

    public async Task AssertUnchangedAsync(IReadOnlyDictionary<string, byte[]> before, CancellationToken token)
    {
        await Assert.That(Files.Directory.GetFiles(Candidate.Build.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            .IsEquivalentTo(before.Keys, CollectionOrdering.Matching);
        foreach (var pair in before)
        {
            await Assert.That(await Files.File.ReadAllBytesAsync(pair.Key, token)).IsEquivalentTo(pair.Value, CollectionOrdering.Matching);
        }
    }

    public async Task ChangeArchiveAndChecksumsAsync(CancellationToken token)
    {
        var path = Asset("linux-x64.tar.gz");
        var original = Convert.ToHexStringLower(SHA256.HashData(await Files.File.ReadAllBytesAsync(path, token)));
        await Files.File.WriteAllBytesAsync(path, [1, 2, 3], token);
        byte[] changedBytes = [1, 2, 3];
        var changed = Convert.ToHexStringLower(SHA256.HashData(changedBytes));
        var text = await Files.File.ReadAllTextAsync(Checksums, token);
        await Files.File.WriteAllBytesAsync(Checksums, Encoding.UTF8.GetBytes(text.Replace(original, changed, StringComparison.Ordinal)), token);
    }
}
