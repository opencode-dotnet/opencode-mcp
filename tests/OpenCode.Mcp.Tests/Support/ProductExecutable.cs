using System.IO.Abstractions;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Selects the exact product artifact used by executable tests.</summary>
internal sealed partial class ProductExecutable
{
    private readonly IFileSystem _files;
    private readonly string _os;
    private readonly Architecture _osArchitecture;
    private readonly Architecture _processArchitecture;

    /// <summary>Receives the filesystem used to resolve product artifacts.</summary>
    public ProductExecutable(IFileSystem fileSystem, string? os = null, Architecture? osArchitecture = null, Architecture? processArchitecture = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _files = fileSystem;
        _os = os ?? (OperatingSystem.IsWindows() ? "win" : "osx");
        if (os is null && OperatingSystem.IsLinux())
        {
            _os = "linux";
        }
        _osArchitecture = osArchitecture ?? RuntimeInformation.OSArchitecture;
        _processArchitecture = processArchitecture ?? RuntimeInformation.ProcessArchitecture;
    }

    /// <summary>Resolves managed output or an explicit executable without fallback on invalid input.</summary>
    public ProcessLaunch Resolve(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var inputs = new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
        var containerSelected = inputs.Remove("OPENCODE_MCP_TEST_CONTAINER_IMAGE", out var image);
        if (containerSelected)
        {
            ValidateContainerInputs(image, inputs);
        }
        var hasNativeSelector = inputs.Remove("OPENCODE_MCP_TEST_EXPECT_NATIVE", out var native);
        var expectNative = string.Equals(native, "true", StringComparison.OrdinalIgnoreCase);
        if (hasNativeSelector && !expectNative && !string.Equals(native, "false", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("OPENCODE_MCP_TEST_EXPECT_NATIVE must be true or false.");
        }
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
            if (expectNative)
            {
                var observed = ReadNativeIdentity(fileName);
                var expected = _os + "-" + _osArchitecture.ToString().ToLowerInvariant();
                if (!string.Equals(observed, expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Native execution needs a compatible {expected} image; observed {observed}.");
                }
            }
            arguments = [];
        }
        else
        {
            if (expectNative)
            {
                throw new InvalidOperationException("Native execution requires OPENCODE_MCP_TEST_EXECUTABLE; managed fallback is forbidden.");
            }
            fileName = inputs.TryGetValue("DOTNET_HOST_PATH", out var host) && !string.IsNullOrEmpty(host) ? host : "dotnet";
            arguments = [ResolveManaged()];
        }

        var container = containerSelected ? SelectContainer(image, fileName) : null;
        var root = _files.Path.Combine(_files.Path.GetTempPath(), "opencode-mcp-" + Guid.NewGuid().ToString("N"));
        var work = _files.Path.Combine(root, "work");
        _files.Directory.CreateDirectory(work);
        return new ProcessLaunch { FileName = container is null ? fileName : "docker", Arguments = arguments, Environment = inputs, OwnedRoot = root, WorkingDirectory = work, Container = container };
    }

    private ContainerLaunch SelectContainer(string? image, string executable)
    {
        if (_os != "linux" || _osArchitecture != _processArchitecture)
        {
            throw new InvalidOperationException("Container execution requires a matching Linux test host.");
        }
        return new ContainerLaunch("opencode-mcp-test-" + Guid.NewGuid().ToString("N"), image ?? throw new InvalidOperationException("Missing container image."),
            _files.Path.GetDirectoryName(executable) ?? throw new InvalidOperationException("Payload needs a parent directory."),
            "/opencode-mcp-payload/" + _files.Path.GetFileName(executable));
    }

    private static void ValidateContainerInputs(string? image, Dictionary<string, string?> inputs)
    {
        var errors = new List<string>();
        if (image is null || !DigestImage().IsMatch(image))
        {
            errors.Add("OPENCODE_MCP_TEST_CONTAINER_IMAGE must be digest-qualified (sha256).");
        }
        if (!inputs.TryGetValue("OPENCODE_MCP_TEST_EXPECT_NATIVE", out var native) || !string.Equals(native, "true", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Container execution requires OPENCODE_MCP_TEST_EXPECT_NATIVE=true.");
        }
        if (!inputs.ContainsKey("OPENCODE_MCP_TEST_EXECUTABLE"))
        {
            errors.Add("Container execution requires OPENCODE_MCP_TEST_EXECUTABLE.");
        }
        if (errors.Count != 0)
        {
            throw new InvalidOperationException(string.Join('\n', errors));
        }
    }

    [GeneratedRegex(@"\A[^\s@]+@sha256:[a-fA-F0-9]{64}\z", RegexOptions.None, Timeout.Infinite)]
    private static partial Regex DigestImage();

    private string ReadNativeIdentity(string path)
    {
        using var reader = new BinaryReader(_files.File.OpenRead(path));
        var stream = reader.BaseStream;
        var magic = reader.ReadUInt32();
        if ((magic & 0xffff) == 0x5a4d)
        {
            stream.Position = 0;
            using var image = new PEReader(stream);
            if (image.PEHeaders.CorHeader is not null)
            {
                return "managed";
            }
            var architecture = "unknown";
            if (image.PEHeaders.CoffHeader.Machine == Machine.Amd64)
            {
                architecture = "x64";
            }
            else if (image.PEHeaders.CoffHeader.Machine == Machine.Arm64)
            {
                architecture = "arm64";
            }
            return "win-" + architecture;
        }
        if (magic == 0x464c457f)
        {
            stream.Position = 18;
            return "linux-" + (reader.ReadUInt16() switch { 62 => "x64", 183 => "arm64", _ => "unknown" });
        }
        if (magic == 0xfeedfacf)
        {
            return "osx-" + (reader.ReadUInt32() switch { 0x01000007 => "x64", 0x0100000c => "arm64", _ => "unknown" });
        }
        return "unknown";
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
