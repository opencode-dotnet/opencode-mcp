using System.Globalization;
using System.IO.Abstractions;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tests.Support;

internal sealed class ContainerLaunchBuilder(IFileSystem files, INativeProcessRunner runner)
{
    public async Task<NativeProcessRequest> BuildAsync(ProcessLaunch launch, IReadOnlyDictionary<string, string?> inherited, CancellationToken token)
    {
        files.Directory.CreateDirectory(launch.WorkingDirectory);
        var environment = new Dictionary<string, string?>(new NativeDockerEnvironment(files).Create(launch.OwnedRoot, inherited), StringComparer.Ordinal);
        var container = launch.Container ?? throw new InvalidOperationException("Container launch metadata is required.");
        var user = await ReadIdentityAsync("-u", launch.WorkingDirectory, environment, token);
        var group = await ReadIdentityAsync("-g", launch.WorkingDirectory, environment, token);
        var arguments = new List<string>
        {
            "run", "-i", "--rm", "--name", container.Name, "--network", "host", "--user", user + ":" + group,
            "--mount", "type=bind," + MountSource(launch.OwnedRoot) + ",target=/opencode-mcp-run",
            "--mount", "type=bind," + MountSource(container.PayloadDirectory) + ",target=/opencode-mcp-payload,readonly",
            "--workdir", "/opencode-mcp-run/work",
        };
        foreach (var isolation in OwnedMcpProcess.IsolationGroups)
        {
            files.Directory.CreateDirectory(files.Path.Combine(launch.OwnedRoot, isolation[0]));
            foreach (var name in isolation.Skip(1))
            {
                arguments.AddRange(["-e", name + "=/opencode-mcp-run/" + isolation[0]]);
            }
        }
        ForwardInputs(launch.Environment, environment, arguments);
        arguments.AddRange(["-e", "NO_PROXY=localhost,127.0.0.1,::1", container.Image, container.Executable]);
        arguments.AddRange(launch.Arguments);
        return new NativeProcessRequest(launch.FileName, arguments, launch.WorkingDirectory, environment, TimeSpan.FromSeconds(15)) { InheritEnvironment = false };
    }

    private static void ForwardInputs(IReadOnlyDictionary<string, string?> explicitInputs, Dictionary<string, string?> environment, List<string> arguments)
    {
        foreach (var (name, value) in explicitInputs)
        {
            if (OwnedMcpProcess.IsolationGroups.Any(group => group.Skip(1).Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("Explicit environment input cannot replace an isolation path: " + name, nameof(explicitInputs));
            }
            if (name.StartsWith("DOCKER_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("OPENCODE_MCP_TEST_", StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
                || name is "PATH" or "LANG" or "LC_ALL" or "NO_PROXY")
            {
                continue;
            }
            arguments.AddRange(["-e", name]);
            if (value is null)
            {
                environment.Remove(name);
            }
            else
            {
                environment[name] = value;
            }
        }
    }

    private async Task<string> ReadIdentityAsync(string argument, string work, IReadOnlyDictionary<string, string?> environment, CancellationToken token)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        guard.CancelAfter(TimeSpan.FromSeconds(15));
        var result = await runner.RunAsync(new NativeProcessRequest("id", [argument], work, environment, TimeSpan.FromSeconds(15)) { InheritEnvironment = false }, guard.Token);
        if (result.ExitCode != 0 || !uint.TryParse(result.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException("Cannot determine Linux identity: id " + argument);
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string MountSource(string path)
    {
        var field = "source=" + path;
        return field.IndexOfAny([',', '"']) >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
    }
}
