using System.IO.Abstractions;
using System.Text.Json;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeStager(IFileSystem files, INativeProcessRunner runner, IUnixFileModes modes)
{
    public async Task<NativeReceipt> StageAsync(NativeBuildRequest request, string publishDirectory, CancellationToken token)
    {
        var store = new NativeEvidenceStore(files);
        var receiptPath = files.Path.Combine(request.Output, "receipt.json");
        var receipt = await store.ReadAsync<NativeReceipt>(receiptPath, token).ConfigureAwait(false);
        var rid = new NativeRid(request.Rid);
        var assetsPath = files.Path.Combine(request.Root, rid.AssetsPath);
        if (await store.DigestAsync(assetsPath, token).ConfigureAwait(false) != receipt.AssetsSha256)
        {
            throw new InvalidOperationException("Resolved assets changed after verification.");
        }
        var comparison = rid.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (receipt.Rid != request.Rid || !string.Equals(files.Path.GetFullPath(publishDirectory), receipt.PublishDirectory, comparison)
            || !receipt.PublishedTests.Succeeded || receipt.PublishExitCode != 0 || receipt.Warnings.Count != 0
            || await store.DigestAsync(receipt.Executable, token).ConfigureAwait(false) != receipt.ExecutableSha256)
        {
            throw new InvalidOperationException("Staging requires matching successful verification evidence and executable digest.");
        }
        var processes = new NativeProcesses(runner, request.Root);
        var source = await new NativeSourceReader(files, processes).ReadAsync(request.Root, token).ConfigureAwait(false);
        if (source.Commit != receipt.SourceCommit || source.InputsSha256 != receipt.CommonInputsSha256)
        {
            throw new InvalidOperationException("Source or common build inputs changed after verification.");
        }
        receipt = receipt with { Payload = [], NormalizedTests = null, ExtractedTests = null, Archive = null, ArchiveSha256 = null };
        await store.WriteAsync(receiptPath, receipt, token).ConfigureAwait(false);
        var payload = await new NativePayloadNormalizer(files, modes).NormalizeAsync(request.Output, receipt, token).ConfigureAwait(false);
        await files.File.WriteAllBytesAsync(files.Path.Combine(payload, "LICENSE"), await files.File.ReadAllBytesAsync(files.Path.Combine(request.Root, "LICENSE"), token).ConfigureAwait(false), token).ConfigureAwait(false);
        var notices = await new NativeNoticeComposer(files).ComposeAsync(request.Root, receipt.Dependencies, token).ConfigureAwait(false);
        using var assets = JsonDocument.Parse(await files.File.ReadAllTextAsync(assetsPath, token).ConfigureAwait(false));
        notices += await new NativeDistributionNotices(files).ComposeAsync(assets.RootElement, request.Rid, token).ConfigureAwait(false);
        await files.File.WriteAllTextAsync(files.Path.Combine(payload, "THIRD-PARTY-NOTICES.md"), notices, token).ConfigureAwait(false);
        await store.WriteAsync(files.Path.Combine(payload, "payload.json"), new { receipt.Version, receipt.Rid, receipt.DependenciesSha256 }, token).ConfigureAwait(false);
        var commandName = rid.CommandName;
        var normalized = files.Path.Combine(payload, commandName);
        if (await store.DigestAsync(normalized, token).ConfigureAwait(false) != receipt.ExecutableSha256)
        {
            throw new InvalidOperationException("Normalized executable digest differs from verified publish.");
        }
        var inventory = new PayloadInventory(files, modes, rid);
        var manifest = await inventory.CaptureAsync(payload, token).ConfigureAwait(false);
        var manifestPath = files.Path.Combine(request.Output, "payload-manifest.json");
        await store.WriteAsync(manifestPath, manifest, token).ConfigureAwait(false);
        var tests = new NativeExecutableTests(files, processes, rid);
        var resultRoot = files.Path.Combine(request.Root, "artifacts", "test-results", "native", receipt.Rid);
        var normalizedTests = await tests.RunAsync(normalized, files.Path.Combine(resultRoot, "normalized"), token).ConfigureAwait(false);
        var archivePath = files.Path.Combine(request.Output, "opencode-mcp-" + receipt.Rid + rid.ArchiveExtension);
        var archive = new PayloadArchive(files, modes, rid);
        await archive.CreateAsync(payload, archivePath, manifest, token).ConfigureAwait(false);
        var extracted = files.Path.Combine(files.Path.GetTempPath(), "opencode-mcp-native-" + Guid.NewGuid().ToString("N"));
        NativeTestRun extractedTests;
        try
        {
            await archive.ExtractAsync(archivePath, extracted, token).ConfigureAwait(false);
            NativeDiagnostics.Refuse(await inventory.CompareAsync(extracted, manifest, token).ConfigureAwait(false));
            extractedTests = await tests.RunAsync(files.Path.Combine(extracted, commandName), files.Path.Combine(resultRoot, "extracted"), token).ConfigureAwait(false);
        }
        finally
        {
            if (files.Directory.Exists(extracted))
            {
                files.Directory.Delete(extracted, recursive: true);
            }
        }
        receipt = receipt with
        {
            PayloadDirectory = "payload",
            PayloadManifest = "payload-manifest.json",
            Payload = manifest,
            NormalizedTests = normalizedTests,
            ExtractedTests = extractedTests,
            Archive = files.Path.GetFileName(archivePath),
            ArchiveSha256 = await store.DigestAsync(archivePath, token).ConfigureAwait(false),
            RuntimeIndependence = rid.IsLinux ? null : "Matching-host execution and native AOT export inspection; not an SDK-free machine.",
        };
        await store.WriteAsync(receiptPath, receipt, token).ConfigureAwait(false);
        return receipt;
    }
}
