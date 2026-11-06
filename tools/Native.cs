#!/usr/bin/env -S dotnet --
#:project ./OpenCode.Mcp.Tools/OpenCode.Mcp.Tools.csproj
#:property RootNamespace=OpenCode.Mcp.Tools
#:property PublishAot=false

namespace OpenCode.Mcp.Tools;

internal static class Native
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        using var interrupted = new SemaphoreSlim(0);
        ConsoleCancelEventHandler interrupt = (sender, signal) =>
        {
            // The signal thread records the request; callback failures surface on the main flow.
            signal.Cancel = true;
            _ = interrupted.Release();
        };
        Console.CancelKeyPress += interrupt;
        try
        {
            var run = OpenCode.Mcp.Tools.NativeToolApp.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
            if (await Task.WhenAny(run, interrupted.WaitAsync()).ConfigureAwait(false) != run)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }

            return await run.ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= interrupt;
        }
    }
}
