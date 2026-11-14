using System.IO.Abstractions;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeReceiptValidator(IFileSystem files, IUnixFileModes modes)
{
    public async Task<IReadOnlyList<string>> ValidateAsync(NativeManifest manifest, string directory, CancellationToken token)
    {
        var locations = await new NativeEvidenceStore(files).ReadReceiptLocationsAsync(directory, token).ConfigureAwait(false);
        var receipts = locations.Select(static location => location.Receipt).ToArray();
        var diagnostics = new List<string>(NativeReceiptPolicy.Diagnostics(manifest, receipts));
        foreach (var location in locations)
        {
            diagnostics.AddRange(await ValidateArtifactsAsync(location.Receipt, location.Directory, token).ConfigureAwait(false));
        }
        return diagnostics.AsReadOnly();
    }

    public async Task<IReadOnlyList<string>> ValidateArtifactsAsync(NativeReceipt receipt, string directory, CancellationToken token)
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
            var observed = await inventory.CaptureAsync(payload, token).ConfigureAwait(false);
            diagnostics.AddRange(PayloadInventory.Compare([.. observed.Select(static file => file with { UnixMode = null })],
                [.. receipt.Payload.Select(static file => file with { UnixMode = null })]).Select(error => receipt.Rid + ": " + error));
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
            var archived = await new PayloadArchive(files, modes, rid).ReadInventoryAsync(archive, token).ConfigureAwait(false);
            diagnostics.AddRange(PayloadInventory.Compare(archived, receipt.Payload).Select(error => receipt.Rid + " archive: " + error));
        }
        return diagnostics.AsReadOnly();
    }
}
