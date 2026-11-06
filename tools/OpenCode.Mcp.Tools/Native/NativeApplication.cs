using System.ComponentModel;
using System.IO.Abstractions;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeApplication(IFileSystem files, INativeProcessRunner runner, INativeHostReader hosts, TextWriter output, TextWriter errors)
{
    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        try
        {
            var command = NativeArguments.Parse(arguments);
            var host = hosts.Read();
            var root = host.WorkingDirectory;
            var manifest = await new NativeManifestStore(files).ReadAsync(files.Path.GetFullPath(command.Manifest, root), token).ConfigureAwait(false);
            if (command.Operation == "validate")
            {
                var diagnostics = await new NativeReceiptValidator(files).ValidateAsync(manifest,
                    files.Path.GetFullPath(command.Receipts ?? throw new InvalidOperationException("Missing receipts directory."), root), token).ConfigureAwait(false);
                NativeDiagnostics.Refuse(diagnostics);
                await output.WriteLineAsync("Native receipt set is complete.".AsMemory(), token).ConfigureAwait(false);
                return 0;
            }
            var rid = command.Rid ?? throw new InvalidOperationException("Missing RID.");
            if (!manifest.Runtimes.Any(runtime => string.Equals(runtime.Rid, rid, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Manifest: unexpected RID " + rid + ".");
            }
            NativeDiagnostics.Refuse(NativePublishPolicy.HostDiagnostics(rid, host.Os, host.OsArchitecture, host.ProcessArchitecture));
            if (command.Operation == "inspect")
            {
                var processes = new NativeProcesses(runner, root);
                var toolchain = await new NativePrerequisites(files, processes).CheckAsync(rid, host, token).ConfigureAwait(false);
                var inspection = await new NativeInspector(files, processes).InspectAsync(rid,
                    files.Path.GetFullPath(command.Path ?? throw new InvalidOperationException("Missing inspection path."), root), toolchain, token).ConfigureAwait(false);
                await output.WriteLineAsync(inspection.AsMemory(), token).ConfigureAwait(false);
                return 0;
            }
            var publishDirectory = command.PublishDirectory is null ? null : files.Path.GetFullPath(command.PublishDirectory, root);
            var destination = publishDirectory is null ? files.Path.GetFullPath(command.Output ?? files.Path.Combine("artifacts", "native", rid), root)
                : files.Path.GetDirectoryName(publishDirectory) ?? throw new InvalidOperationException("Publish directory needs an evidence parent.");
            var request = new NativeBuildRequest(root, destination, rid, host, manifest);
            var receipt = publishDirectory is null ? await new NativeVerifier(files, runner).VerifyAsync(request, token).ConfigureAwait(false)
                : await new NativeStager(files, runner).StageAsync(request, publishDirectory, token).ConfigureAwait(false);
            await output.WriteLineAsync((files.Path.Combine(destination, "receipt.json") + "\nExecutable SHA-256: " + receipt.ExecutableSha256).AsMemory(), token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            await errors.WriteLineAsync(error.Message.AsMemory(), token).ConfigureAwait(false);
            return 2;
        }
    }
}
