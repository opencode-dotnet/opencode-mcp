using System.Diagnostics;
using System.Runtime.ExceptionServices;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class SystemNativeProcessRunner : INativeProcessRunner
{
    public async Task<NativeProcessResult> RunAsync(NativeProcessRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(request.FileName)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in request.Arguments)
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var entry in request.Environment)
        {
            start.Environment[entry.Key] = entry.Value;
        }
        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start " + request.FileName);
        }
        // Cleanup owns the exit and readers even after the command's token is cancelled.
        var exited = process.WaitForExitAsync(CancellationToken.None);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(request.Timeout);
        Exception? failure = null;
        try
        {
            await Task.WhenAll(exited, stdout, stderr).WaitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await Task.WhenAll(exited, stdout, stderr).WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception cleanup)
        {
            IEnumerable<Exception> failures = failure is null ? [cleanup] : [failure, cleanup];
            throw new AggregateException("Native command cleanup failed.", failures);
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
        return new NativeProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
