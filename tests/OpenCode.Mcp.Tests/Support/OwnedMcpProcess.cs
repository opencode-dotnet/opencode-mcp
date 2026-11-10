using System.Diagnostics;
using System.IO.Abstractions;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Owns a child process, redirected streams and isolated files through completed cleanup.</summary>
internal sealed partial class OwnedMcpProcess : IAsyncDisposable
{
    private static readonly string[] LaunchVariables = ["PATH", "SystemRoot", "WINDIR", "ComSpec", "PATHEXT", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64",
        "DOTNET_HOST_PATH", "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE"];
    internal static readonly string[][] IsolationGroups =
    [
        ["home", "HOME", "USERPROFILE", "DOTNET_CLI_HOME"],
        ["config", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "OPENCODE_CONFIG_DIR"],
        ["cache", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME"],
        ["temp", "TEMP", "TMP", "TMPDIR", "XDG_RUNTIME_DIR"],
    ];
    private static readonly string[] SensitiveNames = ["TOKEN", "KEY", "SECRET", "PASSWORD"];
    private readonly IFileSystem _files;
    private readonly INativeProcessRunner _runner;
    private NativeContainerOwner? _container;
    private readonly Process _process = new();
    private readonly List<string> _errors = [];
    private readonly List<string> _completeErrors = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<Uri> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RecordingDuplexStream? _input;
    private RecordingDuplexStream? _output;
    private StreamReader? _stderr;
    private McpClient? _client;
    private Task _drain = Task.CompletedTask;
    private Task _exit = Task.CompletedTask;
    private Task? _stdoutDrain;
    private Task? _dispose;
    private string? _root;
    private string[] _diagnosticRoots = [];
    private bool _started;
    private bool _startAttempted;
    private readonly Dictionary<Process, DateTime> _ownedProcesses = [];
    private readonly List<Task> _ownedExits = [];

    /// <summary>Receives the filesystem used for the child's isolated run.</summary>
    public OwnedMcpProcess(IFileSystem fileSystem, INativeProcessRunner? runner = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _files = fileSystem;
        _runner = runner ?? new SystemNativeProcessRunner();
    }

    /// <summary>Gets the child's recorded protocol observations.</summary>
    public WireTranscript Transcript { get; } = new();

    internal RecordingDuplexStream StandardInput => _input ?? throw new InvalidOperationException("The child has not started.");
    internal bool HasExited => _process.HasExited;
    internal int ProcessId => _process.Id;
    internal bool DiagnosticsCompleted => _drain.IsCompletedSuccessfully;
    internal bool ExitCompleted => _exit.IsCompletedSuccessfully;
    internal bool OutputDrainStarted => _stdoutDrain is not null;
    internal IReadOnlyList<string> CompleteStandardError
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_completeErrors.ToArray());
            }
        }
    }
    internal IReadOnlyDictionary<string, string?> ChildEnvironment => new System.Collections.ObjectModel.ReadOnlyDictionary<string, string?>(_process.StartInfo.Environment);
    internal static TimeSpan ListeningExpressionTimeout => ListeningEndpointRegex().MatchTimeout;

    /// <summary>Gets bounded, redacted diagnostic observations.</summary>
    public IReadOnlyList<string> StandardError
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_errors.ToArray());
            }
        }
    }

    /// <summary>Starts the supplied child and begins owned diagnostic draining.</summary>
    public async Task StartAsync(ProcessLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (_startAttempted)
        {
            throw new InvalidOperationException("The owner can start only one child.");
        }
        var root = _files.Path.GetFullPath(launch.OwnedRoot).TrimEnd(_files.Path.DirectorySeparatorChar, _files.Path.AltDirectorySeparatorChar);
        var temp = _files.Path.GetFullPath(_files.Path.GetTempPath()).TrimEnd(_files.Path.DirectorySeparatorChar, _files.Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(_files.Path.GetDirectoryName(root), temp, comparison)
            || !_files.Path.GetFileName(root).StartsWith("opencode-mcp-", StringComparison.Ordinal)
            || !_files.Path.GetFullPath(launch.WorkingDirectory).StartsWith(root + _files.Path.DirectorySeparatorChar, comparison))
        {
            throw new ArgumentException("The launch requires an opencode-mcp- temp child root containing its working directory.", nameof(launch));
        }
        _startAttempted = true;
        _root = root;
        try
        {
            if (launch.Container is { } container)
            {
                var environment = new NativeDockerEnvironment(_files).Create(root, new Dictionary<string, string?>(new ProcessStartInfo().Environment, StringComparer.Ordinal));
                _container = new NativeContainerOwner(_runner, new NativeProcessRequest("docker", [], launch.WorkingDirectory, environment, TimeSpan.FromSeconds(15)) { InheritEnvironment = false }, container.Name);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _files.Directory.CreateDirectory(launch.WorkingDirectory);
            var start = await PrepareStartAsync(launch, cancellationToken);
            _process.StartInfo = start;
            _started = _process.Start();
            // The owner observes this task through exit or forced cleanup, independent of startup cancellation.
            _exit = _process.WaitForExitAsync(CancellationToken.None);
            _input = new RecordingDuplexStream(_process.StandardInput.BaseStream, Transcript, fromServer: false);
            _output = new RecordingDuplexStream(_process.StandardOutput.BaseStream, Transcript, fromServer: true);
            _stderr = _process.StandardError;
            _diagnosticRoots = [root, new PhysicalPathResolver(_files).Resolve(root)];
            _drain = DrainAsync(_stderr);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception original)
        {
            try
            {
                await DisposeAsync();
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(original, cleanup);
            }
            throw;
        }
    }

    /// <summary>Connects a modern MCP client over the child's recorded standard streams.</summary>
    public async Task<McpClient> ConnectStdioAsync(CancellationToken cancellationToken)
    {
        var transport = new StreamClientTransport(_input ?? throw new InvalidOperationException("The child has not started."),
            _output ?? throw new InvalidOperationException("The child has not started."));
        _client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ProtocolVersion = ProtocolData.Revision,
            DiscoverProbeTimeout = TimeSpan.FromSeconds(30),
            InitializationTimeout = TimeSpan.FromSeconds(30),
        }, cancellationToken: cancellationToken);
        return _client;
    }

    /// <summary>Ends the child's protocol input without disposing its client.</summary>
    public async Task CloseInputAsync()
    {
        if (_input is not null)
        {
            await _input.DisposeAsync();
        }
        Transcript.Complete(fromServer: false);
    }

    /// <summary>Waits for the owned child's completed exit and returns its exit code.</summary>
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_exit, _drain, DrainOutput()).WaitAsync(cancellationToken);
        await Task.WhenAll(_ownedExits).WaitAsync(cancellationToken);
        if (_client is not null)
        {
            await _client.Completion.WaitAsync(cancellationToken);
        }
        return _process.ExitCode;
    }

    /// <summary>Returns the dynamic HTTP address announced by the owned child.</summary>
    public Task<Uri> WaitForHttpEndpointAsync(CancellationToken cancellationToken)
    {
        _stdoutDrain = DrainOutput();
        return _endpoint.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Finishes child termination, readers and handles before removing owned files.</summary>
    public ValueTask DisposeAsync() => new(_dispose ??= CleanupAsync());

    internal IReadOnlyDictionary<string, string?> CreateEnvironment(ProcessLaunch launch)
        => CreateEnvironment(launch, new Dictionary<string, string?>(new ProcessStartInfo().Environment, StringComparer.Ordinal));

    internal IReadOnlyDictionary<string, string?> CreateEnvironment(ProcessLaunch launch, IReadOnlyDictionary<string, string?> inheritedEnvironment)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var start = new ProcessStartInfo();
        ConfigureEnvironment(start, launch.Environment, launch.OwnedRoot, inheritedEnvironment);
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string?>(start.Environment);
    }

    private Task CleanupAsync() => _container is null ? CleanupDirectAsync() : CleanupContainerAsync();

    private async Task<ProcessStartInfo> PrepareStartAsync(ProcessLaunch launch, CancellationToken token)
    {
        var start = new ProcessStartInfo(launch.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = launch.WorkingDirectory,
        };
        var arguments = launch.Arguments;
        if (launch.Container is not null)
        {
            var request = await new ContainerLaunchBuilder(_files, _runner).BuildAsync(launch, new Dictionary<string, string?>(start.Environment, StringComparer.Ordinal), token);
            arguments = request.Arguments;
            start.Environment.Clear();
            foreach (var entry in request.Environment)
            {
                start.Environment[entry.Key] = entry.Value;
            }
        }
        else
        {
            ConfigureEnvironment(start, launch.Environment, launch.OwnedRoot, new Dictionary<string, string?>(start.Environment, StringComparer.Ordinal));
        }
        if (launch.VerbatimCommandLine is { } commandLine)
        {
            start.Arguments = commandLine;
        }
        else
        {
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
        }
        return start;
    }

    private async Task CleanupContainerAsync()
    {
        var failures = new List<Exception>();
        await AttemptCleanupAsync(async token =>
        {
            if (_started)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
                await _exit.WaitAsync(token);
            }
        }, failures);
        var terminated = failures.Count == 0;
        var container = _container ?? throw new InvalidOperationException("No container owner.");
        failures.AddRange(await container.CleanupAsync());
        await AttemptCleanupAsync(async token =>
        {
            await Task.WhenAll(_drain, DrainOutput()).WaitAsync(token);
        }, failures);
        await AttemptCleanupAsync(async token =>
        {
            if (_client is not null)
            {
                await _client.DisposeAsync().AsTask().WaitAsync(token);
            }
        }, failures);
        await AttemptCleanupAsync(async _ =>
        {
            if (_input is not null)
            {
                await _input.DisposeAsync();
            }
        }, failures);
        await AttemptCleanupAsync(async _ =>
        {
            if (_output is not null)
            {
                await _output.DisposeAsync();
            }
        }, failures);
        await AttemptCleanupAsync(_ =>
        {
            _stderr?.Dispose();
            return Task.CompletedTask;
        }, failures);
        await AttemptCleanupAsync(_ =>
        {
            _process.Dispose();
            return Task.CompletedTask;
        }, failures);
        if (terminated && container.AbsenceProven)
        {
            await AttemptCleanupAsync(_ =>
            {
                if (_root is not null && _files.Directory.Exists(_root))
                {
                    _files.Directory.Delete(_root, recursive: true);
                }
                return Task.CompletedTask;
            }, failures);
        }
        else
        {
            failures.Add(new InvalidOperationException("Container ownership is unresolved; retained root: " + _root));
        }
        if (failures.Count != 0)
        {
            throw new AggregateException("Container process cleanup failed.", failures);
        }
    }

    private static async Task AttemptCleanupAsync(Func<CancellationToken, Task> cleanup, List<Exception> failures)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // Each independent cleanup phase owns a fresh guard.
        try
        {
            await cleanup(guard.Token).WaitAsync(guard.Token);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
    }

    private async Task CleanupDirectAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // Forced termination and owned reader completion.
        try
        {
            var captureFailure = CaptureOwnedProcessFailure();
            await EndRootAsync(guard.Token);
            if (captureFailure is null)
            {
                captureFailure = CaptureOwnedProcessFailure(); // The launcher can exit before its native entry does.
            }
            foreach (var (process, identity) in _ownedProcesses)
            {
                if (!process.HasExited)
                {
                    using var current = Process.GetProcessById(process.Id);
                    if (current.StartTime != identity)
                    {
                        throw new InvalidOperationException("An owned PID was reused; retained root: " + _root);
                    }
                    process.Kill();
                }
            }
            await Task.WhenAll(_ownedExits).WaitAsync(guard.Token);
            await Task.WhenAll(_drain, DrainOutput()).WaitAsync(guard.Token);
            if (_client is not null)
            {
                await _client.DisposeAsync().AsTask().WaitAsync(guard.Token);
            }
            if (captureFailure is not null)
            {
                ExceptionDispatchInfo.Capture(captureFailure).Throw();
            }
        }
        finally
        {
            try
            {
                if (_input is not null)
                {
                    await _input.DisposeAsync();
                }
            }
            finally
            {
                try
                {
                    if (_output is not null)
                    {
                        await _output.DisposeAsync();
                    }
                }
                finally
                {
                    try
                    {
                        _stderr?.Dispose();
                    }
                    finally
                    {
                        foreach (var owned in _ownedProcesses.Keys)
                        {
                            owned.Dispose();
                        }
                        _process.Dispose();
                    }
                }
            }
        }
        if (_root is not null && _files.Directory.Exists(_root))
        {
            _files.Directory.Delete(_root, recursive: true);
        }
    }

    internal string ObserveExpectedExecutable(string expectedExecutable)
    {
        return ObserveOwnedExecutables().Values.SingleOrDefault(path => string.Equals(path, expectedExecutable,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The packaged native entry is not running: " + expectedExecutable);
    }

    private Exception? CaptureOwnedProcessFailure()
    {
        try
        {
            CaptureOwnedProcesses();
            return null;
        }
        catch (Exception failure)
        {
            return failure;
        }
    }

    private async Task EndRootAsync(CancellationToken token)
    {
        if (_started)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
            await _exit.WaitAsync(token);
        }
    }

    internal IReadOnlyDictionary<int, string> ObserveOwnedExecutables()
    {
        CaptureOwnedProcesses();
        return _ownedProcesses.Keys.Where(static process => !process.HasExited)
            .ToDictionary(static process => process.Id, static process => process.MainModule?.FileName ?? throw new InvalidOperationException("An owned process has no executable path."));
    }

    private void CaptureOwnedProcesses()
    {
        if (_root is null || !_files.Directory.Exists(_root))
        {
            return;
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var names = _files.Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Where(path => !OperatingSystem.IsWindows() || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Select(_files.Path.GetFileNameWithoutExtension).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (names.Count == 0)
        {
            return;
        }
        var physicalRoot = new PhysicalPathResolver(_files).Resolve(_root) + _files.Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            var retained = false;
            try
            {
                if (_ownedProcesses.Keys.All(owned => owned.Id != process.Id)
                    && names.Contains(process.ProcessName) && !process.HasExited
                    && process.MainModule?.FileName is { } path && path.StartsWith(physicalRoot, comparison))
                {
                    _ = process.SafeHandle;
                    _ownedProcesses.Add(process, process.StartTime);
                    _ownedExits.Add(process.WaitForExitAsync(CancellationToken.None)); // Ownership survives startup cancellation.
                    retained = true;
                }
            }
            finally
            {
                if (!retained)
                {
                    process.Dispose();
                }
            }
        }
    }

    private Task DrainOutput() => _client is null && _output is not null
        ? _stdoutDrain ??= _output.CopyToAsync(Stream.Null)
        : Task.CompletedTask;

    private void ConfigureEnvironment(ProcessStartInfo start, IReadOnlyDictionary<string, string?> explicitInputs, string root, IReadOnlyDictionary<string, string?> inheritedEnvironment)
    {
        var inherited = new Dictionary<string, string?>(inheritedEnvironment, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        start.Environment.Clear();
        foreach (var name in LaunchVariables)
        {
            if (inherited.TryGetValue(name, out var value))
            {
                start.Environment[name] = value;
            }
        }
        var isolation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in IsolationGroups)
        {
            var path = _files.Path.Combine(root, group[0]);
            _files.Directory.CreateDirectory(path);
            foreach (var name in group.Skip(1))
            {
                isolation.Add(name, path);
            }
        }
        start.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
        foreach (var (name, value) in explicitInputs)
        {
            if (isolation.ContainsKey(name))
            {
                throw new ArgumentException("Explicit environment input cannot replace an isolation path: " + name, nameof(explicitInputs));
            }
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }
        foreach (var (name, value) in isolation)
        {
            start.Environment[name] = value;
        }
    }

    private async Task DrainAsync(StreamReader reader)
    {
        try
        {
            var buffer = new char[1024];
            var line = new StringBuilder();
            var lifetime = false;
            var truncated = false;
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory())) != 0)
            {
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == '\n')
                    {
                        lifetime = ObserveError(line.ToString().TrimEnd('\r'), lifetime, truncated);
                        line.Clear();
                        truncated = false;
                    }
                    else if (line.Length < 2000)
                    {
                        line.Append(buffer[index]);
                    }
                    else
                    {
                        truncated = true;
                    }
                }
            }
            if (line.Length > 0)
            {
                ObserveError(line.ToString(), lifetime, truncated);
            }
            _endpoint.TrySetException(new InvalidOperationException("The child closed stderr before announcing an HTTP endpoint."));
        }
        catch (Exception error)
        {
            _endpoint.TrySetException(error);
            throw;
        }
    }

    private bool ObserveError(string line, bool lifetime, bool truncated)
    {
        var diagnostic = Redact(line);
        if (truncated || diagnostic.Length > 2000)
        {
            diagnostic = diagnostic[..Math.Min(diagnostic.Length, 1986)] + "...[truncated]";
        }
        lock (_gate)
        {
            if (_completeErrors.Count < 200)
            {
                _completeErrors.Add(diagnostic);
            }
        }
        if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
        {
            lifetime = line.Contains("Microsoft.Hosting.Lifetime[", StringComparison.Ordinal);
        }
        var match = ListeningEndpointRegex().Match(line);
        if (lifetime && match.Success && Uri.TryCreate(match.Groups["endpoint"].Value, UriKind.Absolute, out var endpoint)
            && IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address) && endpoint.Port > 0)
        {
            _endpoint.TrySetResult(endpoint);
        }
        if (lifetime || line.StartsWith("fail:", StringComparison.Ordinal) || line.StartsWith("crit:", StringComparison.Ordinal)
            || line.StartsWith("Select 'stdio' or 'http' once", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                if (_errors.Count < 200)
                {
                    _errors.Add(diagnostic);
                }
            }
        }
        return lifetime;
    }

    private string Redact(string line)
    {
        var tokens = DiagnosticWhitespaceRegex().Split(line);
        var sensitiveName = false;
        var redactNext = false;
        for (var index = 0; index < tokens.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(tokens[index]))
            {
                continue;
            }
            if (redactNext)
            {
                tokens[index] = "***";
                redactNext = false;
                sensitiveName = false;
                continue;
            }
            var separator = tokens[index].IndexOfAny(['=', ':']);
            var name = separator > 0 ? tokens[index][..separator] : tokens[index];
            if (separator >= 0 && (IsSensitive(name) || (separator == 0 && sensitiveName)))
            {
                redactNext = separator == tokens[index].Length - 1;
                tokens[index] = tokens[index][..(separator + 1)] + "***";
            }
            sensitiveName = separator < 0 && IsSensitive(name);
        }
        var redacted = string.Concat(tokens);
        foreach (var root in _diagnosticRoots.OrderByDescending(static value => value.Length))
        {
            redacted = redacted.Replace(root, "<run>", StringComparison.OrdinalIgnoreCase);
        }
        return redacted;
    }

    private static bool IsSensitive(string name) => SensitiveNames.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<whitespace>\s+)", RegexOptions.ExplicitCapture, Timeout.Infinite)]
    private static partial Regex DiagnosticWhitespaceRegex();

    [GeneratedRegex(@"\A[ \t]*Now listening on:[ \t]+(?<endpoint>http://[^ \t\r\n]+)[ \t]*\z", RegexOptions.ExplicitCapture, Timeout.Infinite)]
    private static partial Regex ListeningEndpointRegex();
}
