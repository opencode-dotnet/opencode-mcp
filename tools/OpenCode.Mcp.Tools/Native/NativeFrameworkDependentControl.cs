using System.IO.Abstractions;
using System.Runtime.ExceptionServices;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeFrameworkDependentControl(IFileSystem files, INativeProcessRunner runner)
{
    public async Task<string> VerifyAsync(NativeBuildRequest request, CancellationToken token)
    {
        NativeDiagnostics.Refuse(NativePublishPolicy.HostDiagnostics(request.Rid, request.Host.Os, request.Host.OsArchitecture, request.Host.ProcessArchitecture));
        var processes = new NativeProcesses(runner, request.Root);
        var toolchain = await new NativePrerequisites(files, processes).CheckAsync(request.Rid, request.Host, token).ConfigureAwait(false);
        var root = files.Path.Combine(files.Path.GetTempPath(), "opencode-mcp-control-" + Guid.NewGuid().ToString("N"));
        var publish = files.Path.Combine(root, "publish");
        files.Directory.CreateDirectory(root);
        Exception? original = null;
        string? refusal = null;
        try
        {
            // Isolate restore and intermediate files too: the control must not invalidate native staging evidence.
            await processes.RequireAsync("dotnet", ["publish", NativeProjectProperties.ProductProject, "-c", "Release", "-r", request.Rid,
                "--self-contained", "false", "-p:PublishAot=false", "--artifacts-path", files.Path.Combine(root, "artifacts"), "-o", publish,
                "-nodeReuse:false", "-p:UseSharedCompilation=false"], toolchain.PublishEnvironment, token).ConfigureAwait(false);
            var executable = files.Path.Combine(publish, new NativeRid(request.Rid).PublishedName);
            var expected = "Missing native export: " + (new NativeRid(request.Rid).IsMacOS ? "_DotNetRuntimeDebugHeader" : "DotNetRuntimeDebugHeader");
            try
            {
                await new NativeInspector(files, processes).InspectAsync(request.Rid, executable, toolchain, token).ConfigureAwait(false);
            }
            catch (InvalidOperationException failure) when (string.Equals(failure.Message, expected, StringComparison.Ordinal))
            {
                refusal = expected;
            }
            if (refusal is null)
            {
                throw new InvalidOperationException("Framework-dependent control unexpectedly passed native inspection.");
            }
        }
        catch (Exception failure)
        {
            original = failure;
        }
        try
        {
            files.Directory.Delete(root, recursive: true);
        }
        catch (Exception cleanup)
        {
            original = original is null ? cleanup : new AggregateException(original, cleanup);
        }
        if (original is not null)
        {
            ExceptionDispatchInfo.Throw(original);
        }
        return refusal ?? throw new InvalidOperationException("Missing framework-dependent control refusal.");
    }
}
