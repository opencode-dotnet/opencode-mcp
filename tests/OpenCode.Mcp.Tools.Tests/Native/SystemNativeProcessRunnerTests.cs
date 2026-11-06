using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using Testably.Abstractions;

namespace OpenCode.Mcp.Tools.Tests.Native;

[NotInParallel("processes")]
public sealed class SystemNativeProcessRunnerTests
{
    [Test]
    [Timeout(30_000)]
    public async Task RunAsync_Should_Cancel_And_End_The_Owned_Child_Before_It_Completes(CancellationToken token)
    {
        var files = new RealFileSystem();
        var root = files.Path.Combine(files.Path.GetTempPath(), "opencode-mcp-native-timeout-" + Guid.NewGuid().ToString("N"));
        files.Directory.CreateDirectory(root);
        var completed = files.Path.Combine(root, "completed");
        var windows = OperatingSystem.IsWindows();
        var command = windows ? files.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/bin/sh";
        string[] arguments = windows ? ["/d", "/c", "ping -n 3 127.0.0.1 >nul & echo completed > completed"] : ["-c", "sleep 2; printf completed > completed"];
        var request = new NativeProcessRequest(command, arguments, root, new Dictionary<string, string?>(StringComparer.Ordinal), TimeSpan.FromMilliseconds(50));
        try
        {
            await Assert.That(async () => await new SystemNativeProcessRunner().RunAsync(request, token)).Throws<OperationCanceledException>();
            await Assert.That(files.File.Exists(completed)).IsFalse();
        }
        finally
        {
            files.Directory.Delete(root, recursive: true);
        }
    }

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
