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
        ConfigureStart(start, request);
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
            if (request.Container is null)
            {
                await Task.WhenAll(exited, stdout, stderr).WaitAsync(bound.Token).ConfigureAwait(false);
            }
            else
            {
                await exited.WaitAsync(bound.Token).ConfigureAwait(false);
            }
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
            var completion = request.Container is null ? Task.WhenAll(exited, stdout, stderr) : exited;
            await completion.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception cleanup)
        {
            if (request.Container is null)
            {
                IEnumerable<Exception> failures = failure is null ? [cleanup] : [failure, cleanup];
                throw new AggregateException("Native command cleanup failed.", failures);
            }
            failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
        }
        if (request.Container is { } container)
        {
            var failures = await container.CleanupAsync(failure).ConfigureAwait(false);
            failure = failures.Count == 0 ? null : new AggregateException("Native container command failed.", failures);
            try
            {
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
            }
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
        return new NativeProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static void ConfigureStart(ProcessStartInfo start, NativeProcessRequest request)
    {
        foreach (var argument in request.Arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (!request.InheritEnvironment)
        {
            start.Environment.Clear();
        }
        foreach (var entry in request.Environment)
        {
            start.Environment[entry.Key] = entry.Value;
        }
    }
}
