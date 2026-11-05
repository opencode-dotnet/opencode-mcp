using System.IO.Abstractions;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Selects the exact product artifact used by executable tests.</summary>
internal sealed class ProductExecutable
{
    private readonly IFileSystem _files;

    /// <summary>Receives the filesystem used to resolve product artifacts.</summary>
    public ProductExecutable(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _files = fileSystem;
    }

    /// <summary>Resolves managed output or an explicit executable without fallback on invalid input.</summary>
    public ProcessLaunch Resolve(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var inputs = new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
        string fileName;
        string[] arguments;
        if (inputs.Remove("OPENCODE_MCP_TEST_EXECUTABLE", out var executable))
        {
            if (string.IsNullOrWhiteSpace(executable) || !_files.File.Exists(executable)
                || string.Equals(_files.Path.GetExtension(executable), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The explicit product executable must exist and must not be a DLL: " + executable);
            }
            fileName = _files.Path.GetFullPath(executable);
            arguments = [];
        }
        else
        {
            fileName = inputs.TryGetValue("DOTNET_HOST_PATH", out var host) && !string.IsNullOrEmpty(host) ? host : "dotnet";
            arguments = [ResolveManaged()];
        }

        var root = _files.Path.Combine(_files.Path.GetTempPath(), "opencode-mcp-" + Guid.NewGuid().ToString("N"));
        var work = _files.Path.Combine(root, "work");
        _files.Directory.CreateDirectory(work);
        return new ProcessLaunch { FileName = fileName, Arguments = arguments, Environment = inputs, OwnedRoot = root, WorkingDirectory = work };
    }

    private string ResolveManaged()
    {
        var root = _files.Path.GetFullPath(AppContext.BaseDirectory);
        while (!_files.File.Exists(_files.Path.Combine(root, "OpenCode.Mcp.slnx")))
        {
            root = _files.Path.GetDirectoryName(root.TrimEnd(_files.Path.DirectorySeparatorChar, _files.Path.AltDirectorySeparatorChar))
                ?? throw new InvalidOperationException("Cannot locate OpenCode.Mcp.slnx above the test executable.");
        }
        var output = _files.Path.Combine(root, "src", "OpenCode.Mcp", "bin", "Release", "net10.0");
        foreach (var name in new[] { "OpenCode.Mcp.dll", "OpenCode.Mcp.runtimeconfig.json", "OpenCode.Mcp.deps.json" })
        {
            var artifact = _files.Path.Combine(output, name);
            if (!_files.File.Exists(artifact))
            {
                throw new InvalidOperationException("Build the Release product before executable tests; missing artifact: " + artifact);
            }
        }
        return _files.Path.Combine(output, "OpenCode.Mcp.dll");
    }
}
