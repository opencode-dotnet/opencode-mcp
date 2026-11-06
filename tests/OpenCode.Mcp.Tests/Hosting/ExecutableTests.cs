using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenCode.Mcp.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Hosting;

/// <summary>Characterizes the actual product executable and its owned process boundary.</summary>
[Category("Executable")]
[NotInParallel("processes")]
public sealed class ExecutableTests
{
    private readonly RealFileSystem _files = new();
    private static readonly string?[] ModernMethods = [ProtocolData.DiscoverMethod, ProtocolData.ListMethod, ProtocolData.CallMethod];
    private static readonly string?[] CompleteResults = [ProtocolData.CompleteResult, ProtocolData.CompleteResult, ProtocolData.CompleteResult];
    private static readonly string?[] SupportedVersions = [ProtocolData.Revision];
    private static readonly string[] HealthPaths = ["/health", "/alive"];

    [Test]
    [Arguments("http")]
    [Arguments("stdio")]
    public async Task StartAsync_Should_Ignore_Project_Configuration_When_Launched_In_A_Project_Directory(string transport)
    {
        await using var process = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Listening or early exit, then modern bootstrap.
        var launch = Launch(["--transport", transport, "--environment", "Development"]);
        await _files.File.WriteAllTextAsync(_files.Path.Combine(launch.WorkingDirectory, "appsettings.json"),
            """{"Kestrel":{"Endpoints":{"A":{"Url":"http://0.0.0.0:5099"}}},"OTEL_EXPORTER_OTLP_ENDPOINT":"http://127.0.0.1:1"}""", guard.Token);
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
            await AssertBootstrapAsync(client, guard.Token);
            await process.CloseInputAsync();
            await Assert.That(await process.WaitForExitAsync(guard.Token)).IsEqualTo(0);
            await Assert.That(process.Transcript.Methods).IsEquivalentTo(ModernMethods, CollectionOrdering.Matching);
            await AssertResultsAsync(process.Transcript.Results);
            var artifact = launch.Arguments.FirstOrDefault(static argument => argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ?? launch.FileName;
            await Assert.That(process.StandardError).Contains("      Content root path: " + _files.Path.GetDirectoryName(artifact) + _files.Path.DirectorySeparatorChar);
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
        await AssertBootstrapAsync(client, connect.Token);
        await Assert.That(process.HasExited).IsFalse();
        await process.CloseInputAsync();
        using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Host stop and SDK reader completion after EOF.
        await Assert.That(await process.WaitForExitAsync(exit.Token)).IsEqualTo(0);
        await Assert.That(process.Transcript.Methods).IsEquivalentTo(ModernMethods, CollectionOrdering.Matching);
        await AssertResultsAsync(process.Transcript.Results);
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
        await process.StandardInput.WriteAsync(WireTranscriptTests.Request(ProtocolData.ListMethod).AsMemory(), guard.Token);
        await Assert.That(await process.WaitForExitAsync(guard.Token)).IsEqualTo(2);
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
        var launch = new ProductExecutable(_files).Resolve(environment);
        return launch with { Arguments = [.. launch.Arguments, .. arguments] };
    }

    private async Task WithHttpAsync(Func<Uri, RecordingHttpHandler, HttpClient, CancellationToken, Task> observation)
    {
        await using var process = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Listening announcement and modern bootstrap.
        await process.StartAsync(Launch(["--transport", "http", "--environment", "Development"]), guard.Token);
        await WithHttpClientAsync(process, observation, guard.Token);
        // HTTP has no portable graceful-stop seam here. The owner forces termination only in teardown.
    }

    private static async Task WithHttpClientAsync(OwnedMcpProcess process, Func<Uri, RecordingHttpHandler, HttpClient, CancellationToken, Task> observation,
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
        await AssertBootstrapAsync(client, token);
        await Assert.That(recorder.Requests.Select(static frame => frame.GetProperty("method").GetString()))
            .IsEquivalentTo(ModernMethods, CollectionOrdering.Matching);
        await AssertResultsAsync([.. recorder.Responses.Select(static frame => frame.GetProperty("result"))]);
        foreach (var headers in recorder.RequestHeaders.Concat(recorder.ResponseHeaders))
        {
            await Assert.That(headers.Keys).DoesNotContain("Mcp-Session-Id", StringComparer.OrdinalIgnoreCase);
            await Assert.That(headers.Keys).DoesNotContain("Access-Control-Allow-Origin", StringComparer.OrdinalIgnoreCase);
        }
        await observation(endpoint, recorder, http, token);
    }

    private static async Task AssertBootstrapAsync(McpClient client, CancellationToken token)
    {
        await Assert.That(client.NegotiatedProtocolVersion).IsEqualTo(ProtocolData.Revision);
        var identity = await Assert.That(client.ServerInfo).IsNotNull();
        await Assert.That(identity.Name).IsEqualTo("opencode-mcp");
        await Assert.That(identity.Version).IsEqualTo(ExpectedVersion());
        var capabilities = await Assert.That(client.ServerCapabilities).IsNotNull();
        await Assert.That(capabilities.Tools).IsNotNull();
        var tools = await client.ListToolsAsync(cancellationToken: token);
        await Assert.That(tools.Select(static tool => tool.Name)).IsEquivalentTo([ProtocolData.PingTool], CollectionOrdering.Matching);
        var tool = tools.Single().ProtocolTool;
        await Assert.That(tool.Description).IsEqualTo("Returns pong with this server's name and version to verify connectivity; performs no external work.");
        var hints = await Assert.That(tool.Annotations).IsNotNull();
        await Assert.That(hints.ReadOnlyHint).IsTrue();
        await Assert.That(hints.IdempotentHint).IsTrue();
        await Assert.That(hints.DestructiveHint).IsFalse();
        await Assert.That(hints.OpenWorldHint).IsFalse();
        await Assert.That(tool.InputSchema.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(tool.InputSchema.GetProperty("properties").EnumerateObject()).IsEmpty();
        var result = await client.CallToolAsync(ProtocolData.PingTool, cancellationToken: token);
        await Assert.That(result.IsError).IsNotEqualTo(true);
        await Assert.That(result.Content.Select(static content => content.Type)).IsEquivalentTo(["text"], CollectionOrdering.Matching);
        await Assert.That(result.Content.OfType<TextContentBlock>().Select(static content => content.Text))
            .IsEquivalentTo([$"pong opencode-mcp {ExpectedVersion()}"], CollectionOrdering.Matching);
    }

    private static async Task AssertResultsAsync(IReadOnlyList<JsonElement> results)
    {
        await Assert.That(results.Select(static result => result.GetProperty("resultType").GetString()))
            .IsEquivalentTo(CompleteResults, CollectionOrdering.Matching);
        await Assert.That(results[0].GetProperty("supportedVersions").EnumerateArray().Select(static value => value.GetString()))
            .IsEquivalentTo(SupportedVersions, CollectionOrdering.Matching);
        var identity = results[0].GetProperty("_meta").GetProperty(MetaKeys.ServerInfo);
        await Assert.That(identity.GetProperty("name").GetString()).IsEqualTo("opencode-mcp");
        await Assert.That(identity.GetProperty("version").GetString()).IsEqualTo(ExpectedVersion());
        await Assert.That(results[0].GetProperty("capabilities").EnumerateObject().Select(static property => property.Name)).Contains("tools");
    }

    private static string ExpectedVersion() => typeof(McpApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion.Split('+')[0] ?? throw new InvalidOperationException("Product informational version is missing.");
}
