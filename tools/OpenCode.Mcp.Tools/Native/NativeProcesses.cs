using System.Globalization;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeProcesses(INativeProcessRunner runner, string root)
{
    public Task<NativeProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment, CancellationToken token, TimeSpan? timeout = null) =>
        runner.RunAsync(new NativeProcessRequest(fileName, arguments, root, environment,
            timeout ?? TimeSpan.FromMinutes(fileName == "dotnet" && arguments.Count != 0 && arguments[0] == "test" ? 30 : 35)), token);

    public async Task<string> RequireAsync(string fileName, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment, CancellationToken token, TimeSpan? timeout = null)
    {
        var result = await RunAsync(fileName, arguments, environment, token, timeout).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{fileName} {string.Join(' ', arguments)} exited {result.ExitCode}:\n{result.Output}"));
        }
        return result.StandardOutput.TrimEnd('\r', '\n');
    }
}
