using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Win32.SafeHandles;
using Testably.Abstractions;

namespace OpenCode.Mcp.AppHost.Tests.Support;

/// <summary>Proves actual product-process ownership through AppHost teardown and failure paths.</summary>
[NotInParallel("processes")]
public sealed class OwnedAppHostTests
{
    private readonly RealFileSystem _files = new();
    private const string SentinelContents = "unrelated sibling data";

    /// <summary>Only isolation paths and proxy bypass override the product's authored environment and supplied Aspire home.</summary>
    [Test]
    public async Task StartAsync_Should_Override_Only_Isolation_Variables_When_Preparing_The_Product()
    {
        using var wholeTest = new CancellationTokenSource(TimeSpan.FromMinutes(20)); // Product environment preparation and observation.
        var builder = await CreateBuilderAsync(wholeTest.Token);
        Dictionary<string, object> authored;
        try
        {
            builder.CreateResourceBuilder(builder.Resources.OfType<ProjectResource>().Single())
                .WithEnvironment("OTEL_RESOURCE_ATTRIBUTES", "service.instance.id=authored-instance")
                .WithEnvironment("OTEL_SERVICE_NAME", "authored-product");
            authored = await ReadAuthoredEnvironmentAsync(builder, wholeTest.Token);
        }
        catch
        {
            await builder.DisposeAsync();
            throw;
        }
        await using var owner = new OwnedAppHost(_files, builder);
        var environment = await ReadAuthoredEnvironmentAsync(builder, wholeTest.Token);
        foreach (var group in OwnedAppHost.IsolationGroups)
        {
            foreach (var name in group.Skip(1))
            {
                await Assert.That(environment[name]).IsEqualTo(_files.Path.Combine(owner.OwnedRoot, group[0]));
            }
        }
        await Assert.That(environment["NO_PROXY"]).IsEqualTo("localhost,127.0.0.1,::1");
        await Assert.That(environment["ASPIRE_HOME"]).IsEqualTo(Environment.GetEnvironmentVariable("ASPIRE_HOME"));
        var ownedNames = OwnedAppHost.IsolationGroups.SelectMany(static group => group.Skip(1))
            .Concat(["NO_PROXY", "ASPIRE_HOME"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedNames = authored.Keys.Concat(ownedNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await Assert.That(environment.Keys).IsEquivalentTo(expectedNames).Using(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<EnvironmentVariableSnapshot>? childEnvironment = null;
        builder.Eventing.Subscribe<ResourceReadyEvent>(builder.Resources.OfType<ProjectResource>().Single(), (ready, _) =>
        {
            var notifications = ready.Services.GetService(typeof(ResourceNotificationService)) as ResourceNotificationService
                ?? throw new InvalidOperationException("Resource notifications are unavailable.");
            if (!notifications.TryGetCurrentState("mcp", out var update))
            {
                throw new InvalidOperationException("The ready product has no resource snapshot.");
            }
            childEnvironment = update.Snapshot.EnvironmentVariables;
            return Task.CompletedTask;
        });
        await owner.StartAsync(wholeTest.Token);
        await Assert.That(childEnvironment).IsNotNull();
        var observed = childEnvironment ?? throw new InvalidOperationException("The ready product's environment was not observed.");
        await Assert.That(expectedNames.Except(observed.Select(static variable => variable.Name), StringComparer.OrdinalIgnoreCase))
            .IsEmpty();
        foreach (var group in OwnedAppHost.IsolationGroups)
        {
            foreach (var name in group.Skip(1))
            {
                await Assert.That(observed.Single(variable => variable.Name == name).Value)
                    .IsEqualTo(_files.Path.Combine(owner.OwnedRoot, group[0]));
            }
        }
        await Assert.That(observed.Single(static variable => variable.Name == "ASPIRE_HOME").Value)
            .IsEqualTo(Environment.GetEnvironmentVariable("ASPIRE_HOME"));
        foreach (var (name, value) in authored.Where(entry => !ownedNames.Contains(entry.Key)))
        {
            var expected = value is IValueProvider provider ? await provider.GetValueAsync(wholeTest.Token) : value.ToString();
            await Assert.That(observed.Single(variable => variable.Name == name).Value).IsEqualTo(expected);
        }
    }

    /// <summary>Disposal ends recorded products, closes owner handles and releases the discovered listener.</summary>
    [Test]
    public async Task DisposeAsync_Should_Release_Product_Processes_And_The_Endpoint_When_The_AppHost_Is_Disposed()
    {
        using var wholeTest = new CancellationTokenSource(TimeSpan.FromMinutes(20)); // Complete lifecycle proof.
        var builder = await CreateBuilderAsync(wholeTest.Token);
        var owner = new OwnedAppHost(_files, builder);
        Uri? endpoint = null;
        SafeProcessHandle[] handles = [];
        Process[] independent = [];
        Task[] exits = [];
        try
        {
            Exception? disposalFailure = null;
            try
            {
                await using (owner)
                {
                    await owner.StartAsync(wholeTest.Token);
                    endpoint = owner.Endpoint;
                    handles = [.. owner.ProductProcesses.Select(static process => process.SafeHandle)];
                    independent = [.. owner.ProductProcesses.Select(RetainIndependentProcess)];
                    exits = [.. independent.Select(static process => process.WaitForExitAsync())];
                }
            }
            catch (Exception error)
            {
                if (endpoint is null)
                {
                    throw;
                }
                disposalFailure = error;
            }
            await Assert.That(independent).IsNotEmpty();
            foreach (var process in independent)
            {
                await Assert.That(process.HasExited).IsTrue();
            }
            await Task.WhenAll(exits).WaitAsync(wholeTest.Token);
            foreach (var handle in handles)
            {
                await Assert.That(handle.IsClosed).IsTrue();
            }
            var address = await Assert.That(endpoint).IsNotNull();
            await AssertEndpointReleasedAsync(address, wholeTest.Token);
            await Assert.That(_files.Directory.Exists(owner.OwnedRoot)).IsFalse();
            await Assert.That(disposalFailure).IsNull();
        }
        finally
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }
            if (endpoint is not null)
            {
                await ReleaseEvidenceAsync(owner, independent, exits, wholeTest.Token);
            }
        }
    }

    /// <summary>A real refused HTTP request unwinds through disposal without deleting sibling files.</summary>
    [Test]
    public async Task DisposeAsync_Should_Preserve_Unrelated_Files_When_A_Request_Fails()
    {
        using var wholeTest = new CancellationTokenSource(TimeSpan.FromMinutes(20)); // Failed request and completed cleanup.
        var builder = await CreateBuilderAsync(wholeTest.Token);
        await using var owner = new OwnedAppHost(_files, builder);
        await owner.StartAsync(wholeTest.Token);
        await WithSentinelAsync(owner, async sentinel =>
        {
            var independent = owner.ProductProcesses.Select(RetainIndependentProcess).ToArray();
            var exits = independent.Select(static process => process.WaitForExitAsync()).ToArray();
            try
            {
                var failure = await Assert.That(async () =>
                {
                    await using (owner)
                    {
                        using var sockets = new SocketsHttpHandler { UseProxy = false };
                        using var http = new HttpClient(sockets) { Timeout = Timeout.InfiniteTimeSpan };
                        using var request = CancellationTokenSource.CreateLinkedTokenSource(wholeTest.Token);
                        request.CancelAfter(TimeSpan.FromSeconds(30)); // An actual HTTP 404, not a timeout failure.
                        using var response = await http.GetAsync(new Uri(owner.Endpoint, "/missing"), request.Token);
                        response.EnsureSuccessStatusCode();
                    }
                }).ThrowsExactly<HttpRequestException>();
                var error = await Assert.That(failure).IsNotNull();
                await Assert.That(error.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                await Assert.That(_files.File.Exists(sentinel)).IsTrue();
                await Assert.That(await _files.File.ReadAllTextAsync(sentinel, wholeTest.Token)).IsEqualTo(SentinelContents);
                await AssertReleasedAsync(owner, independent, wholeTest.Token);
            }
            finally
            {
                await ReleaseEvidenceAsync(owner, independent, exits, wholeTest.Token);
            }
        });
    }

    /// <summary>A retained resource-ready failure propagates after the started product and its files are released.</summary>
    [Test]
    public async Task StartAsync_Should_Release_Started_Product_Processes_When_A_Readiness_Callback_Fails()
    {
        using var wholeTest = new CancellationTokenSource(TimeSpan.FromMinutes(20)); // Ready-callback failure and cleanup.
        var builder = await CreateBuilderAsync(wholeTest.Token);
        await using var owner = new OwnedAppHost(_files, builder);
        var expected = new InvalidOperationException("resource-ready callback failed");
        Process? independent = null;
        var exit = Task.CompletedTask;
        builder.Eventing.Subscribe<ResourceReadyEvent>(builder.Resources.OfType<ProjectResource>().Single(), async (ready, _) =>
        {
            var notifications = ready.Services.GetService(typeof(ResourceNotificationService)) as ResourceNotificationService
                ?? throw new InvalidOperationException("Resource notifications are unavailable.");
            await Assert.That(notifications.TryGetCurrentState("mcp", out var update)).IsTrue();
            var snapshot = await Assert.That(update).IsNotNull();
            var pid = Convert.ToInt32(snapshot.Snapshot.Properties.Single(static property => property.Name == "executable.pid").Value,
                System.Globalization.CultureInfo.InvariantCulture);
            using var observed = Process.GetProcessById(pid);
            independent = RetainIndependentProcess(observed);
            // Independent evidence must outlive the resource callback's cancellation on shutdown.
            exit = independent.WaitForExitAsync(CancellationToken.None);
            throw expected;
        });
        try
        {
            var actual = await Assert.That(() => owner.StartAsync(wholeTest.Token)).ThrowsExactly<InvalidOperationException>();
            await Assert.That(actual).IsSameReferenceAs(expected);
            var process = await Assert.That(independent).IsNotNull();
            await AssertReleasedAsync(owner, [process], wholeTest.Token);
            await exit.WaitAsync(wholeTest.Token);
        }
        finally
        {
            if (independent is not null)
            {
                await ReleaseEvidenceAsync(owner, [independent], [exit], wholeTest.Token);
            }
        }
    }

    /// <summary>Explicit cancellation propagates and removes only the owner's prepared files.</summary>
    [Test]
    public async Task StartAsync_Should_Remove_Owned_Files_When_Startup_Is_Cancelled()
    {
        using var wholeTest = new CancellationTokenSource(TimeSpan.FromMinutes(20)); // Cancelled startup and owned-root deletion.
        var builder = await CreateBuilderAsync(wholeTest.Token);
        await using var owner = new OwnedAppHost(_files, builder);
        await WithSentinelAsync(owner, async sentinel =>
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.That(_files.Directory.Exists(owner.OwnedRoot)).IsTrue();
            var failure = await Assert.That(() => owner.StartAsync(cancelled.Token)).ThrowsExactly<OperationCanceledException>();
            var error = await Assert.That(failure).IsNotNull();
            await Assert.That(error.CancellationToken).IsEqualTo(cancelled.Token);
            await Assert.That(_files.Directory.Exists(owner.OwnedRoot)).IsFalse();
            await Assert.That(await _files.File.ReadAllTextAsync(sentinel, wholeTest.Token)).IsEqualTo(SentinelContents);
        });
    }

    /// <summary>Repeated disposal keeps completed process evidence and leaves the sibling sentinel unchanged.</summary>
    [Test]
    public async Task DisposeAsync_Should_Complete_Idempotently_When_Disposal_Is_Repeated()
    {
        using var wholeTest = new CancellationTokenSource(TimeSpan.FromMinutes(20)); // Repeated disposal and retained exit evidence.
        var builder = await CreateBuilderAsync(wholeTest.Token);
        await using var owner = new OwnedAppHost(_files, builder);
        await owner.StartAsync(wholeTest.Token);
        await WithSentinelAsync(owner, async sentinel =>
        {
            var handles = owner.ProductProcesses.Select(static process => process.SafeHandle).ToArray();
            var independent = owner.ProductProcesses.Select(RetainIndependentProcess).ToArray();
            var exits = independent.Select(static process => process.WaitForExitAsync()).ToArray();
            try
            {
                await owner.DisposeAsync();
                await owner.DisposeAsync();
                await AssertReleasedAsync(owner, independent, wholeTest.Token);
                foreach (var handle in handles)
                {
                    await Assert.That(handle.IsClosed).IsTrue();
                }
                await Assert.That(owner.ProductProcesses.Count).IsEqualTo(independent.Length);
                await Assert.That(await _files.File.ReadAllTextAsync(sentinel, wholeTest.Token)).IsEqualTo(SentinelContents);
            }
            finally
            {
                foreach (var handle in handles)
                {
                    handle.Dispose();
                }
                await ReleaseEvidenceAsync(owner, independent, exits, wholeTest.Token);
            }
        });
    }

    private async Task WithSentinelAsync(OwnedAppHost owner, Func<string, Task> action)
    {
        var sibling = owner.OwnedRoot + "-sibling";
        _files.Directory.CreateDirectory(sibling);
        var sentinel = _files.Path.Combine(sibling, "sentinel.txt");
        await _files.File.WriteAllTextAsync(sentinel, SentinelContents);
        try
        {
            await action(sentinel);
        }
        finally
        {
            if (_files.Directory.Exists(sibling))
            {
                _files.Directory.Delete(sibling, recursive: true);
            }
        }
    }

    private async Task AssertReleasedAsync(OwnedAppHost owner, IReadOnlyList<Process> independent, CancellationToken token)
    {
        await Assert.That(independent).IsNotEmpty();
        foreach (var process in independent)
        {
            await Assert.That(process.HasExited).IsTrue();
        }
        await AssertEndpointReleasedAsync(owner.Endpoint, token);
        await Assert.That(_files.Directory.Exists(owner.OwnedRoot)).IsFalse();
    }

    private void RemoveOwnedFiles(OwnedAppHost owner)
    {
        if (_files.Directory.Exists(owner.OwnedRoot))
        {
            _files.Directory.Delete(owner.OwnedRoot, recursive: true);
        }
    }

    private async Task ReleaseEvidenceAsync(OwnedAppHost owner, IReadOnlyList<Process> processes,
        IReadOnlyList<Task> exits, CancellationToken token)
    {
        var failures = new List<Exception>();
        foreach (var process in processes)
        {
            TryCleanup(() => KillIfRunning(process), failures);
        }
        try
        {
            await Task.WhenAll(exits).WaitAsync(token);
            TryCleanup(() => RemoveOwnedFiles(owner), failures);
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        foreach (var process in processes)
        {
            TryCleanup(process.Dispose, failures);
        }
        if (failures.Count != 0)
        {
            throw new AggregateException(failures);
        }
    }

    private static void TryCleanup(Action action, List<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
    }

    private static Process RetainIndependentProcess(Process observed)
    {
        var process = Process.GetProcessById(observed.Id);
        _ = process.SafeHandle;
        _ = process.StartTime;
        return process;
    }

    private static void KillIfRunning(Process process)
    {
        if (!process.HasExited)
        {
            using var current = Process.GetProcessById(process.Id);
            if (current.StartTime != process.StartTime)
            {
                throw new InvalidOperationException("The retained product PID now belongs to a different process.");
            }
            process.Kill();
        }
    }

    internal static async Task<IDistributedApplicationTestingBuilder> CreateBuilderAsync(CancellationToken token)
    {
        using var creation = CancellationTokenSource.CreateLinkedTokenSource(token);
        creation.CancelAfter(TimeSpan.FromSeconds(360)); // Resolve the actual AppHost entry point.
        return await DistributedApplicationTestingBuilder.CreateAsync<Projects.OpenCode_Mcp_AppHost>(
            new DistributedApplicationTestingBuilderOptions { EnableDashboard = false }, [], creation.Token);
    }

    internal static async Task<Dictionary<string, object>> ReadAuthoredEnvironmentAsync(IDistributedApplicationTestingBuilder builder,
        CancellationToken token)
    {
        var environment = new Dictionary<string, object>(StringComparer.Ordinal);
        var product = builder.Resources.OfType<ProjectResource>().Single();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, product, environment, token);
        foreach (var annotation in product.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }
        return environment;
    }

    internal static async Task AssertEndpointReleasedAsync(Uri endpoint, CancellationToken token)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        request.CancelAfter(TimeSpan.FromSeconds(30)); // Fresh TCP connection refusal after disposal.
        using var socket = new TcpClient();
        var failure = await Assert.That(async () => await socket.ConnectAsync(IPAddress.Loopback, endpoint.Port, request.Token))
            .ThrowsExactly<SocketException>();
        var error = await Assert.That(failure).IsNotNull();
        await Assert.That(error.SocketErrorCode).IsEqualTo(SocketError.ConnectionRefused);
    }
}
