using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using Testably.Abstractions;

namespace OpenCode.Mcp.Tools.Tests.Native;

[NotInParallel("processes")]
public sealed class SystemNativeProcessRunnerTests
{
    [Test]
    [Timeout(30_000)]
    public async Task RunAsync_Should_Preserve_Both_Streams_And_A_Nonzero_Exit(CancellationToken token)
    {
        var files = new RealFileSystem();
        var windows = OperatingSystem.IsWindows();
        var command = windows ? "cmd.exe" : "/bin/sh";
        string[] arguments = windows ? ["/d", "/c", "echo output&echo error 1>&2&exit /b 7"] : ["-c", "printf 'output\\n'; printf 'error\\n' >&2; exit 7"];
        var request = new NativeProcessRequest(command, arguments, files.Path.GetTempPath(),
            new Dictionary<string, string?>(StringComparer.Ordinal), TimeSpan.FromSeconds(15));
        var result = await new SystemNativeProcessRunner().RunAsync(request, token);
        await Assert.That(result.StandardOutput.Trim()).IsEqualTo("output");
        await Assert.That(result.StandardError.Trim()).IsEqualTo("error");
        await Assert.That(result.ExitCode).IsEqualTo(7);
    }
}
