using System.ComponentModel;
using System.IO.Abstractions;
using System.Text.Json;
using System.Xml;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeApplication(IFileSystem files, INativeProcessRunner runner, INativeHostReader hosts, IUnixFileModes modes, TextWriter output, TextWriter errors, TimeProvider? clock = null)
{
    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        try
        {
            var command = NativeArguments.Parse(arguments);
            var host = hosts.Read();
            var root = host.WorkingDirectory;
            if (command.Operation == "snapshot")
            {
                await new NativeProcessSnapshot(files, runner, root, output).RecordAsync(
                    files.Path.GetFullPath(command.Output ?? throw new InvalidOperationException("Missing snapshot output."), root), (clock ?? TimeProvider.System).GetUtcNow, token).ConfigureAwait(false);
                return 0;
            }
            var manifest = await new NativeManifestStore(files).ReadAsync(files.Path.GetFullPath(command.Manifest, root), token).ConfigureAwait(false);
            if (command.Operation == "matrix")
            {
                await new NativeMatrix(files).WriteAsync(manifest, files.Path.GetFullPath(command.GithubOutput ?? throw new InvalidOperationException("Missing GitHub output."), root), token).ConfigureAwait(false);
                return 0;
            }
            if (command.Operation == "validate")
            {
                var diagnostics = await new NativeReceiptValidator(files, modes).ValidateAsync(manifest,
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
            var destination = publishDirectory is null ? files.Path.GetFullPath(command.Evidence ?? command.Output ?? files.Path.Combine("artifacts", "native", rid), root)
                : files.Path.GetDirectoryName(publishDirectory) ?? throw new InvalidOperationException("Publish directory needs an evidence parent.");
            var request = new NativeBuildRequest(root, destination, rid, host, manifest);
            if (command.Operation == "control")
            {
                var refusal = await new NativeFrameworkDependentControl(files, runner).VerifyAsync(request, token).ConfigureAwait(false);
                await output.WriteLineAsync(("Framework-dependent control refused: " + refusal).AsMemory(), token).ConfigureAwait(false);
                return 0;
            }
            var receipt = command.Operation switch
            {
                "verify" => await new NativeVerifier(files, runner).VerifyAsync(request, token).ConfigureAwait(false),
                "prove-linux" => await new NativeLinuxProof(files, runner, modes).ProveAsync(request, token).ConfigureAwait(false),
                _ => await new NativeStager(files, runner, modes).StageAsync(request, publishDirectory ?? throw new InvalidOperationException("Missing publish directory."), token).ConfigureAwait(false),
            };
            await output.WriteLineAsync((files.Path.Combine(destination, "receipt.json") + "\nExecutable SHA-256: " + receipt.ExecutableSha256).AsMemory(), token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception
            or OperationCanceledException or JsonException or FormatException or KeyNotFoundException or AggregateException or XmlException or InvalidDataException)
        {
            var diagnostic = error switch
            {
                OperationCanceledException => token.IsCancellationRequested ? "Native command cancelled (Ctrl+C)." : "Native command timed out.",
                JsonException => "Malformed JSON input: " + error.Message,
                FormatException => "Malformed toolchain or assets input: " + error.Message,
                KeyNotFoundException => "Missing toolchain or assets field: " + error.Message,
                AggregateException aggregate => "Native command cleanup failed:\n" + string.Join('\n', aggregate.InnerExceptions.Select(static failure => failure.GetType().Name + ": " + failure.Message)),
                XmlException => "Malformed TRX input: " + error.Message,
                InvalidDataException => "Invalid archive input: " + error.Message,
                _ => error.Message,
            };
            // A cancellation refusal must still reach stderr after the caller cancels.
            await errors.WriteLineAsync(diagnostic.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return 2;
        }
    }
}
