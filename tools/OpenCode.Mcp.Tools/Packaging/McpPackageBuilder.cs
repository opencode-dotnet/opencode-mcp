using System.IO.Abstractions;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Packaging;

internal sealed class McpPackageBuilder(IFileSystem files, INativeProcessRunner runner, IUnixFileModes modes)
{
    public async Task PackRootAsync(NativeBuildRequest request, string feedDirectory, TextWriter output, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        NativeDiagnostics.Refuse(await new NativeReceiptValidator(files, modes).ValidateAsync(request.Manifest, request.Output, token).ConfigureAwait(false));
        var locations = await ReadLocationsAsync(request.Output, token).ConfigureAwait(false);
        var first = locations[request.Manifest.Runtimes[0].Rid].Receipt;
        var processes = new NativeProcesses(runner, request.Root);
        var identity = await RequireIdentityAsync(request, first, processes, "Root", token).ConfigureAwait(false);
        await RequireChildrenAsync(request.Manifest, identity, locations, token).ConfigureAwait(false);
        if (files.File.Exists(feedDirectory) || (files.Directory.Exists(feedDirectory) && files.Directory.EnumerateFileSystemEntries(feedDirectory).Any()))
        {
            throw new InvalidOperationException("Feed directory must be absent or empty: " + feedDirectory);
        }
        await BuildInOwnedDirectoryAsync(feedDirectory, async () =>
        {
            var result = await processes.RunAsync("dotnet", ["pack", NativeProjectProperties.ProductProject, "-c", "Release", "-o", feedDirectory,
                "-nodeReuse:false", "-p:UseSharedCompilation=false"], request.Host.Environment, token).ConfigureAwait(false);
            NativeDiagnostics.Refuse(McpPackagePolicy.PackDiagnostics(result, "Root"));
            CopyEvidence(feedDirectory, identity, locations);
            NativeDiagnostics.Refuse(await new McpPackageFeed(files).ValidateAsync(feedDirectory, request.Manifest, identity, token).ConfigureAwait(false));
            var paths = McpPackagePolicy.PackageNames(identity).Select(name => files.Path.Combine(feedDirectory, name)).ToArray();
            await output.WriteLineAsync(feedDirectory.AsMemory(), token).ConfigureAwait(false);
            foreach (var path in paths)
            {
                var digest = await new NativeEvidenceStore(files).DigestAsync(path, token).ConfigureAwait(false);
                await output.WriteLineAsync((files.Path.GetFileName(path) + " SHA-256: " + digest).AsMemory(), token).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    public async Task<string> PackChildAsync(NativeBuildRequest request, CancellationToken token)
    {
        if (!files.File.Exists(files.Path.Combine(request.Output, ".opencode-mcp-native")))
        {
            throw new InvalidOperationException("Output directory is not owned by the native tool: " + request.Output);
        }
        var packages = files.Path.Combine(request.Output, "packages");
        DeletePackages(packages);
        var store = new NativeEvidenceStore(files);
        var receipt = await store.ReadAsync<NativeReceipt>(files.Path.Combine(request.Output, "receipt.json"), token).ConfigureAwait(false);
        if (receipt.Rid != request.Rid)
        {
            throw new InvalidOperationException("Child packing requires a receipt for " + request.Rid + ".");
        }
        var validator = new NativeReceiptValidator(files, modes);
        NativeDiagnostics.Refuse(await validator.ValidateArtifactsAsync(receipt, request.Output, token).ConfigureAwait(false));
        var processes = new NativeProcesses(runner, request.Root);
        var identity = await RequireIdentityAsync(request, receipt, processes, "Child", token).ConfigureAwait(false);
        var payload = files.Path.Combine(request.Output, receipt.PayloadDirectory ?? throw new InvalidOperationException("Missing payload directory."));
        var name = McpPackagePolicy.PackageNames(identity with { RuntimeIdentifiers = [request.Rid] })[1];
        var packagePath = files.Path.Combine(packages, name);
        await BuildInOwnedDirectoryAsync(packages, async () =>
        {
            var result = await processes.RunAsync("dotnet", ["pack", NativeProjectProperties.ProductProject, "-c", "Release", "-r", request.Rid, "--no-build", "--no-restore", "-p:IsPublishable=false",
                "-p:PublishDir=" + files.Path.TrimEndingDirectorySeparator(payload) + files.Path.DirectorySeparatorChar, "-p:ToolEntryPoint=" + new NativeRid(request.Rid).CommandName,
                "-o", packages, "-nodeReuse:false", "-p:UseSharedCompilation=false"], request.Host.Environment, token).ConfigureAwait(false);
            NativeDiagnostics.Refuse(McpPackagePolicy.PackDiagnostics(result));
            NativeDiagnostics.Refuse(await validator.ValidateArtifactsAsync(receipt, request.Output, token).ConfigureAwait(false));
            RequireSinglePackage(packages, name);
            var package = await new McpPackageInspector(files).InspectAsync(packagePath, token).ConfigureAwait(false);
            NativeDiagnostics.Refuse(McpPackagePolicy.ChildDiagnostics(package, identity, receipt));
        }).ConfigureAwait(false);
        return packagePath;
    }

    private void DeletePackages(string packages)
    {
        if (files.Directory.Exists(packages))
        {
            files.Directory.Delete(packages, recursive: true);
        }
    }

    private async Task<PackageIdentity> RequireIdentityAsync(NativeBuildRequest request, NativeReceipt receipt, NativeProcesses processes, string kind, CancellationToken token)
    {
        var sdk = await processes.RequireAsync("dotnet", ["--version"], request.Host.Environment, token).ConfigureAwait(false);
        if (sdk != receipt.SdkVersion)
        {
            throw new InvalidOperationException(kind + " packing requires the receipt's executed SDK.");
        }
        var source = await new NativeSourceReader(files, processes).ReadAsync(request.Root, token).ConfigureAwait(false);
        if (source.Commit != receipt.SourceCommit || source.InputsSha256 != receipt.CommonInputsSha256)
        {
            throw new InvalidOperationException("Source or common build inputs changed after verification.");
        }
        var identity = await new PackageIdentityReader(processes).ReadAsync(token).ConfigureAwait(false);
        if (!identity.RuntimeIdentifiers.SequenceEqual(request.Manifest.Runtimes.Select(static runtime => runtime.Rid), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("ToolPackageRuntimeIdentifiers must match the native manifest in order.");
        }
        return identity;
    }

    private async Task<Dictionary<string, (NativeReceipt Receipt, string Directory)>> ReadLocationsAsync(string directory, CancellationToken token)
    {
        var locations = new Dictionary<string, (NativeReceipt Receipt, string Directory)>(StringComparer.Ordinal);
        foreach (var path in files.Directory.EnumerateFiles(directory, "receipt.json", SearchOption.AllDirectories))
        {
            var receipt = await new NativeEvidenceStore(files).ReadAsync<NativeReceipt>(path, token).ConfigureAwait(false);
            locations.Add(receipt.Rid, (receipt, files.Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Receipt needs a parent directory.")));
        }
        return locations;
    }

    private async Task RequireChildrenAsync(NativeManifest manifest, PackageIdentity identity, Dictionary<string, (NativeReceipt Receipt, string Directory)> locations, CancellationToken token)
    {
        var diagnostics = new List<string>();
        var names = McpPackagePolicy.PackageNames(identity);
        for (var index = 0; index < manifest.Runtimes.Count; index++)
        {
            var rid = manifest.Runtimes[index].Rid;
            var location = locations[rid];
            var packages = files.Path.Combine(location.Directory, "packages");
            var name = names[index + 1];
            var path = files.Path.Combine(packages, name);
            try
            {
                RequireSinglePackage(packages, name);
                var package = await new McpPackageInspector(files).InspectAsync(path, token).ConfigureAwait(false);
                diagnostics.AddRange(McpPackagePolicy.ChildDiagnostics(package, identity, location.Receipt).Select(error => rid + ": " + error));
            }
            catch (InvalidOperationException error)
            {
                diagnostics.Add(rid + ": " + error.Message);
            }
        }
        NativeDiagnostics.Refuse(diagnostics);
    }

    private void CopyEvidence(string feed, PackageIdentity identity, Dictionary<string, (NativeReceipt Receipt, string Directory)> locations)
    {
        var names = McpPackagePolicy.PackageNames(identity);
        for (var index = 0; index < identity.RuntimeIdentifiers.Count; index++)
        {
            var rid = identity.RuntimeIdentifiers[index];
            var directory = locations[rid].Directory;
            var name = names[index + 1];
            files.File.Copy(files.Path.Combine(directory, "packages", name), files.Path.Combine(feed, name));
            var evidence = files.Path.Combine(feed, "evidence", rid);
            files.Directory.CreateDirectory(evidence);
            foreach (var file in new[] { "receipt.json", "payload-manifest.json" })
            {
                files.File.Copy(files.Path.Combine(directory, file), files.Path.Combine(evidence, file));
            }
        }
    }

    private void RequireSinglePackage(string directory, string name)
    {
        var entries = files.Directory.Exists(directory) ? files.Directory.EnumerateFileSystemEntries(directory).ToArray() : [];
        if (entries.Length != 1 || entries[0] != files.Path.Combine(directory, name))
        {
            throw new InvalidOperationException("Expected exactly one child package named " + name + ".");
        }
    }

    private async Task BuildInOwnedDirectoryAsync(string directory, Func<Task> build)
    {
        try
        {
            files.Directory.CreateDirectory(directory);
            await build().ConfigureAwait(false);
        }
        catch (Exception refusal)
        {
            try
            {
                DeletePackages(directory);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(refusal, cleanup);
            }
            throw;
        }
    }
}
