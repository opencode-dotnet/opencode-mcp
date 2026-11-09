using NSubstitute;
using OpenCode.Mcp.Tools.Native.Abstractions;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Support;

public sealed class ContainerLaunchBuilderTests
{
    private readonly MockFileSystem _files = new(options => options.SimulatingOperatingSystem(SimulationMode.Linux));
    private readonly INativeProcessRunner _runner = Substitute.For<INativeProcessRunner>();

    [Test]
    [Timeout(30_000)]
    [Arguments("HOME")]
    [Arguments("TEMP")]
    [Arguments("dotnet_cli_home")]
    public async Task BuildAsync_Should_Refuse_Explicit_Isolation_Overrides(string name, CancellationToken token)
    {
        _runner.RunAsync(Arg.Any<NativeProcessRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new NativeProcessResult(0, "1001", "")));
        var launch = Launch("/payload") with { Environment = new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = "not-owned" } };
        await Assert.That(async () => await new ContainerLaunchBuilder(_files, _runner).BuildAsync(launch,
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["PATH"] = "host-path" }, token)).ThrowsExactly<ArgumentException>()
            .WithMessage("Explicit environment input cannot replace an isolation path: " + name + " (Parameter 'explicitInputs')", StringComparison.Ordinal);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("explicit")]
    public async Task BuildAsync_Should_Separate_Cli_And_Container_Environment(string? value, CancellationToken token)
    {
        _runner.RunAsync(Arg.Any<NativeProcessRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new NativeProcessResult(0, "1001", "")));
        var launch = Launch("/payload") with
        {
            Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["OPENCODE_TEST_INPUT"] = value,
                ["DOTNET_ENVIRONMENT"] = "Development",
                ["DOTNET_ROOT"] = "sdk-path",
            },
        };
        var request = await new ContainerLaunchBuilder(_files, _runner).BuildAsync(launch, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = "host-path",
            ["DOCKER_HOST"] = "remote",
            ["OPENCODE_TEST_INPUT"] = "ambient",
        }, token);
        string[] expectedKeys = value is null ? ["DOCKER_CONFIG", "DOTNET_ENVIRONMENT", "HOME", "LANG", "LC_ALL", "PATH"]
            : ["DOCKER_CONFIG", "DOTNET_ENVIRONMENT", "HOME", "LANG", "LC_ALL", "OPENCODE_TEST_INPUT", "PATH"];
        await Assert.That(request.Environment.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(expectedKeys, CollectionOrdering.Matching);
        await Assert.That(request.Environment.GetValueOrDefault("OPENCODE_TEST_INPUT")).IsEqualTo(value);
        await Assert.That(request.Arguments.Skip(15)).IsEquivalentTo(IsolationArguments().Concat(
            ["-e", "OPENCODE_TEST_INPUT", "-e", "DOTNET_ENVIRONMENT", "-e", "NO_PROXY=localhost,127.0.0.1,::1", "registry.test/runtime@sha256:" + new string('b', 64), "/opencode-mcp-payload/opencode-mcp", "--transport", "stdio"]), CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("/payload with spaces", "source=/payload with spaces")]
    [Arguments("/payload,comma", "\"source=/payload,comma\"")]
    [Arguments("/payload\"quoted", "\"source=/payload\"\"quoted\"")]
    public async Task BuildAsync_Should_Map_The_Prepared_Working_Directory_And_Read_Only_Payload(string payload, string source, CancellationToken token)
    {
        var launch = Launch(payload);
        _runner.RunAsync(Arg.Any<NativeProcessRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            Task.FromResult(new NativeProcessResult(0, call.Arg<NativeProcessRequest>().Arguments[0] == "-u" ? "1001\n" : "1002\n", "")));
        var request = await new ContainerLaunchBuilder(_files, _runner).BuildAsync(launch, new Dictionary<string, string?>(StringComparer.Ordinal) { ["PATH"] = "host-path" }, token);
        await Assert.That(request.FileName).IsEqualTo("docker");
        await Assert.That(request.Arguments).IsEquivalentTo(new[]
        {
            "run", "-i", "--rm", "--name", "opencode-mcp-test-fixture", "--network", "host", "--user", "1001:1002",
            "--mount", "type=bind,source=/tmp/opencode-mcp-fixture,target=/opencode-mcp-run", "--mount", "type=bind," + source + ",target=/opencode-mcp-payload,readonly",
            "--workdir", "/opencode-mcp-run/work",
        }.Concat(IsolationArguments()).Concat(["-e", "NO_PROXY=localhost,127.0.0.1,::1", "registry.test/runtime@sha256:" + new string('b', 64), "/opencode-mcp-payload/opencode-mcp", "--transport", "stdio"]), CollectionOrdering.Matching);
        await Assert.That(request.InheritEnvironment).IsFalse();
        foreach (var group in new[] { "home", "config", "cache", "temp" })
        {
            await Assert.That(_files.Directory.Exists(_files.Path.Combine(launch.OwnedRoot, group))).IsTrue();
        }
    }

    private static ProcessLaunch Launch(string payload) => new()
    {
        FileName = "docker",
        Arguments = ["--transport", "stdio"],
        OwnedRoot = "/tmp/opencode-mcp-fixture",
        WorkingDirectory = "/tmp/opencode-mcp-fixture/work",
        Environment = new Dictionary<string, string?>(StringComparer.Ordinal),
        Container = new ContainerLaunch("opencode-mcp-test-fixture", "registry.test/runtime@sha256:" + new string('b', 64), payload, "/opencode-mcp-payload/opencode-mcp"),
    };

    private static IEnumerable<string> IsolationArguments()
    {
        string[][] groups =
        [
            ["home", "HOME", "USERPROFILE", "DOTNET_CLI_HOME"],
            ["config", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "OPENCODE_CONFIG_DIR"],
            ["cache", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME"],
            ["temp", "TEMP", "TMP", "TMPDIR", "XDG_RUNTIME_DIR"],
        ];
        return groups.SelectMany(static group => group.Skip(1).SelectMany(name => new[] { "-e", name + "=/opencode-mcp-run/" + group[0] }));
    }
}
