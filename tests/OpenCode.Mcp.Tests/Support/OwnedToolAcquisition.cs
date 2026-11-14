using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tests.Support;

internal sealed class OwnedToolAcquisition : IAsyncDisposable
{
    private static readonly string[] ColdStores = ["cache/packages", "cache/http", "cache/plugins", "temp/nuget-scratch", "tool-path"];
    private readonly IFileSystem _files;
    private readonly PrivateSdkCopy _sdk;
    private readonly bool _ownsSdk;
    private readonly INativeProcessRunner _runner;
    private string? _configurationPath;
    private string? _framework;
    private string[]? _realTools;
    private Task? _dispose;
    public OwnedToolAcquisition(IFileSystem fileSystem, INativeProcessRunner? runner = null, PrivateSdkCopy? sdk = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _files = fileSystem;
        _ownsSdk = sdk is null;
        _sdk = sdk ?? new PrivateSdkCopy(fileSystem);
        _runner = runner ?? new SystemNativeProcessRunner();
        Process = new OwnedMcpProcess(fileSystem, runner);
        OwnedRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "opencode-mcp-acquisition-" + Guid.NewGuid().ToString("N"));
        fileSystem.Directory.CreateDirectory(OwnedRoot);
    }

    public string OwnedRoot { get; }
    public OwnedMcpProcess Process { get; }
    public string SdkVersion { get; private set; } = "";
    public string SdkDirectory => _files.Path.Combine(_sdk.Root, "sdk", SdkVersion);
    public string ToolPath => _files.Path.Combine(OwnedRoot, "tool-path");
    public string GlobalToolPath => _files.Path.Combine(OwnedRoot, "home", ".dotnet", "tools");
    public async Task<ProcessLaunch> InstallGlobalAsync(string version, CancellationToken token)
    {
        try
        {
            var config = _configurationPath ?? throw new InvalidOperationException("Prepare the acquisition before installing.");
            var path = GlobalToolPath;
            TestContext.Current?.Output.WriteLine("Owned global tools: " + path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var resolver = new PhysicalPathResolver(_files);
            if (!_files.Path.GetFullPath(path).StartsWith(OwnedRoot + _files.Path.DirectorySeparatorChar, comparison)
                || !resolver.Resolve(path).StartsWith(resolver.Resolve(OwnedRoot) + _files.Path.DirectorySeparatorChar, comparison))
            {
                throw new InvalidOperationException("Global tools directory must remain inside the owned root: " + path);
            }
            _files.Directory.CreateDirectory(path);
            RequireColdStores([.. ColdStores.Select(relative => _files.Path.Combine(OwnedRoot, relative)), path]);
            _realTools = ObserveRealTools();
            InstallResult = await RunCommandAsync(BaseLaunch(["tool", "install", "-g", "OpenCodeDotNet.Mcp", "--version", version, "--configfile", config]), token);
            if (InstallResult.ExitCode != 0)
            {
                InstalledCommands = ObserveInstalledCommands();
                throw new InvalidOperationException("Global installation failed: " + InstallResult.Output.Trim());
            }
            var launch = await CompleteInstallAsync(path, token);
            if (!_files.Directory.Exists(_files.Path.Combine(path, ".store")))
            {
                throw new InvalidOperationException("The SDK reported success without the global tool store: " + path);
            }
            return launch;
        }
        catch (Exception original)
        {
            await ReleaseAfterFailureAsync(original);
            throw;
        }
    }
    public string? InstalledExecutable { get; private set; }
    public NativeProcessResult? InstallResult { get; private set; }
    public IReadOnlyList<string>? InstalledCommands { get; private set; }
    public static async Task AssertMissingImplementationAsync(NativeProcessResult result, string rid, string version, IReadOnlyList<string> commands)
    {
        await Assert.That(result.ExitCode).IsNotEqualTo(0);
        await Assert.That(result.Output.ToLowerInvariant()).Contains("opencodedotnet.mcp." + rid);
        await Assert.That(result.Output).Contains(version);
        await Assert.That(commands).IsEmpty();
    }

    public IReadOnlyList<string> ObserveInstalledCommands() => [.. _files.Directory.EnumerateFiles(OwnedRoot, "*", SearchOption.AllDirectories)
        .Where(path => _files.Path.GetFileName(path) is "opencode-mcp" or "opencode-mcp.exe" or "opencode-mcp.cmd").Order(StringComparer.Ordinal)];
    public async Task<string> ReadSdkRidAsync(CancellationToken token)
    {
        var result = await RunCommandAsync(BaseLaunch(["--info"]), token);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Cannot identify the private dotnet process: " + result.Output);
        }
        return result.StandardOutput.Split('\n').Select(static line => line.Trim())
            .Single(static line => line.StartsWith("RID:", StringComparison.Ordinal))[4..].Trim();
    }

    public async Task<ProcessLaunch> InstallAsync(string version, CancellationToken token)
    {
        try
        {
            var config = _configurationPath ?? throw new InvalidOperationException("Prepare the acquisition before installing.");
            InstallResult = await RunCommandAsync(BaseLaunch(["tool", "install", "OpenCodeDotNet.Mcp", "--version", version,
            "--tool-path", ToolPath, "--configfile", config,
            "--framework", _framework ?? throw new InvalidOperationException("The target framework is missing."),
            "-v", "minimal"]), token);
            if (InstallResult.ExitCode != 0)
            {
                InstalledCommands = ObserveInstalledCommands();
                throw new InvalidOperationException("Tool-path installation failed: " + InstallResult.Output.Trim());
            }
            return await CompleteInstallAsync(ToolPath, token);
        }
        catch (Exception original)
        {
            await ReleaseAfterFailureAsync(original);
            throw;
        }
    }

    private async Task<ProcessLaunch> CompleteInstallAsync(string directory, CancellationToken token)
    {
        var shim = _files.Path.Combine(directory, OperatingSystem.IsWindows() ? "opencode-mcp.cmd" : "opencode-mcp");
        if (!_files.File.Exists(shim))
        {
            throw new InvalidOperationException("The SDK reported success without the installed command: " + shim);
        }
        InstalledExecutable = await FindNativeEntryAsync(directory, token);
        var launch = BaseLaunch(["--transport", "stdio"]) with { FileName = shim };
        return OperatingSystem.IsWindows() ? launch with
        {
            FileName = _files.Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = [],
            VerbatimCommandLine = "/d /e:on /v:off /s /c \"\"" + shim + "\" --transport stdio\"",
        } : launch;
    }

    private string[] ObserveRealTools()
    {
        var path = _files.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
        if (!_files.Directory.Exists(path))
        {
            return [];
        }
        return [.. _files.Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(entry => _files.File.Exists(entry)
                ? string.Create(CultureInfo.InvariantCulture, $"{entry}|{_files.FileInfo.New(entry).Length}|{_files.FileInfo.New(entry).LastWriteTimeUtc.Ticks}") : entry)];
    }

    private async Task ReleaseAfterFailureAsync(Exception original)
    {
        try
        {
            await DisposeAsync();
        }
        catch (Exception cleanup)
        {
            _dispose = Task.CompletedTask; // Cleanup is reported here once; later disposal must not replace it.
            if (ReferenceEquals(original, cleanup))
            {
                throw;
            }
            IEnumerable<Exception> failures = original is AggregateException reported && reported.InnerExceptions.Contains(cleanup)
                ? reported.InnerExceptions : [original, cleanup];
            throw new AggregateException(_files.Directory.Exists(OwnedRoot)
                ? "Acquisition failed; retained root: " + OwnedRoot : "Acquisition failed.", failures);
        }
    }

    public async Task<string> FindNativeEntryAsync(string directory, CancellationToken token)
    {
        var entries = new List<string>();
        foreach (var path in _files.Directory.EnumerateFiles(directory, "DotnetToolSettings.xml", SearchOption.AllDirectories))
        {
            await using var stream = _files.File.OpenRead(path);
            var settings = await XDocument.LoadAsync(stream, LoadOptions.None, token);
            foreach (var command in settings.Descendants("Command").Where(static command => command.Attribute("Runner")?.Value == "executable"))
            {
                var entry = _files.Path.GetFullPath(_files.Path.Combine(_files.Path.GetDirectoryName(path) ?? throw new InvalidOperationException(),
                    command.Attribute("EntryPoint")?.Value ?? throw new InvalidOperationException("Missing native entry point.")));
                if (!_files.File.Exists(entry))
                {
                    throw new InvalidOperationException("Missing installed native entry: " + entry);
                }
                entries.Add(entry);
            }
        }
        return entries.Single();
    }

    public async Task StartAsync(ProcessLaunch launch, CancellationToken token)
    {
        try
        {
            RefuseAmbientConfiguration();
            await Process.StartAsync(launch, token);
        }
        catch (Exception original)
        {
            await ReleaseAfterFailureAsync(original);
            throw;
        }
    }

    public Task PrepareRemoteAsync(string repositoryRoot, string configurationPath, string sdkVersion, CancellationToken cancellationToken)
        => PrepareCoreAsync(repositoryRoot, sdkVersion, () =>
        {
            _configurationPath = _files.Path.Combine(OwnedDirectory("config"), _files.Path.GetFileName(configurationPath));
            _files.File.Copy(configurationPath, _configurationPath);
            return Task.CompletedTask;
        }, cancellationToken);

    public Task PrepareAsync(string repositoryRoot, string feed, string sdkVersion, CancellationToken cancellationToken)
        => PrepareCoreAsync(repositoryRoot, sdkVersion, () => ConfigureLocalAsync(repositoryRoot, feed, cancellationToken), cancellationToken);

    private async Task ConfigureLocalAsync(string repositoryRoot, string feed, CancellationToken token)
    {
        var configuration = XDocument.Parse(await _files.File.ReadAllTextAsync(_files.Path.Combine(repositoryRoot, "packaging", "local-feed.nuget.config"), token));
        configuration.Descendants("packageSources").Single().Elements("add").Single().SetAttributeValue("value", _files.Path.GetFullPath(feed));
        _configurationPath = _files.Path.Combine(OwnedDirectory("config"), "local-feed.nuget.config");
        await using var stream = _files.File.Create(_configurationPath);
        await configuration.SaveAsync(stream, SaveOptions.None, token);
    }

    private async Task PrepareCoreAsync(string repositoryRoot, string sdkVersion, Func<Task> configure, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(repositoryRoot);
            cancellationToken.ThrowIfCancellationRequested();
            var work = OwnedDirectory("work");
            RefuseAmbientConfiguration();
            _files.File.Copy(_files.Path.Combine(repositoryRoot, "global.json"), _files.Path.Combine(work, "global.json"));
            var global = JsonNode.Parse(await _files.File.ReadAllTextAsync(_files.Path.Combine(work, "global.json"), cancellationToken));
            var version = global?["sdk"]?["version"]?.GetValue<string>() ?? throw new InvalidOperationException("global.json has no SDK version.");
            var cli = OwnedDirectory("home", ".dotnet");
            await _files.File.WriteAllTextAsync(_files.Path.Combine(cli, sdkVersion + ".dotnetFirstUseSentinel"), "", cancellationToken);
            await configure();
            var properties = XDocument.Parse(await _files.File.ReadAllTextAsync(_files.Path.Combine(repositoryRoot, "Directory.Build.props"), cancellationToken));
            _framework = properties.Descendants("TargetFramework").Single().Value;
            await _sdk.PrepareAsync(sdkVersion, cancellationToken);
            var probe = BaseLaunch([]);
            OwnedDirectory("tool-path");
            var sdk = await RunCommandAsync(probe with { Arguments = ["--version"] }, cancellationToken);
            SdkVersion = sdk.StandardOutput.Trim();
            if (sdk.ExitCode != 0 || SdkVersion != sdkVersion)
            {
                throw new InvalidOperationException("The private SDK selected for global.json " + version + " must be " + sdkVersion + ": " + sdk.Output);
            }
            var runtimes = await RunCommandAsync(probe with { Arguments = ["--list-runtimes"] }, cancellationToken);
            var expected = "Microsoft.NETCore.App " + _sdk.RuntimeVersion + " [" + _files.Path.Combine(new PhysicalPathResolver(_files).Resolve(_sdk.Root), "shared", "Microsoft.NETCore.App") + "]";
            if (runtimes.ExitCode != 0 || runtimes.StandardOutput.Trim() != expected)
            {
                throw new InvalidOperationException("The private SDK must list only its required Core runtime: " + runtimes.Output);
            }
        }
        catch (Exception original)
        {
            await ReleaseAfterFailureAsync(original);
            throw;
        }
    }

    public ProcessLaunch CreateDnxLaunch(string version) => CreateDnxLaunch(version, ["--transport", "stdio"]);

    public ProcessLaunch CreateDnxLaunch(string version, IReadOnlyList<string> productArguments)
    {
        var config = _configurationPath ?? throw new InvalidOperationException("Prepare the acquisition before creating a launch.");
        RefuseAmbientConfiguration();
        RequireColdStores(ColdStores.Select(relative => _files.Path.Combine(OwnedRoot, relative)));
        return BaseLaunch(["dnx", "-v", "minimal", "OpenCodeDotNet.Mcp@" + version, "--configfile", config, "--yes", "--", .. productArguments]);
    }

    private void RequireColdStores(IEnumerable<string> directories)
    {
        var entries = directories.SelectMany(_files.Directory.EnumerateFileSystemEntries)
            .Order(StringComparer.Ordinal).ToArray();
        if (entries.Length != 0)
        {
            throw new InvalidOperationException("Cold package acquisition requires empty stores:\n" + string.Join('\n', entries));
        }
    }

    private ProcessLaunch BaseLaunch(IReadOnlyList<string> arguments) => new()
    {
        FileName = _sdk.HostPath,
        Arguments = arguments,
        OwnedRoot = OwnedRoot,
        WorkingDirectory = _files.Path.Combine(OwnedRoot, "work"),
        Environment = AcquisitionInputs(),
    };

    private Task<NativeProcessResult> RunCommandAsync(ProcessLaunch launch, CancellationToken token)
    {
        RefuseAmbientConfiguration();
        return _runner.RunAsync(new NativeProcessRequest(launch.FileName, launch.Arguments, launch.WorkingDirectory, Process.CreateEnvironment(launch), TimeSpan.FromSeconds(45))
        { InheritEnvironment = false }, token);
    }

    private void RefuseAmbientConfiguration()
    {
        var paths = new List<string>();
        var work = _files.Path.Combine(OwnedRoot, "work");
        foreach (var directory in new[] { work, new PhysicalPathResolver(_files).Resolve(work) }.Distinct(StringComparer.Ordinal))
        {
            for (var ancestor = directory; ancestor is not null; ancestor = _files.Path.GetDirectoryName(ancestor))
            {
                paths.AddRange(_files.Directory.EnumerateFiles(ancestor).Where(path => string.Equals(_files.Path.GetFileName(path), "nuget.config", StringComparison.OrdinalIgnoreCase)));
            }
        }
        const string machine = "/Library/Application Support/NuGet/Config";
        if (OperatingSystem.IsMacOS() && _files.Directory.Exists(machine))
        {
            paths.AddRange(_files.Directory.EnumerateFiles(machine, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".config", StringComparison.OrdinalIgnoreCase)));
        }
        if (paths.Count != 0)
        {
            throw new InvalidOperationException("Ambient NuGet configuration cannot be owned:\n" + string.Join('\n', paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        }
    }

    private Dictionary<string, string?> AcquisitionInputs()
    {
        var inputs = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = null,
            ["DOTNET_ROOT_X64"] = null,
            ["DOTNET_ROOT_ARM64"] = null,
            ["DOTNET_HOST_PATH"] = null,
            ["NUGET_PACKAGES"] = OwnedDirectory("cache", "packages"),
            ["NUGET_HTTP_CACHE_PATH"] = OwnedDirectory("cache", "http"),
            ["NUGET_PLUGINS_CACHE_PATH"] = OwnedDirectory("cache", "plugins"),
            ["NUGET_SCRATCH"] = OwnedDirectory("temp", "nuget-scratch"),
            ["NUGET_PLUGIN_PATHS"] = OwnedDirectory("empty-plugins"),
            ["PATH"] = _sdk.Root + _files.Path.PathSeparator + (OperatingSystem.IsWindows() ? Environment.SystemDirectory : "/usr/bin:/bin"),
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false",
            ["DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK"] = "true",
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        };
        if (OperatingSystem.IsWindows())
        {
            inputs.Add("ProgramFiles", OwnedDirectory("machine", "program-files"));
            inputs.Add("ProgramFiles(x86)", OwnedDirectory("machine", "program-files-x86"));
        }
        if (OperatingSystem.IsLinux())
        {
            inputs.Add("NUGET_COMMON_APPLICATION_DATA", OwnedDirectory("machine"));
        }
        return inputs;
    }

    private string OwnedDirectory(params string[] components)
    {
        var path = _files.Path.Combine([OwnedRoot, .. components]);
        _files.Directory.CreateDirectory(path);
        return path;
    }

    public ValueTask DisposeAsync() => new(_dispose ??= CleanupAsync());

    private async Task CleanupAsync()
    {
        try
        {
            await CleanupCoreAsync();
        }
        catch (Exception cleanup)
        {
            try
            {
                await RemoveRetainedCredentialsAsync();
            }
            catch (Exception credentials)
            {
                throw new AggregateException(cleanup, credentials);
            }
            throw;
        }
    }

    private async Task CleanupCoreAsync()
    {
        await Process.DisposeAsync();
        try
        {
            try
            {
                if (_realTools is not null && !_realTools.SequenceEqual(ObserveRealTools(), StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("The real profile global-tools inventory changed.");
                }
            }
            finally
            {
                if (_ownsSdk)
                {
                    await _sdk.DisposeAsync();
                }
            }
        }
        finally
        {
            if (_files.Directory.Exists(OwnedRoot))
            {
                _files.Directory.Delete(OwnedRoot, recursive: true);
            }
        }
    }

    private async Task RemoveRetainedCredentialsAsync()
    {
        if (_configurationPath is null || !_files.File.Exists(_configurationPath))
        {
            return;
        }
        // Credential cleanup must finish even after the acquisition token is cancelled.
        var token = CancellationToken.None;
        XDocument configuration;
        await using (var input = _files.File.OpenRead(_configurationPath))
        {
            configuration = await XDocument.LoadAsync(input, LoadOptions.None, token);
        }
        configuration.Root?.Elements("packageSourceCredentials").Remove();
        await using var output = _files.File.Create(_configurationPath);
        await configuration.SaveAsync(output, SaveOptions.None, token);
    }
}
