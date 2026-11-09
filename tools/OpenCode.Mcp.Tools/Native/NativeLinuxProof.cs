using System.IO.Abstractions;
using System.Runtime.ExceptionServices;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeLinuxProof(IFileSystem files, INativeProcessRunner runner, IUnixFileModes modes)
{
    public async Task<NativeReceipt> ProveAsync(NativeBuildRequest request, CancellationToken token)
    {
        if (!new NativeRid(request.Rid).IsLinux)
        {
            throw new InvalidOperationException("prove-linux requires a Linux RID.");
        }
        NativeDiagnostics.Refuse(NativePublishPolicy.HostDiagnostics(request.Rid, request.Host.Os, request.Host.OsArchitecture, request.Host.ProcessArchitecture));
        var store = new NativeEvidenceStore(files);
        var receiptPath = files.Path.Combine(request.Output, "receipt.json");
        var receipt = await store.ReadAsync<NativeReceipt>(receiptPath, token).ConfigureAwait(false);
        receipt = receipt with { RuntimeIndependence = null, GlibcFloor = null };
        await store.WriteAsync(receiptPath, receipt, token).ConfigureAwait(false);
        if (receipt.Rid != request.Rid)
        {
            throw new InvalidOperationException("Downloaded receipt RID differs from requested RID.");
        }
        NativeDiagnostics.Refuse(NativeReceiptPolicy.EntryDiagnostics(receipt, request.Manifest));
        NativeDiagnostics.Refuse(await new NativeReceiptValidator(files, modes).ValidateArtifactsAsync(receipt, request.Output, token).ConfigureAwait(false));
        var processes = new NativeProcesses(runner, request.Root);
        await RequireIdentityAsync(request, receipt, processes, token).ConfigureAwait(false);
        var extracted = files.Path.Combine(files.Path.GetTempPath(), "opencode-mcp-native-" + Guid.NewGuid().ToString("N"));
        Exception? failure = null;
        try
        {
            var rid = new NativeRid(request.Rid);
            await new PayloadArchive(files, modes, rid).ExtractAsync(files.Path.Combine(request.Output, receipt.Archive ?? throw new InvalidOperationException("Missing archive.")), extracted, token).ConfigureAwait(false);
            NativeDiagnostics.Refuse(await new PayloadInventory(files, modes, rid).CompareAsync(extracted, receipt.Payload, token).ConfigureAwait(false));
            var proof = await new NativeRuntimeImageProbe(files, runner).ProbeAsync(request, token).ConfigureAwait(false);
            var executable = files.Path.Combine(extracted, rid.CommandName);
            var results = files.Path.Combine(request.Root, "artifacts", "test-results", "native", request.Rid, "container");
            var tests = await new NativeExecutableTests(files, processes, rid).RunAsync(executable, results, request.Manifest.RuntimeImage, token).ConfigureAwait(false);
            var tool = await processes.RunAsync("objdump", ["--version"], request.Host.Environment, token).ConfigureAwait(false);
            var symbols = await processes.RunAsync("objdump", ["-T", executable], request.Host.Environment, token).ConfigureAwait(false);
            await files.File.WriteAllTextAsync(files.Path.Combine(request.Output, "objdump-version.log"), tool.Output, token).ConfigureAwait(false);
            await files.File.WriteAllTextAsync(files.Path.Combine(request.Output, "glibc-symbols.log"), symbols.Output, token).ConfigureAwait(false);
            receipt = receipt with { RuntimeIndependence = proof with { Tests = tests }, GlibcFloor = NativeGlibcPolicy.Measure(tool, symbols) };
        }
        catch (Exception error)
        {
            failure = error;
        }
        try
        {
            if (files.Directory.Exists(extracted))
            {
                files.Directory.Delete(extracted, recursive: true);
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
        await store.WriteAsync(receiptPath, receipt, token).ConfigureAwait(false);
        return receipt;
    }

    private async Task RequireIdentityAsync(NativeBuildRequest request, NativeReceipt receipt, NativeProcesses processes, CancellationToken token)
    {
        var sdk = await processes.RequireAsync("dotnet", ["--version"], request.Host.Environment, token).ConfigureAwait(false);
        var sdkInfo = await processes.RequireAsync("dotnet", ["--info"], request.Host.Environment, token).ConfigureAwait(false);
        NativeDiagnostics.Refuse(NativePublishPolicy.SdkArchitectureDiagnostics(sdkInfo, request.Host.ProcessArchitecture));
        var source = await new NativeSourceReader(files, processes).ReadAsync(request.Root, token).ConfigureAwait(false);
        if (sdk != receipt.SdkVersion || source.Commit != receipt.SourceCommit || source.InputsSha256 != receipt.CommonInputsSha256)
        {
            throw new InvalidOperationException("Linux proof requires matching source, common inputs and executed SDK.");
        }
    }
}
