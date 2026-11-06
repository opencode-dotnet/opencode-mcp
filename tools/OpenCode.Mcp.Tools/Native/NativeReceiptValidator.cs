using System.IO.Abstractions;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeReceiptValidator(IFileSystem files, IUnixFileModes modes)
{
    public async Task<IReadOnlyList<string>> ValidateAsync(NativeManifest manifest, string directory, CancellationToken token)
    {
        var receipts = new List<NativeReceipt>();
        var locations = new List<string>();
        var store = new NativeEvidenceStore(files);
        if (files.Directory.Exists(directory))
        {
            foreach (var path in files.Directory.EnumerateFiles(directory, "receipt.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                receipts.Add(await store.ReadAsync<NativeReceipt>(path, token).ConfigureAwait(false));
                locations.Add(files.Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Receipt needs a parent directory."));
            }
        }
        var diagnostics = new List<string>(NativeReceiptPolicy.Diagnostics(manifest, receipts));
        for (var index = 0; index < receipts.Count; index++)
        {
            diagnostics.AddRange(await ValidateArtifactsAsync(receipts[index], locations[index], token).ConfigureAwait(false));
        }
        return diagnostics.AsReadOnly();
    }

    private async Task<IReadOnlyList<string>> ValidateArtifactsAsync(NativeReceipt receipt, string directory, CancellationToken token)
    {
        var diagnostics = new List<string>();
        var store = new NativeEvidenceStore(files);
        var rid = new NativeRid(receipt.Rid);
        var inventory = new PayloadInventory(files, modes, rid);
        if (receipt.PayloadManifest is null || receipt.PayloadDirectory is null || receipt.Archive is null)
        {
            return [receipt.Rid + ": Missing staging evidence."];
        }
        var manifestPath = files.Path.Combine(directory, receipt.PayloadManifest);
        if (!files.File.Exists(manifestPath))
        {
            diagnostics.Add(receipt.Rid + ": Missing payload manifest.");
        }
        else
        {
            var manifest = await store.ReadAsync<PayloadFile[]>(manifestPath, token).ConfigureAwait(false);
            if (!receipt.Payload.SequenceEqual(manifest))
            {
                diagnostics.Add(receipt.Rid + ": Payload manifest differs from receipt.");
            }
        }
        var payload = files.Path.Combine(directory, receipt.PayloadDirectory);
        if (files.Directory.Exists(payload))
        {
            diagnostics.AddRange((await inventory.CompareAsync(payload, receipt.Payload, token).ConfigureAwait(false)).Select(error => receipt.Rid + ": " + error));
        }
        else
        {
            diagnostics.Add(receipt.Rid + ": Missing payload directory.");
        }
        var archive = files.Path.Combine(directory, receipt.Archive);
        if (!files.File.Exists(archive))
        {
            diagnostics.Add(receipt.Rid + ": Missing archive.");
        }
        else if (await store.DigestAsync(archive, token).ConfigureAwait(false) != receipt.ArchiveSha256)
        {
            diagnostics.Add(receipt.Rid + ": Archive digest differs from receipt.");
        }
        else
        {
            var extracted = files.Path.Combine(files.Path.GetTempPath(), "opencode-mcp-native-" + Guid.NewGuid().ToString("N"));
            try
            {
                await new PayloadArchive(files, modes, rid).ExtractAsync(archive, extracted, token).ConfigureAwait(false);
                diagnostics.AddRange((await inventory.CompareAsync(extracted, receipt.Payload, token).ConfigureAwait(false)).Select(error => receipt.Rid + " archive: " + error));
            }
            finally
            {
                if (files.Directory.Exists(extracted))
                {
                    files.Directory.Delete(extracted, recursive: true);
                }
            }
        }
        return diagnostics.AsReadOnly();
    }
}
