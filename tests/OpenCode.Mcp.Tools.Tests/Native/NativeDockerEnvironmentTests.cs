using System.Diagnostics;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using Testably.Abstractions;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

[NotInParallel("processes")]
public sealed class NativeDockerEnvironmentTests
{
    [Test]
    public async Task Create_Should_Refuse_Nonempty_Owned_Configuration()
    {
        var files = new MockFileSystem();
        var root = files.Path.Combine(files.Path.GetTempPath(), "opencode-mcp-docker-fixture");
        var config = files.Path.Combine(root, "docker-config");
        files.Directory.CreateDirectory(config);
        files.Initialize().WithFile(files.Path.Combine(config, "config.json"));
        await Assert.That(() => new NativeDockerEnvironment(files).Create(root, new Dictionary<string, string?>(StringComparer.Ordinal) { ["PATH"] = "host-path" }))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Owned Docker configuration must be empty: " + config, StringComparison.Ordinal);
    }

    [Test]
    public async Task Create_Should_Keep_Only_Launch_Necessities_And_Own_Empty_Cli_Configuration()
    {
        var files = new MockFileSystem();
        var root = files.Path.Combine(files.Path.GetTempPath(), "opencode-mcp-docker-fixture");
        var environment = new NativeDockerEnvironment(files).Create(root, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = "host-path",
            ["DOTNET_ROOT"] = "host-runtime",
            ["DOCKER_HOST"] = "remote",
            ["DOCKER_CONFIG"] = "credentials",
            ["HTTP_PROXY"] = "proxy",
            ["DOTNET_STARTUP_HOOKS"] = "hook",
            ["HOME"] = "personal",
            ["SECRET"] = "synthetic",
        });
        await Assert.That(environment.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(["DOCKER_CONFIG", "DOTNET_ROOT", "HOME", "LANG", "LC_ALL", "PATH"], CollectionOrdering.Matching);
        await Assert.That(environment["PATH"]).IsEqualTo("host-path");
        await Assert.That(environment["DOTNET_ROOT"]).IsEqualTo("host-runtime");
        await Assert.That(environment["LANG"]).IsEqualTo("C");
        await Assert.That(environment["LC_ALL"]).IsEqualTo("C");
        await Assert.That(environment["HOME"]).IsEqualTo(files.Path.Combine(root, "docker-home"));
        var config = await Assert.That(environment["DOCKER_CONFIG"]).IsNotNull();
        await Assert.That(config).IsEqualTo(files.Path.Combine(root, "docker-config"));
        await Assert.That(files.Directory.EnumerateFileSystemEntries(config)).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    public async Task RunAsync_Should_Clear_Inherited_Environment_When_The_Request_Opts_Out(CancellationToken token)
    {
        var files = new RealFileSystem();
        var command = OperatingSystem.IsWindows() ? files.Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows() ? ["/d", "/c", "echo %PATH%"] : ["-c", "printf '%s' \"$PATH\""];
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var request = new NativeProcessRequest(command, arguments, files.Path.GetTempPath(), environment, TimeSpan.FromSeconds(15)) { InheritEnvironment = false };
        var result = await new SystemNativeProcessRunner().RunAsync(request, token);
        await Assert.That(result.StandardOutput.Trim()).IsNotEqualTo(new ProcessStartInfo().Environment["PATH"]);
        var inherited = await new SystemNativeProcessRunner().RunAsync(request with { InheritEnvironment = true }, token);
        await Assert.That(inherited.StandardOutput.Trim()).IsEqualTo(new ProcessStartInfo().Environment["PATH"]);
    }
}
