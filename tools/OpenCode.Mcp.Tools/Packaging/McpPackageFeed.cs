using System.IO.Abstractions;
using OpenCode.Mcp.Tools.Native;

namespace OpenCode.Mcp.Tools.Packaging;

internal sealed class McpPackageFeed(IFileSystem files)
{
    public async Task<IReadOnlyList<string>> ValidateAsync(string directory, NativeManifest manifest, PackageIdentity identity, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(identity);
        identity = identity with { RuntimeIdentifiers = [.. manifest.Runtimes.Select(static runtime => runtime.Rid)] };
        var packages = McpPackagePolicy.PackageNames(identity);
        var diagnostics = FileDiagnostics(directory, packages, manifest);
        if (diagnostics.Count != 0)
        {
            return diagnostics;
        }
        var receipts = new List<NativeReceipt>();
        var store = new NativeEvidenceStore(files);
        foreach (var runtime in manifest.Runtimes)
        {
            receipts.Add(await store.ReadAsync<NativeReceipt>(files.Path.Combine(directory, "evidence", runtime.Rid, "receipt.json"), token).ConfigureAwait(false));
        }
        var evidenceDiagnostics = new List<string>(NativeReceiptPolicy.Diagnostics(manifest, receipts));
        for (var index = 0; index < receipts.Count; index++)
        {
            var receipt = receipts[index];
            if (receipt.Version != identity.Version)
            {
                evidenceDiagnostics.Add(receipt.Rid + ": Receipt version must be " + identity.Version + ".");
            }
            var payload = await store.ReadAsync<PayloadFile[]>(files.Path.Combine(directory, "evidence", manifest.Runtimes[index].Rid, "payload-manifest.json"), token).ConfigureAwait(false);
            if (!receipt.Payload.SequenceEqual(payload))
            {
                evidenceDiagnostics.Add(receipt.Rid + ": Payload manifest differs from receipt.");
            }
        }
        if (evidenceDiagnostics.Count != 0)
        {
            return evidenceDiagnostics.AsReadOnly();
        }
        return await InspectPackagesAsync(directory, identity, receipts, token).ConfigureAwait(false);
    }

    private IReadOnlyList<string> FileDiagnostics(string directory, IReadOnlyList<string> packages, NativeManifest manifest)
    {
        string[] expected = [.. packages, .. manifest.Runtimes.SelectMany(static runtime => new[] { "evidence/" + runtime.Rid + "/receipt.json", "evidence/" + runtime.Rid + "/payload-manifest.json" })];
        var actual = files.Directory.Exists(directory) ? files.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => files.Path.GetRelativePath(directory, path).Replace('\\', '/')).ToArray() : [];
        return [.. expected.Except(actual, StringComparer.Ordinal).Select(static path => "Missing feed file: " + path),
            .. actual.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(static path => "Unexpected feed file: " + path)];
    }

    private async Task<IReadOnlyList<string>> InspectPackagesAsync(string directory, PackageIdentity identity, List<NativeReceipt> receipts, CancellationToken token)
    {
        var names = McpPackagePolicy.PackageNames(identity);
        var diagnostics = new List<string>();
        var hashes = new List<string>();
        for (var index = 0; index < names.Count; index++)
        {
            var name = names[index];
            try
            {
                var package = await new McpPackageInspector(files).InspectAsync(files.Path.Combine(directory, name), token).ConfigureAwait(false);
                var errors = index == 0 ? McpPackagePolicy.RootDiagnostics(package, identity, receipts[0].SourceCommit)
                    : McpPackagePolicy.ChildDiagnostics(package, identity, receipts[index - 1]);
                diagnostics.AddRange(errors.Select(error => name + ": " + error));
                hashes.Add(package.ManifestSha256);
            }
            catch (InvalidOperationException error)
            {
                diagnostics.Add(name + ": " + error.Message);
            }
        }
        if (hashes.Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            diagnostics.Add("Packed manifests must be byte-identical.");
        }
        return diagnostics.AsReadOnly();
    }
}
