using System.IO.Abstractions;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeRuntimeImageProbe(IFileSystem files, INativeProcessRunner runner)
{
    private readonly List<NativeContainerOwner> _owners = [];
    private const string Scan = "find / -xdev -type f \\( -name dotnet -o -name libhostfxr.so -o -name libhostpolicy.so -o -name libcoreclr.so \\) -print";

    public async Task<NativeRuntimeIndependence> ProbeAsync(NativeBuildRequest request, CancellationToken token)
    {
        var root = files.Path.Combine(files.Path.GetTempPath(), "opencode-mcp-image-" + Guid.NewGuid().ToString("N"));
        var environment = new NativeDockerEnvironment(files).Create(root, request.Host.Environment);
        var docker = new NativeProcessRequest("docker", [], root, environment, TimeSpan.FromMinutes(30)) { InheritEnvironment = false };
        NativeRuntimeIndependence? proof = null;
        Exception? failure = null;
        try
        {
            var image = request.Manifest.RuntimeImage;
            var pulled = await runner.RunAsync(docker with { Arguments = ["pull", image] }, token).ConfigureAwait(false);
            if (pulled.ExitCode != 0)
            {
                throw new InvalidOperationException("Runtime image pull failed:\n" + pulled.Output.Trim());
            }
            var inspection = await runner.RunAsync(docker with { Arguments = ["image", "inspect", image, "--format", "{{json .}}"] }, token).ConfigureAwait(false);
            await files.File.WriteAllTextAsync(files.Path.Combine(request.Output, "runtime-image-inspection.json"), inspection.Output, token).ConfigureAwait(false);
            var imageId = ReadImage(inspection, request.Rid);
            var lookup = await RunProbeAsync(docker, image, "command -v dotnet", token).ConfigureAwait(false);
            await files.File.WriteAllTextAsync(files.Path.Combine(request.Output, "dotnet-lookup.log"), lookup.Output, token).ConfigureAwait(false);
            if (lookup.ExitCode != 1 || !string.IsNullOrWhiteSpace(lookup.StandardOutput) || !string.IsNullOrWhiteSpace(lookup.StandardError))
            {
                throw new InvalidOperationException("Runtime image dotnet lookup did not prove absence.");
            }
            var scan = await RunProbeAsync(docker, image, Scan, token).ConfigureAwait(false);
            await files.File.WriteAllTextAsync(files.Path.Combine(request.Output, "dotnet-filesystem-scan.log"), scan.Output, token).ConfigureAwait(false);
            if (scan.ExitCode != 0 || !string.IsNullOrWhiteSpace(scan.StandardError))
            {
                throw new InvalidOperationException("Runtime image filesystem enumeration failed:\n" + scan.StandardError.Trim());
            }
            if (!string.IsNullOrWhiteSpace(scan.StandardOutput))
            {
                throw new InvalidOperationException("Runtime image contains .NET files:\n" + scan.StandardOutput.Trim());
            }
            proof = new NativeRuntimeIndependence("linux-clean-container", ImageReference: image, ImageId: imageId, DotnetAbsent: true);
        }
        catch (Exception error)
        {
            failure = error;
        }
        try
        {
            if (_owners.All(static owner => owner.AbsenceProven))
            {
                files.Directory.Delete(root, recursive: true);
            }
            else
            {
                throw new InvalidOperationException("Container absence is unresolved; retained root: " + root);
            }
        }
        catch (Exception cleanup)
        {
            failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
        return proof ?? throw new InvalidOperationException("Missing runtime image proof.");
    }

    private async Task<NativeProcessResult> RunProbeAsync(NativeProcessRequest docker, string image, string command, CancellationToken token)
    {
        var name = "opencode-mcp-test-" + Guid.NewGuid().ToString("N");
        var owner = new NativeContainerOwner(runner, docker, name);
        _owners.Add(owner);
        NativeProcessResult? result = null;
        Exception? original = null;
        try
        {
            result = await runner.RunAsync(docker with { Arguments = ["run", "--rm", "--name", name, image, "/bin/sh", "-c", command], Container = owner }, token).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            original = failure;
        }
        var failures = await owner.CleanupAsync(original).ConfigureAwait(false);
        if (failures.Count != 0)
        {
            throw new AggregateException("Runtime image probe cleanup failed.", failures);
        }
        return result ?? throw new InvalidOperationException("Runtime image probe returned no result.");
    }

    private static string ReadImage(NativeProcessResult inspection, string rid)
    {
        if (inspection.ExitCode != 0)
        {
            throw new InvalidOperationException("Runtime image inspection failed:\n" + inspection.Output.Trim());
        }
        using var document = JsonDocument.Parse(inspection.StandardOutput);
        var image = document.RootElement;
        var architecture = rid.EndsWith("-x64", StringComparison.Ordinal) ? "amd64" : "arm64";
        var id = image.GetProperty("Id").GetString();
        if (image.GetProperty("Os").GetString() != "linux" || image.GetProperty("Architecture").GetString() != architecture || string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("Runtime image must have a recorded ID and match " + rid + ".");
        }
        return id;
    }
}
