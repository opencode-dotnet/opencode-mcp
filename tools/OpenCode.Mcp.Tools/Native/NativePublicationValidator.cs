using System.IO.Abstractions;
using System.Text;
using OpenCode.Mcp.Tools.Native.Abstractions;
using OpenCode.Mcp.Tools.Packaging;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativePublicationValidator(IFileSystem files, IUnixFileModes modes)
{
    public async Task<IReadOnlyList<string>> ValidateAsync(NativePublicationRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = await new NativeReceiptValidator(files, modes).ValidateAsync(request.Manifest, request.Receipts, token).ConfigureAwait(false);
        if (diagnostics.Count != 0)
        {
            return diagnostics;
        }
        var locations = await new NativeEvidenceStore(files).ReadReceiptLocationsAsync(request.Receipts, token).ConfigureAwait(false);
        var byRid = locations.ToDictionary(static location => location.Receipt.Rid, StringComparer.Ordinal);
        var ordered = request.Manifest.Runtimes.Select(runtime => byRid[runtime.Rid]).ToArray();
        diagnostics = IdentityDiagnostics(request, ordered);
        if (diagnostics.Count != 0)
        {
            return diagnostics;
        }
        var identity = new PackageIdentity("OpenCodeDotNet.Mcp", request.Version, "opencode-mcp", [.. request.Manifest.Runtimes.Select(static runtime => runtime.Rid)]);
        var feedDiagnostics = new List<string>(await new McpPackageFeed(files).ValidateAsync(request.Feed, request.Manifest, identity, token).ConfigureAwait(false));
        feedDiagnostics.AddRange(await CompareFeedEvidenceAsync(request, ordered, token).ConfigureAwait(false));
        return feedDiagnostics.Count != 0 ? feedDiagnostics.AsReadOnly() : await ValidateAssetsAsync(request, ordered, token).ConfigureAwait(false);
    }

    private static string[] IdentityDiagnostics(NativePublicationRequest request, NativeReceiptLocation[] ordered)
    {
        var diagnostics = new List<string>();
        foreach (var receipt in ordered.Select(static location => location.Receipt))
        {
            if (!string.Equals(receipt.Version, request.Version, StringComparison.Ordinal))
            {
                diagnostics.Add(receipt.Rid + ": Receipt version must be " + request.Version + ".");
            }
            if (!string.Equals(receipt.SourceCommit, request.Commit, StringComparison.Ordinal))
            {
                diagnostics.Add(receipt.Rid + ": Receipt source commit must be " + request.Commit + ".");
            }
            if (receipt.SourceStatus.Length != 0)
            {
                diagnostics.Add(receipt.Rid + ": Candidate receipt source status must be empty.");
            }
        }
        return [.. diagnostics];
    }

    private async Task<IReadOnlyList<string>> CompareFeedEvidenceAsync(NativePublicationRequest request, NativeReceiptLocation[] ordered, CancellationToken token)
    {
        var diagnostics = new List<string>();
        foreach (var location in ordered)
        {
            foreach (var name in new[] { "receipt.json", "payload-manifest.json" })
            {
                var retainedPath = files.Path.Combine(request.Feed, "evidence", location.Receipt.Rid, name);
                if (!files.File.Exists(retainedPath))
                {
                    continue;
                }
                var completed = await files.File.ReadAllBytesAsync(files.Path.Combine(location.Directory, name), token).ConfigureAwait(false);
                var retained = await files.File.ReadAllBytesAsync(retainedPath, token).ConfigureAwait(false);
                if (!completed.SequenceEqual(retained))
                {
                    diagnostics.Add(location.Receipt.Rid + ": Feed " + (name == "receipt.json" ? "receipt" : "payload manifest") + " differs from completed evidence.");
                }
            }
        }
        return diagnostics.AsReadOnly();
    }

    private async Task<IReadOnlyList<string>> ValidateAssetsAsync(NativePublicationRequest request, NativeReceiptLocation[] ordered, CancellationToken token)
    {
        IReadOnlyList<NativeInstallerAsset> installers;
        try
        {
            installers = await new NativeInstallerAssets(files).ReadAsync(request.Installers, request.Version, token).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            return error.Message.Split('\n');
        }
        var names = ordered.Select(location => "opencode-mcp-" + request.Version + "-" + location.Receipt.Rid + new NativeRid(location.Receipt.Rid).ArchiveExtension).ToArray();
        var diagnostics = new List<string>(AssetSetDiagnostics(request.Assets, [.. names, .. installers.Select(static installer => installer.Name), "SHA256SUMS"]));
        var store = new NativeEvidenceStore(files);
        var checksums = new StringBuilder();
        for (var index = 0; index < ordered.Length; index++)
        {
            var receipt = ordered[index].Receipt;
            var path = files.Path.Combine(request.Assets, names[index]);
            if (files.File.Exists(path) && !string.Equals(await store.DigestAsync(path, token).ConfigureAwait(false), receipt.ArchiveSha256, StringComparison.Ordinal))
            {
                diagnostics.Add(receipt.Rid + ": Release archive digest differs from receipt.");
            }
            checksums.Append(receipt.ArchiveSha256).Append("  ").Append(names[index]).Append('\n');
        }
        foreach (var installer in installers)
        {
            var path = files.Path.Combine(request.Assets, installer.Name);
            if (files.File.Exists(path) && !(await files.File.ReadAllBytesAsync(path, token).ConfigureAwait(false)).SequenceEqual(installer.Bytes))
            {
                diagnostics.Add(installer.Name + ": Release script differs from committed installer.");
            }
            checksums.Append(installer.Sha256).Append("  ").Append(installer.Name).Append('\n');
        }
        var checksumPath = files.Path.Combine(request.Assets, "SHA256SUMS");
        if (files.File.Exists(checksumPath) && !(await files.File.ReadAllBytesAsync(checksumPath, token).ConfigureAwait(false)).SequenceEqual(Encoding.UTF8.GetBytes(checksums.ToString())))
        {
            diagnostics.Add("SHA256SUMS differs from the ordered release checksums.");
        }
        return diagnostics.AsReadOnly();
    }

    private IReadOnlyList<string> AssetSetDiagnostics(string directory, string[] expected)
    {
        var actual = files.Directory.Exists(directory) ? files.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => files.Path.GetRelativePath(directory, path).Replace(files.Path.DirectorySeparatorChar, '/')).ToArray() : [];
        var directories = files.Directory.Exists(directory) ? files.Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
            .Select(path => files.Path.GetRelativePath(directory, path).Replace(files.Path.DirectorySeparatorChar, '/')).ToArray() : [];
        return [.. expected.Except(actual, StringComparer.Ordinal).Select(static name => "Missing release asset: " + name),
            .. actual.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(static name => "Unexpected release asset: " + name),
            .. directories.Order(StringComparer.Ordinal).Select(static name => "Unexpected release directory: " + name)];
    }
}
