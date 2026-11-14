using System.IO.Abstractions;
using System.Text;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeReleaseAssets(IFileSystem files, IUnixFileModes modes)
{
    public async Task BuildAsync(NativeReleaseAssetRequest request, TextWriter output, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        var store = new NativeEvidenceStore(files);
        var locations = await store.ReadReceiptLocationsAsync(request.Receipts, token).ConfigureAwait(false);
        NativeDiagnostics.Refuse(NativeReceiptPolicy.Diagnostics(request.Manifest, [.. locations.Select(static location => location.Receipt)]));
        var receipts = locations.ToDictionary(static location => location.Receipt.Rid, static location => location.Receipt, StringComparer.Ordinal);
        NativeDiagnostics.Refuse([.. request.Manifest.Runtimes.Select(runtime => receipts[runtime.Rid])
            .Where(receipt => !string.Equals(receipt.Version, request.Version, StringComparison.Ordinal))
            .Select(receipt => receipt.Rid + ": Receipt version must be " + request.Version + ".")]);
        NativeDiagnostics.Refuse(await ValidateEvidenceAsync(request, receipts, token).ConfigureAwait(false));
        if (!store.IsAbsentOrEmptyDirectory(request.Output))
        {
            throw new InvalidOperationException("Release assets directory must be absent or empty: " + request.Output);
        }
        await store.WriteOwnedDirectoryAsync(request.Output, async () =>
        {
            var lines = new List<string>();
            var report = new List<string> { request.Output };
            foreach (var rid in request.Manifest.Runtimes.Select(static runtime => runtime.Rid))
            {
                var receipt = receipts[rid];
                var name = "opencode-mcp-" + request.Version + "-" + rid + new NativeRid(rid).ArchiveExtension;
                var source = files.Path.Combine(request.Evidence, "native-complete-" + rid, receipt.Archive ?? throw new InvalidOperationException("Missing archive."));
                var destination = files.Path.Combine(request.Output, name);
                await files.File.WriteAllBytesAsync(destination, await files.File.ReadAllBytesAsync(source, token).ConfigureAwait(false), token).ConfigureAwait(false);
                var digest = await store.DigestAsync(destination, token).ConfigureAwait(false);
                if (!string.Equals(digest, receipt.ArchiveSha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(rid + ": Release archive digest differs from receipt.");
                }
                lines.Add(digest + "  " + name + "\n");
                report.Add(name + " SHA-256: " + digest);
            }
            var checksums = files.Path.Combine(request.Output, "SHA256SUMS");
            await files.File.WriteAllTextAsync(checksums, string.Concat(lines), new UTF8Encoding(false), token).ConfigureAwait(false);
            report.Add("SHA256SUMS SHA-256: " + await store.DigestAsync(checksums, token).ConfigureAwait(false));
            foreach (var line in report)
            {
                await output.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> ValidateEvidenceAsync(NativeReleaseAssetRequest request, Dictionary<string, NativeReceipt> receipts, CancellationToken token)
    {
        var diagnostics = new List<string>();
        var validator = new NativeReceiptValidator(files, modes);
        foreach (var rid in request.Manifest.Runtimes.Select(static runtime => runtime.Rid))
        {
            var receipt = receipts[rid];
            var name = "opencode-mcp-" + rid + new NativeRid(rid).ArchiveExtension;
            if (!string.Equals(receipt.Archive, name, StringComparison.Ordinal))
            {
                diagnostics.Add(rid + ": Staged archive name must be " + name + ".");
            }
            else
            {
                diagnostics.AddRange(await validator.ValidateArtifactsAsync(receipt, files.Path.Combine(request.Evidence, "native-complete-" + rid), token).ConfigureAwait(false));
            }
        }
        return diagnostics.AsReadOnly();
    }
}
