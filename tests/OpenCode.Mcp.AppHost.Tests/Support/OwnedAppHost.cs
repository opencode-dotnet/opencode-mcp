using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace OpenCode.Mcp.AppHost.Tests.Support;

/// <summary>Owns an actual AppHost run and retains its product-process evidence through cleanup.</summary>
internal sealed class OwnedAppHost : IAsyncDisposable
{
    internal static IReadOnlyList<IReadOnlyList<string>> IsolationGroups { get; } = Array.AsReadOnly<IReadOnlyList<string>>(
    [
        Array.AsReadOnly(["home", "HOME", "USERPROFILE", "DOTNET_CLI_HOME"]),
        Array.AsReadOnly(["config", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "OPENCODE_CONFIG_DIR"]),
        Array.AsReadOnly(["cache", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME"]),
        Array.AsReadOnly(["temp", "TEMP", "TMP", "TMPDIR", "XDG_RUNTIME_DIR"]),
    ]);
    private readonly IFileSystem _files;
    private readonly IDistributedApplicationTestingBuilder _builder;
    private readonly CancellationTokenSource _observation = new();
    private readonly Dictionary<Process, DateTime> _processes = [];
    private readonly List<Task> _exits = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _productObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DistributedApplication? _app;
    private Task _observer = Task.CompletedTask;
    private Task? _dispose;
    private Uri? _endpoint;

    /// <summary>Accepts ownership of the actual testing builder and filesystem-backed run directories.</summary>
    public OwnedAppHost(IFileSystem fileSystem, IDistributedApplicationTestingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(builder);
        _files = fileSystem;
        _builder = builder;
        try
        {
            var product = builder.CreateResourceBuilder(builder.Resources.OfType<ProjectResource>().Single());
            var executable = ResolveProductExecutable(product.Resource);
            OwnedRoot = _files.Path.Combine(_files.Path.GetTempPath(), "opencode-mcp-" + Guid.NewGuid().ToString("N"));
            var work = _files.Path.Combine(OwnedRoot, "work");
            _files.Directory.CreateDirectory(work);
            product.WithAnnotation(new ExecutableAnnotation { Command = executable, WorkingDirectory = work })
                .WithEnvironment(PrepareChildEnvironment);
        }
        catch (Exception original)
        {
            try
            {
                builder.Dispose();
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(original, cleanup);
            }
            finally
            {
                _observation.Dispose();
            }
            throw;
        }
    }

    /// <summary>Gets the allocated HTTP authority after successful readiness.</summary>
    public Uri Endpoint => _endpoint ?? throw new InvalidOperationException("The product has not reached readiness.");

    /// <summary>Gets a read-only snapshot of retained product process handles.</summary>
    internal IReadOnlyList<Process> ProductProcesses
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_processes.Keys.ToArray());
            }
        }
    }

    /// <summary>Gets the directory whose files this owner alone removes.</summary>
    internal string OwnedRoot { get; }

    /// <summary>Builds and starts the application, retaining process ownership before waiting for readiness.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _app = await _builder.BuildAsync(cancellationToken);
            _observer = ObserveResourcesAsync(_app, _observation.Token);
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(180)); // DCP startup and resource creation.
                await _app.StartAsync(startup.Token);
            }
            _endpoint = _app.GetEndpoint("mcp", "http");
            using var health = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            health.CancelAfter(TimeSpan.FromSeconds(120)); // Product health and resource-ready callbacks.
            var ready = _app.ResourceNotifications.WaitForResourceHealthyAsync("mcp", health.Token);
            await Task.WhenAny(ready, _observer).Unwrap();
            await ready;
            await _productObserved.Task.WaitAsync(health.Token);
        }
        catch (Exception original)
        {
            try
            {
                await DisposeAsync();
            }
            catch (Exception cleanup)
            {
                _dispose = Task.CompletedTask; // Startup reports both failures; later disposal must not replace them.
                throw new AggregateException(original, cleanup);
            }
            throw;
        }
    }

    /// <summary>Completes shutdown, exit waits and observer cleanup before releasing owned files.</summary>
    public ValueTask DisposeAsync() => new(_dispose ??= CleanupAsync());

    private async Task ObserveResourcesAsync(DistributedApplication app, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in app.ResourceNotifications.WatchAsync(cancellationToken))
            {
                if (update.Resource.Name == "mcp-rebuilder")
                {
                    if (update.Snapshot.State?.Text == KnownResourceStates.Running || update.Snapshot.State?.Text == KnownResourceStates.Starting
                        || update.Snapshot.Properties.Any(static property => property.Name == "executable.pid"
                            && Convert.ToInt32(property.Value, CultureInfo.InvariantCulture) > 0))
                    {
                        throw new InvalidOperationException("The hidden mcp-rebuilder must never start.");
                    }
                }
                else if (ReferenceEquals(update.Resource, _builder.Resources.OfType<ProjectResource>().Single()))
                {
                    var pid = update.Snapshot.Properties.SingleOrDefault(static property => property.Name == "executable.pid")?.Value;
                    if (pid is not null && Convert.ToInt32(pid, CultureInfo.InvariantCulture) > 0)
                    {
                        RetainProcess(Convert.ToInt32(pid, CultureInfo.InvariantCulture));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return; // This owner's cancellation ends and joins its observer during cleanup.
        }
    }

    private void RetainProcess(int pid)
    {
        lock (_gate)
        {
            if (_processes.Keys.All(process => process.Id != pid))
            {
                var process = Process.GetProcessById(pid);
                try
                {
                    _ = process.SafeHandle;
                    _processes.Add(process, process.StartTime);
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    process.Dispose();
                    throw new InvalidOperationException("Cannot retain the observed product PID " + pid.ToString(CultureInfo.InvariantCulture), error);
                }
                // Ownership outlives startup cancellation; cleanup observes every exit task.
                _exits.Add(process.WaitForExitAsync(CancellationToken.None));
                _productObserved.TrySetResult();
            }
        }
    }

    private async Task CleanupAsync()
    {
        var failures = new List<Exception>();
        if (_app is not null)
        {
            await AwaitCleanupStepAsync(() => _app.DisposeAsync().AsTask(), TimeSpan.FromSeconds(60),
                "AppHost disposal did not finish within 60 s.", failures);
        }
        await AwaitCleanupStepAsync(() => _builder.DisposeAsync().AsTask(), TimeSpan.FromSeconds(30),
            "AppHost builder disposal did not finish within 30 s.", failures);
        await StopProductsAsync(failures);
        try
        {
            if (failures.Count != 0)
            {
                failures.Add(new InvalidOperationException("AppHost cleanup failed; kept owned root: " + OwnedRoot));
            }
            else if (_files.Directory.Exists(OwnedRoot))
            {
                _files.Directory.Delete(OwnedRoot, recursive: true);
            }
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        if (failures.Count != 0)
        {
            throw new AggregateException(failures);
        }
    }

    private async Task StopProductsAsync(List<Exception> failures)
    {
        using (var observerJoin = new CancellationTokenSource(TimeSpan.FromSeconds(10))) // Independent resource-observer join.
        {
            try
            {
                await _observation.CancelAsync();
                await _observer.WaitAsync(observerJoin.Token);
            }
            catch (OperationCanceledException error) when (observerJoin.IsCancellationRequested)
            {
                failures.Add(new TimeoutException("AppHost observer join did not finish within 10 s.", error));
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }
        KeyValuePair<Process, DateTime>[] processes;
        Task[] exits;
        lock (_gate)
        {
            processes = [.. _processes];
            exits = [.. _exits];
        }
        foreach (var (process, startTime) in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    using var current = Process.GetProcessById(process.Id);
                    if (current.StartTime != startTime)
                    {
                        throw new InvalidOperationException("The recorded product PID now belongs to a different process.");
                    }
                    failures.Add(new InvalidOperationException("AppHost disposal left a recorded product process running."));
                    process.Kill(); // The product has no children; never kill a PID's process tree.
                }
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }
        try
        {
            await AwaitCleanupStepAsync(() => Task.WhenAll(exits), TimeSpan.FromSeconds(30),
                "Product exit waits did not finish within 30 s.", failures);
        }
        finally
        {
            foreach (var (process, _) in processes)
            {
                process.Dispose();
            }
            _observation.Dispose();
        }
    }

    private static async Task AwaitCleanupStepAsync(Func<Task> action, TimeSpan bound, string timeoutMessage, List<Exception> failures)
    {
        var task = Task.CompletedTask;
        try
        {
            task = action();
            await task.WaitAsync(bound);
        }
        catch (TimeoutException error) when (!task.IsCompleted)
        {
            failures.Add(new TimeoutException(timeoutMessage, error));
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
    }

    private void PrepareChildEnvironment(EnvironmentCallbackContext context)
    {
        foreach (var group in IsolationGroups)
        {
            var path = _files.Path.Combine(OwnedRoot, group[0]);
            _files.Directory.CreateDirectory(path);
            foreach (var name in group.Skip(1))
            {
                context.EnvironmentVariables[name] = path;
            }
        }
        context.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1,::1";
        var aspireHome = Environment.GetEnvironmentVariable("ASPIRE_HOME");
        context.EnvironmentVariables["ASPIRE_HOME"] = !string.IsNullOrEmpty(aspireHome) ? aspireHome
            : throw new InvalidOperationException("AppHost tests require an owned ASPIRE_HOME before building or testing.");
    }

    private string ResolveProductExecutable(ProjectResource product)
    {
        var project = product.GetProjectMetadata().ProjectPath;
        var directory = _files.Path.GetDirectoryName(project) ?? throw new InvalidOperationException("The product project has no directory.");
        // Both projects inherit the same target framework from the root build properties.
        var framework = "net" + new FrameworkName(AppContext.TargetFrameworkName
            ?? throw new InvalidOperationException("The test executable has no target framework.")).Version.ToString(2);
        var executable = _files.Path.Combine(directory, "bin", "Release", framework,
            _files.Path.GetFileNameWithoutExtension(project) + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        if (!_files.File.Exists(executable))
        {
            throw new InvalidOperationException("Build the Release product before AppHost tests; missing artifact: " + executable);
        }
        return executable;
    }

}
