using System.Diagnostics;
using System.IO.Abstractions;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Owns a child process, redirected streams and isolated files through completed cleanup.</summary>
internal sealed partial class OwnedMcpProcess : IAsyncDisposable
{
    private static readonly string[] LaunchVariables = ["PATH", "SystemRoot", "WINDIR", "ComSpec", "PATHEXT", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64",
        "DOTNET_HOST_PATH", "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE"];
    private static readonly string[][] IsolationGroups =
    [
        ["home", "HOME", "USERPROFILE", "DOTNET_CLI_HOME"],
        ["config", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "OPENCODE_CONFIG_DIR"],
        ["cache", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME"],
        ["temp", "TEMP", "TMP", "TMPDIR", "XDG_RUNTIME_DIR"],
    ];
    private static readonly string[] SensitiveNames = ["TOKEN", "KEY", "SECRET", "PASSWORD"];
    private readonly IFileSystem _files;
    private readonly Process _process = new();
    private readonly List<string> _errors = [];
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
    private bool _started;
    private bool _startAttempted;

    /// <summary>Receives the filesystem used for the child's isolated run.</summary>
    public OwnedMcpProcess(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _files = fileSystem;
    }

    /// <summary>Gets the child's recorded protocol observations.</summary>
    public WireTranscript Transcript { get; } = new();

    internal RecordingDuplexStream StandardInput => _input ?? throw new InvalidOperationException("The child has not started.");
    internal bool HasExited => _process.HasExited;
    internal bool DiagnosticsCompleted => _drain.IsCompletedSuccessfully;
    internal bool ExitCompleted => _exit.IsCompletedSuccessfully;
    internal bool OutputDrainStarted => _stdoutDrain is not null;
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
            cancellationToken.ThrowIfCancellationRequested();
            _files.Directory.CreateDirectory(launch.WorkingDirectory);
            var start = new ProcessStartInfo(launch.FileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = launch.WorkingDirectory,
            };
            foreach (var argument in launch.Arguments)
            {
                start.ArgumentList.Add(argument);
            }
            ConfigureEnvironment(start, launch.Environment);
            _process.StartInfo = start;
            _started = _process.Start();
            // The owner observes this task through exit or forced cleanup, independent of startup cancellation.
            _exit = _process.WaitForExitAsync(CancellationToken.None);
            _input = new RecordingDuplexStream(_process.StandardInput.BaseStream, Transcript, fromServer: false);
            _output = new RecordingDuplexStream(_process.StandardOutput.BaseStream, Transcript, fromServer: true);
            _stderr = _process.StandardError;
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

    private async Task CleanupAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // Forced termination and owned reader completion.
        try
        {
            if (_started)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
                await Task.WhenAll(_exit, _drain, DrainOutput()).WaitAsync(guard.Token);
            }
            if (_client is not null)
            {
                await _client.DisposeAsync().AsTask().WaitAsync(guard.Token);
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

    private Task DrainOutput() => _client is null && _output is not null
        ? _stdoutDrain ??= _output.CopyToAsync(Stream.Null)
        : Task.CompletedTask;

    private void ConfigureEnvironment(ProcessStartInfo start, IReadOnlyDictionary<string, string?> explicitInputs)
    {
        var inherited = new Dictionary<string, string?>(start.Environment, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
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
            var path = _files.Path.Combine(_root ?? throw new InvalidOperationException("No owned run root."), group[0]);
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
            start.Environment[name] = value;
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
            var selected = Redact(line);
            if (truncated || selected.Length > 2000)
            {
                selected = selected[..Math.Min(selected.Length, 1986)] + "...[truncated]";
            }
            lock (_gate)
            {
                if (_errors.Count < 200)
                {
                    _errors.Add(selected);
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
        return string.Concat(tokens).Replace(_root ?? throw new InvalidOperationException("No owned run root."), "<run>", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSensitive(string name) => SensitiveNames.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<whitespace>\s+)", RegexOptions.ExplicitCapture, Timeout.Infinite)]
    private static partial Regex DiagnosticWhitespaceRegex();

    [GeneratedRegex(@"\A[ \t]*Now listening on:[ \t]+(?<endpoint>http://[^ \t\r\n]+)[ \t]*\z", RegexOptions.ExplicitCapture, Timeout.Infinite)]
    private static partial Regex ListeningEndpointRegex();
}
