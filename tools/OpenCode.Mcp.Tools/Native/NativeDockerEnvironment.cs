using System.IO.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeDockerEnvironment(IFileSystem files)
{
    private static readonly string[] Necessities = ["PATH", "SystemRoot", "WINDIR", "ComSpec", "PATHEXT"];

    public IReadOnlyDictionary<string, string?> Create(string root, IReadOnlyDictionary<string, string?> inherited)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in Necessities.Where(inherited.ContainsKey))
        {
            environment.Add(name, inherited[name]);
        }
        var home = files.Path.Combine(root, "docker-home");
        var config = files.Path.Combine(root, "docker-config");
        files.Directory.CreateDirectory(home);
        files.Directory.CreateDirectory(config);
        if (files.Directory.EnumerateFileSystemEntries(config).Any())
        {
            throw new InvalidOperationException("Owned Docker configuration must be empty: " + config);
        }
        environment["HOME"] = home;
        environment["DOCKER_CONFIG"] = config;
        environment["LANG"] = "C";
        environment["LC_ALL"] = "C";
        return environment;
    }
}
