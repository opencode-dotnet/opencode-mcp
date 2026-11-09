using System.IO.Abstractions;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Packaging;

internal sealed class McpPackageBuilder(IFileSystem files, INativeProcessRunner runner, IUnixFileModes modes)
{
    public async Task<string> PackChildAsync(NativeBuildRequest request, CancellationToken token)
    {
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
        var identity = await RequireIdentityAsync(request, receipt, processes, token).ConfigureAwait(false);
        var payload = files.Path.Combine(request.Output, receipt.PayloadDirectory ?? throw new InvalidOperationException("Missing payload directory."));
        try
        {
            files.Directory.CreateDirectory(packages);
            var result = await processes.RunAsync("dotnet", ["pack", NativeProjectProperties.ProductProject, "-c", "Release", "-r", request.Rid, "--no-build", "--no-restore", "-p:IsPublishable=false",
                "-p:PublishDir=" + files.Path.TrimEndingDirectorySeparator(payload) + files.Path.DirectorySeparatorChar, "-p:ToolEntryPoint=" + new NativeRid(request.Rid).CommandName,
                "-o", packages, "-nodeReuse:false", "-p:UseSharedCompilation=false"], request.Host.Environment, token).ConfigureAwait(false);
            NativeDiagnostics.Refuse(McpPackagePolicy.PackDiagnostics(result));
            NativeDiagnostics.Refuse(await validator.ValidateArtifactsAsync(receipt, request.Output, token).ConfigureAwait(false));
            var name = identity.Id + "." + request.Rid + "." + identity.Version + ".nupkg";
            var paths = files.Directory.EnumerateFiles(packages, "*", SearchOption.AllDirectories).ToArray();
            var packagePath = files.Path.Combine(packages, name);
            if (paths.Length != 1 || paths[0] != packagePath)
            {
                throw new InvalidOperationException("Expected exactly one child package named " + name + ".");
            }
            var package = await new McpPackageInspector(files).InspectAsync(packagePath, token).ConfigureAwait(false);
            NativeDiagnostics.Refuse(McpPackagePolicy.ChildDiagnostics(package, identity, receipt));
            return packagePath;
        }
        catch (Exception refusal)
        {
            try
            {
                DeletePackages(packages);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(refusal, cleanup);
            }
            throw;
        }
    }

    private void DeletePackages(string packages)
    {
        if (files.Directory.Exists(packages))
        {
            files.Directory.Delete(packages, recursive: true);
        }
    }

    private async Task<PackageIdentity> RequireIdentityAsync(NativeBuildRequest request, NativeReceipt receipt, NativeProcesses processes, CancellationToken token)
    {
        var sdk = await processes.RequireAsync("dotnet", ["--version"], request.Host.Environment, token).ConfigureAwait(false);
        if (sdk != receipt.SdkVersion)
        {
            throw new InvalidOperationException("Child packing requires the receipt's executed SDK.");
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
}
