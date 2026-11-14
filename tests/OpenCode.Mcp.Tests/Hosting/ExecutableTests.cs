using System.Net;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol.Client;
using OpenCode.Mcp.Tests.Support;
using OpenCode.Mcp.Tools.Native;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Hosting;

/// <summary>Characterizes the actual product executable and its owned process boundary.</summary>
[Category("Executable")]
[NotInParallel("processes")]
public sealed class ExecutableTests
{
    private readonly RealFileSystem _files = new();
    private readonly BootstrapAssertions _bootstrap = new();
    private static readonly string[] HealthPaths = ["/health", "/alive"];

    [Test]
    [Arguments("http")]
    [Arguments("stdio")]
    public async Task StartAsync_Should_Ignore_Project_Configuration_When_Launched_In_A_Project_Directory(string transport)
    {
        await using var process = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Listening or early exit, then modern bootstrap.
        var launch = Launch(["--transport", transport, "--environment", "Development"]);
        var configuration = """{"Kestrel":{"Endpoints":{"A":{"Url":"http://0.0.0.0:5099"}}},"OTEL_EXPORTER_OTLP_ENDPOINT":"http://127.0.0.1:1"}""";
        await _files.File.WriteAllTextAsync(_files.Path.Combine(launch.WorkingDirectory, "appsettings.json"), configuration, guard.Token);
        await AssertPreparedFixtureAsync(launch, configuration, guard.Token);
        await process.StartAsync(launch, guard.Token);
        if (string.Equals(transport, "http", StringComparison.Ordinal))
        {
            await Assert.That(async () =>
            {
                try
                {
                    await process.WaitForHttpEndpointAsync(guard.Token);
                }
                catch (InvalidOperationException)
                {
                    await process.WaitForExitAsync(guard.Token);
                    throw;
                }
            }).ThrowsNothing();
            await WithHttpClientAsync(process, static (_, _, _, _) => Task.CompletedTask, guard.Token);
        }
        else
        {
            var client = await process.ConnectStdioAsync(guard.Token);
            await _bootstrap.AssertBootstrapAsync(client, guard.Token);
            await process.CloseInputAsync();
            await Assert.That(await process.WaitForExitAsync(guard.Token)).IsEqualTo(0);
            await Assert.That(process.Transcript.Methods).IsEquivalentTo(BootstrapAssertions.ModernMethods, CollectionOrdering.Matching);
            await _bootstrap.AssertResultsAsync(process.Transcript.Results);
            if (launch.Container is null)
            {
                var artifact = launch.Arguments.FirstOrDefault(static argument => argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ?? launch.FileName;
                var contentRoot = _files.Path.GetDirectoryName(new PhysicalPathResolver(_files).Resolve(artifact)) ?? throw new InvalidOperationException("The product artifact has no directory.");
                await Assert.That(process.StandardError).Contains("      Content root path: " + contentRoot + _files.Path.DirectorySeparatorChar);
            }
        }
        if (launch.Container is not null)
        {
            await AssertContainerContentRootAsync(process);
        }
    }

    [Test]
    [Arguments((object)new string[0])]
    [Arguments((object)new[] { "--transport", "stdio" })]
    [Arguments((object)new[] { "--urls", "not an HTTP listener" })]
    public async Task StartAsync_Should_Keep_Stdout_Protocol_Only_When_Lifetime_Logging_Is_Enabled(string[] arguments)
    {
        await using var process = new OwnedMcpProcess(_files);
        using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Startup, discovery, list and call.
        await process.StartAsync(Launch(arguments), connect.Token);
        var client = await process.ConnectStdioAsync(connect.Token);
        await _bootstrap.AssertBootstrapAsync(client, connect.Token);
        await Assert.That(process.HasExited).IsFalse();
        await process.CloseInputAsync();
        using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Host stop and SDK reader completion after EOF.
        await Assert.That(await process.WaitForExitAsync(exit.Token)).IsEqualTo(0);
        await Assert.That(process.Transcript.Methods).IsEquivalentTo(BootstrapAssertions.ModernMethods, CollectionOrdering.Matching);
        await _bootstrap.AssertResultsAsync(process.Transcript.Results);
        await Assert.That(process.Transcript.MalformedLines).IsEquivalentTo(Array.Empty<string>(), CollectionOrdering.Matching);
        await Assert.That(process.StandardError).Contains("      Application started. Press Ctrl+C to shut down.");
        await Assert.That(process.StandardError).Contains("      Application is shutting down...");
    }

    [Test]
    [Arguments((object)new[] { "--transport", "other" })]
    [Arguments((object)new[] { "--transport", "HTTP" })]
    [Arguments((object)new[] { "--transport" })]
    [Arguments((object)new[] { "--transport", "stdio", "--transport", "stdio" })]
    [Arguments((object)new[] { "--transport=http" })]
    public async Task StartAsync_Should_Return_Two_With_Empty_Stdout_When_Selector_Is_Invalid(string[] arguments)
    {
        await using var process = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Selector refusal without closing stdin.
        await process.StartAsync(Launch(arguments), guard.Token);
        IOException? writeFailure = null;
        try
        {
            await process.StandardInput.WriteAsync(WireTranscriptTests.Request(ProtocolData.ListMethod).AsMemory(), guard.Token);
        }
        catch (IOException failure)
        {
            writeFailure = failure;
        }
        var exitCode = await process.WaitForExitAsync(guard.Token);
        if (exitCode != 2 && writeFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(writeFailure);
        }
        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(process.Transcript.ServerByteCount).IsEqualTo(0);
        await Assert.That(process.StandardError).IsEquivalentTo(
            ["Select 'stdio' or 'http' once using '--transport <value>'. (Parameter 'arguments')"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task StartAsync_Should_Expose_Modern_Http_And_Health_When_Http_Is_Selected()
        => await WithHttpAsync(async (endpoint, recorder, http, token) =>
        {
            foreach (var path in HealthPaths)
            {
                using var response = await http.GetAsync(new Uri(endpoint, path), token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(await response.Content.ReadAsStringAsync(token)).IsEqualTo("Healthy");
            }
            using var hostileOrigin = new HttpRequestMessage(HttpMethod.Post, endpoint);
            hostileOrigin.Content = new StringContent(recorder.Requests[0].GetRawText(), Encoding.UTF8, "application/json");
            hostileOrigin.Headers.Add("Origin", "http://localhost.evil.example");
            using var forbidden = await http.SendAsync(hostileOrigin, token);
            await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await forbidden.Content.ReadAsStringAsync(token)).IsEmpty();
            using var hostileHost = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "/alive"));
            hostileHost.Headers.Host = "evil.example";
            using var badHost = await http.SendAsync(hostileHost, token);
            await Assert.That(badHost.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        });

    [Test]
    public async Task DisposeAsync_Should_Release_The_Endpoint_When_The_Http_Process_Ends()
    {
        Uri? observed = null;
        await WithHttpAsync((endpoint, _, _, _) =>
        {
            observed = endpoint;
            return Task.CompletedTask;
        });
        var address = await Assert.That(observed).IsNotNull();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // Fresh connection refusal after owned forced termination.
        using var socket = new TcpClient();
        var failure = await Assert.That(async () => await socket.ConnectAsync(IPAddress.Loopback, address.Port, guard.Token))
            .ThrowsExactly<SocketException>();
        var error = await Assert.That(failure).IsNotNull();
        await Assert.That(error.SocketErrorCode).IsEqualTo(SocketError.ConnectionRefused);
    }

    private ProcessLaunch Launch(IReadOnlyList<string> arguments)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var executable = Environment.GetEnvironmentVariable("OPENCODE_MCP_TEST_EXECUTABLE");
        if (executable is not null)
        {
            environment.Add("OPENCODE_MCP_TEST_EXECUTABLE", executable);
        }
        var expectNative = Environment.GetEnvironmentVariable("OPENCODE_MCP_TEST_EXPECT_NATIVE");
        if (expectNative is not null)
        {
            environment.Add("OPENCODE_MCP_TEST_EXPECT_NATIVE", expectNative);
        }
        var containerImage = Environment.GetEnvironmentVariable("OPENCODE_MCP_TEST_CONTAINER_IMAGE");
        if (containerImage is not null)
        {
            environment.Add("OPENCODE_MCP_TEST_CONTAINER_IMAGE", containerImage);
        }
        var launch = new ProductExecutable(_files).Resolve(environment);
        return launch with { Arguments = [.. launch.Arguments, .. arguments] };
    }

    private static async Task AssertContainerContentRootAsync(OwnedMcpProcess process) =>
        await Assert.That(process.StandardError).Contains("      Content root path: /opencode-mcp-payload/");

    private async Task AssertPreparedFixtureAsync(ProcessLaunch launch, string configuration, CancellationToken token)
    {
        if (launch.Container is not { } container)
        {
            await Assert.That(await _files.File.ReadAllTextAsync(_files.Path.Combine(launch.WorkingDirectory, "appsettings.json"), token)).IsEqualTo(configuration);
            return;
        }
        var runner = new SystemNativeProcessRunner();
        var probe = launch with
        {
            Container = container with { Name = "opencode-mcp-test-" + Guid.NewGuid().ToString("N"), Executable = "/bin/sh" },
            Arguments = ["-c", "pwd; cat appsettings.json"],
        };
        var request = await new ContainerLaunchBuilder(_files, runner).BuildAsync(probe,
            new Dictionary<string, string?>(new System.Diagnostics.ProcessStartInfo().Environment, StringComparer.Ordinal), token);
        request = request with { Arguments = [.. request.Arguments.Where(static argument => argument != "-i")] };
        var owner = new NativeContainerOwner(runner, request, probe.Container.Name);
        Exception? failure = null;
        try
        {
            var result = await runner.RunAsync(request with { Container = owner }, token);
            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.StandardError).IsEmpty();
            await Assert.That(result.StandardOutput).IsEqualTo("/opencode-mcp-run/work\n" + configuration);
        }
        catch (Exception original)
        {
            failure = original;
        }
        var failures = await owner.CleanupAsync(failure);
        if (failures.Count != 0)
        {
            if (owner.AbsenceProven)
            {
                _files.Directory.Delete(launch.OwnedRoot, recursive: true);
            }
            throw new AggregateException("Prepared container fixture probe failed; root: " + launch.OwnedRoot, failures);
        }
    }

    private async Task WithHttpAsync(Func<Uri, RecordingHttpHandler, HttpClient, CancellationToken, Task> observation)
    {
        await using var process = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Listening announcement and modern bootstrap.
        await process.StartAsync(Launch(["--transport", "http", "--environment", "Development"]), guard.Token);
        await WithHttpClientAsync(process, observation, guard.Token);
        // HTTP has no portable graceful-stop seam here. The owner forces termination only in teardown.
    }

    private async Task WithHttpClientAsync(OwnedMcpProcess process, Func<Uri, RecordingHttpHandler, HttpClient, CancellationToken, Task> observation,
        CancellationToken token)
    {
        var address = await process.WaitForHttpEndpointAsync(token);
        await Assert.That(address.Host).IsEqualTo("127.0.0.1");
        await Assert.That(address.Port).IsGreaterThan(0);
        var endpoint = new Uri(address, "/mcp");
        using var sockets = new SocketsHttpHandler { UseProxy = false };
        using var recorder = new RecordingHttpHandler(sockets, 1_048_576);
        using var http = new HttpClient(recorder, disposeHandler: false);
        http.DefaultRequestHeaders.Add("Origin", "https://localhost:1234");
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http);
        await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ProtocolVersion = ProtocolData.Revision,
            DiscoverProbeTimeout = TimeSpan.FromSeconds(30),
            InitializationTimeout = TimeSpan.FromSeconds(30),
        }, cancellationToken: token);
        await _bootstrap.AssertBootstrapAsync(client, token);
        await Assert.That(recorder.Requests.Select(static frame => frame.GetProperty("method").GetString()))
            .IsEquivalentTo(BootstrapAssertions.ModernMethods, CollectionOrdering.Matching);
        await _bootstrap.AssertResultsAsync([.. recorder.Responses.Select(static frame => frame.GetProperty("result"))]);
        foreach (var headers in recorder.RequestHeaders.Concat(recorder.ResponseHeaders))
        {
            await Assert.That(headers.Keys).DoesNotContain("Mcp-Session-Id", StringComparer.OrdinalIgnoreCase);
            await Assert.That(headers.Keys).DoesNotContain("Access-Control-Allow-Origin", StringComparer.OrdinalIgnoreCase);
        }
        await observation(endpoint, recorder, http, token);
    }
}
