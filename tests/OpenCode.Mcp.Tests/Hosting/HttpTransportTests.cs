using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenCode.Mcp.Hosting;
using OpenCode.Mcp.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Hosting;

/// <summary>Exercises production HTTP composition over owned loopback sockets.</summary>
public sealed class HttpTransportTests
{
    private static readonly string?[] ExpectedMethods = ["server/discover", "tools/list", "tools/call"];
    private static readonly string?[] ExpectedVersions = ["2026-07-28"];
    private const string StdioHostedService = "ModelContextProtocol.SingleSessionMcpServerHostedService";
    private const string LocalOrigin = "http://localhost:1234";
    private const string DynamicLoopbackUrl = "http://127.0.0.1:0";
    private const string Pong = "pong opencode-mcp";

    /// <summary>The real HTTP factory refuses a non-loopback listener before creating a host.</summary>
    [Test]
    public async Task Create_Should_Reject_Configuration_When_Url_Is_Not_Loopback() =>
        await Assert.That(async () =>
        {
            await using var host = (WebApplication)new McpHostFactory().Create(
                TransportSelector.Parse(HostArguments(arguments: ["--urls", "http://0.0.0.0:0"])));
        }).ThrowsExactly<InvalidOperationException>().WithMessage(
            "HTTP URLs must use HTTP and a loopback host, without user information, a path, query or fragment.", StringComparison.Ordinal);

    /// <summary>Kestrel endpoint configuration cannot bypass the factory's listener policy.</summary>
    [Test]
    public async Task Create_Should_Reject_Configuration_When_Kestrel_Endpoints_Are_Configured() =>
        await Assert.That(async () =>
        {
            await using var host = (WebApplication)new McpHostFactory().Create(TransportSelector.Parse(
                HostArguments(arguments: [$"--Kestrel:Endpoints:A:Url={DynamicLoopbackUrl}"])));
        }).ThrowsExactly<InvalidOperationException>().WithMessage(
            "Configure HTTP endpoints with 'urls'; 'Kestrel:Endpoints' is not supported.", StringComparison.Ordinal);

    /// <summary>The pinned URL binds numeric loopback on an allocated port.</summary>
    [Test]
    public async Task Create_Should_Bind_Dynamic_Loopback_When_Urls_Are_Pinned() =>
        await WithApplicationAsync(async (_, endpoint, token) =>
        {
            await Assert.That(endpoint.Host).IsEqualTo("127.0.0.1");
            await Assert.That(endpoint.Port).IsNotEqualTo(0);
            await WithClientAsync(endpoint, AssertBootstrapAsync, token);
        });

    /// <summary>The real client sees identity, metadata and a successful connectivity probe.</summary>
    [Test]
    public async Task Create_Should_Expose_Modern_Bootstrap_When_Client_Connects() =>
        await WithApplicationAsync((_, endpoint, token) => WithClientAsync(endpoint, AssertBootstrapAsync, token));

    /// <summary>Wire discovery, list and call are ordered, complete and stateless.</summary>
    [Test]
    public async Task Create_Should_Emit_Discovery_List_And_Call_When_Client_Uses_Modern_Protocol() =>
        await WithApplicationAsync((_, endpoint, token) => WithClientAsync(endpoint, async (client, recorder, cancellation) =>
        {
            await AssertBootstrapAsync(client, recorder, cancellation);
            await Assert.That(recorder.Requests.Select(static frame => frame.GetProperty("method").GetString()))
                .IsEquivalentTo(ExpectedMethods, CollectionOrdering.Matching);
            await Assert.That(recorder.Responses.Count).IsEqualTo(3);
            for (var index = 0; index < recorder.Requests.Count; index++)
            {
                var response = recorder.Responses[index];
                await Assert.That(response.GetProperty("id").GetRawText()).IsEqualTo(recorder.Requests[index].GetProperty("id").GetRawText());
                await Assert.That(response.EnumerateObject().Select(static property => property.Name)).DoesNotContain("error");
                await Assert.That(response.GetProperty("result").GetProperty("resultType").GetString()).IsEqualTo("complete");
            }

            var discovery = recorder.Responses[0].GetProperty("result");
            await Assert.That(discovery.GetProperty("supportedVersions").EnumerateArray().Select(static value => value.GetString()))
                .IsEquivalentTo(ExpectedVersions, CollectionOrdering.Matching);
            await Assert.That(discovery.GetProperty("capabilities").EnumerateObject().Select(static property => property.Name)).Contains("tools");
            var identity = discovery.GetProperty("_meta").GetProperty(MetaKeys.ServerInfo);
            await Assert.That(identity.GetProperty("name").GetString()).IsEqualTo("opencode-mcp");
            await Assert.That(identity.GetProperty("version").GetString()).IsEqualTo(ExpectedVersion());
            foreach (var headers in recorder.RequestHeaders.Concat(recorder.ResponseHeaders))
            {
                await Assert.That(headers.Keys).DoesNotContain("Mcp-Session-Id", StringComparer.OrdinalIgnoreCase);
            }
        }, token));

    /// <summary>An untrusted browser origin is refused before protocol handling, while the endpoint remains usable.</summary>
    [Test]
    [Arguments("/mcp")]
    [Arguments("/MCP")]
    [Arguments("/Mcp/")]
    [Arguments("/mcp/")]
    [Arguments("/alive")]
    public async Task Create_Should_Reject_The_Request_When_Origin_Is_Untrusted_And_Host_Is_Loopback(string path) =>
        await WithApplicationAsync((_, endpoint, token) => AssertRefusalAsync(new Uri(endpoint, path), "http://localhost.evil.example", null,
            HttpStatusCode.Forbidden, token));

    /// <summary>Native clients without Origin complete real discovery, list and call.</summary>
    [Test]
    public async Task Create_Should_Accept_Bootstrap_When_Origin_Is_Missing() =>
        await WithApplicationAsync((_, endpoint, token) => WithClientAsync(endpoint, async (client, recorder, cancellation) =>
        {
            await AssertBootstrapAsync(client, recorder, cancellation);
            foreach (var headers in recorder.RequestHeaders)
            {
                await Assert.That(headers.Keys).DoesNotContain("Origin", StringComparer.OrdinalIgnoreCase);
            }
        }, token));

    /// <summary>Local browser origins need not share the listener's scheme or port.</summary>
    [Test]
    [Arguments(LocalOrigin)]
    [Arguments("https://127.0.0.1:4321")]
    [Arguments("https://[::1]:4443")]
    public async Task Create_Should_Accept_Bootstrap_When_Origin_Is_Loopback(string origin) =>
        await WithApplicationAsync((_, endpoint, token) => WithClientAsync(endpoint, AssertBootstrapAsync, token, origin));

    /// <summary>Host filtering rejects a hostile Host independently of a trusted Origin.</summary>
    [Test]
    public async Task Create_Should_Reject_The_Request_When_Host_Is_Untrusted() =>
        await WithApplicationAsync((_, endpoint, token) => AssertRefusalAsync(endpoint, LocalOrigin, "evil.example",
            HttpStatusCode.BadRequest, token));

    /// <summary>The host allow-list refuses an absent Host on an otherwise healthy Development route.</summary>
    [Test]
    public async Task Create_Should_Reject_The_Request_When_Host_Is_Missing() =>
        await WithApplicationAsync(async (_, endpoint, token) =>
        {
            await Assert.That(await SendRawRequestAsync(endpoint, "GET /alive HTTP/1.0\r\nHost: 127.0.0.1\r\n\r\n", token))
                .IsEqualTo("HTTP/1.1 200 OK");
            await Assert.That(await SendRawRequestAsync(endpoint, "GET /alive HTTP/1.0\r\n\r\n", token))
                .IsEqualTo("HTTP/1.1 400 Bad Request");
        });

    /// <summary>A successful trusted-origin call does not grant cross-origin access.</summary>
    [Test]
    public async Task Create_Should_Not_Grant_Cors_When_Origin_Is_Present() =>
        await WithApplicationAsync((_, endpoint, token) => WithClientAsync(endpoint, async (client, recorder, cancellation) =>
        {
            await AssertBootstrapAsync(client, recorder, cancellation);
            foreach (var headers in recorder.ResponseHeaders)
            {
                await Assert.That(headers.Keys).DoesNotContain("Access-Control-Allow-Origin", StringComparer.OrdinalIgnoreCase);
            }
        }, token, LocalOrigin));

    /// <summary>Development exposes both standard health routes alongside the actual MCP endpoint.</summary>
    [Test]
    public async Task Create_Should_Expose_Health_And_Alive_When_Environment_Is_Development() =>
        await WithApplicationAsync((_, endpoint, token) => AssertHealthAsync(endpoint, HttpStatusCode.OK, token));

    /// <summary>Production omits development health routes without removing MCP.</summary>
    [Test]
    public async Task Create_Should_Omit_Health_And_Alive_When_Environment_Is_Not_Development() =>
        await WithApplicationAsync((_, endpoint, token) => AssertHealthAsync(endpoint, HttpStatusCode.NotFound, token), "Production");

    /// <summary>Awaited shutdown and disposal release the listener, evidenced by a refused fresh connection.</summary>
    [Test]
    public async Task Create_Should_Refuse_Connections_When_Stopped_And_Disposed()
    {
        Uri? observed = null;
        await WithApplicationAsync(async (_, endpoint, token) =>
        {
            observed = endpoint;
            await WithClientAsync(endpoint, AssertBootstrapAsync, token);
        });
        var address = await Assert.That(observed).IsNotNull();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = new TcpClient();
        var error = await Assert.That(async () => await socket.ConnectAsync(IPAddress.Loopback, address.Port, guard.Token))
            .ThrowsExactly<SocketException>();
        var socketError = await Assert.That(error).IsNotNull();
        await Assert.That(socketError.SocketErrorCode).IsEqualTo(SocketError.ConnectionRefused);
    }

    /// <summary>Stdio creates no web server and ignores unusable HTTP endpoint configuration.</summary>
    [Test]
    public async Task Create_Should_Not_Register_A_Web_Server_When_Transport_Is_Stdio()
    {
        IServiceCollection? registrations = null;
        var host = new McpHostFactory(services => registrations = services)
            .Create(TransportSelector.Parse(HostArguments(transport: "stdio", arguments: ["--urls", "http://0.0.0.0:0"])));
        try
        {
            await Assert.That(host.Services.GetService<IServer>()).IsNull();
            await Assert.That(registrations).IsNotNull();
            var services = registrations ?? throw new InvalidOperationException("Host services were not observed.");
            var hosted = services.Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
                .Select(static descriptor => descriptor.ImplementationType?.FullName).ToArray();
            await Assert.That(hosted).Contains(StdioHostedService);
            await Assert.That(hosted.Count(static name => name == StdioHostedService)).IsEqualTo(1);
            var transport = services.Single(static descriptor => descriptor.ServiceType == typeof(ITransport));
            await Assert.That(transport.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
            await Assert.That(transport.ImplementationFactory).IsNotNull();
        }
        finally
        {
            await ((IAsyncDisposable)host).DisposeAsync();
        }
    }

    /// <summary>A launcher override enables session headers, and production shutdown releases its listener.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task CreateApplication_Should_Forward_Overrides_When_Http_Host_Is_Run(bool stateful)
    {
        using var errors = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using var stopping = new CancellationTokenSource();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IServer? observed = null;
        Uri? recordedAddress = null;
        var application = McpApp.CreateApplication(errors, services =>
            services.AddOptions<HttpServerTransportOptions>().Configure<IServer, IHostApplicationLifetime>((options, server, lifetime) =>
            {
                if (stateful)
                {
                    options.SessionMode = HttpServerSessionMode.Stateful;
                }

                observed = server;
                lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            }));
        var run = application.RunAsync(HostArguments(), stopping.Token);
        try
        {
            var server = await Assert.That(observed).IsNotNull();
            await started.Task.WaitAsync(guard.Token);
            var addresses = await Assert.That(server.Features.Get<IServerAddressesFeature>()).IsNotNull();
            recordedAddress = new Uri(addresses.Addresses.Single());
            await AssertSessionHeadersAsync(new Uri(recordedAddress, "/mcp"), stateful, guard.Token);
        }
        finally
        {
            await stopping.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(15));
        }

        await Assert.That(await run).IsEqualTo(0);
        await Assert.That(errors.ToString()).IsEmpty();
        var address = await Assert.That(recordedAddress).IsNotNull();
        using var connectionGuard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = new TcpClient();
        var error = await Assert.That(async () => await socket.ConnectAsync(IPAddress.Loopback, address.Port, connectionGuard.Token))
            .ThrowsExactly<SocketException>();
        var socketError = await Assert.That(error).IsNotNull();
        await Assert.That(socketError.SocketErrorCode).IsEqualTo(SocketError.ConnectionRefused);
    }

    private static async Task AssertSessionHeadersAsync(Uri endpoint, bool stateful, CancellationToken token)
    {
        using var sockets = new SocketsHttpHandler { UseProxy = false };
        using var recorder = new RecordingHttpHandler(sockets, 1_048_576);
        using var http = new HttpClient(recorder, disposeHandler: false);
        var initialize = new JsonRpcRequest
        {
            Id = new RequestId(1),
            Method = RequestMethods.Initialize,
            Params = JsonSerializer.SerializeToNode(new InitializeRequestParams
            {
                ProtocolVersion = "2025-11-25",
                Capabilities = new ClientCapabilities(),
                ClientInfo = new Implementation { Name = "override-probe", Version = "1.0.0" },
            }, McpJsonUtilities.DefaultOptions.GetTypeInfo<InitializeRequestParams>()),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = JsonContent.Create(initialize, McpJsonUtilities.DefaultOptions.GetTypeInfo<JsonRpcRequest>());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await http.SendAsync(request, token);
        await Assert.That(recorder.ResponseHeaders).IsNotEmpty();
        foreach (var headers in recorder.ResponseHeaders)
        {
            if (stateful)
            {
                await Assert.That(headers.Keys).Contains("Mcp-Session-Id", StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                await Assert.That(headers.Keys).DoesNotContain("Mcp-Session-Id", StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static string[] HostArguments(string transport = "http", string environment = "Development", string[]? arguments = null) =>
        ["--transport", transport, "--environment", environment, "--urls", DynamicLoopbackUrl, "--OTEL_EXPORTER_OTLP_ENDPOINT=", .. arguments ?? []];

    private static async Task WithApplicationAsync(Func<WebApplication, Uri, CancellationToken, Task> action, string environment = "Development")
    {
        var app = (WebApplication)new McpHostFactory().Create(TransportSelector.Parse(HostArguments(environment: environment)));
        try
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await app.StartAsync(guard.Token);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var bound = await Assert.That(addresses).IsNotNull();
            await action(app, new Uri(new Uri(bound.Addresses.Single()), "/mcp"), guard.Token);
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await app.StopAsync(cleanup.Token);
            }
            finally
            {
                await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    private static async Task WithClientAsync(Uri endpoint, Func<McpClient, RecordingHttpHandler, CancellationToken, Task> action,
        CancellationToken token, string? origin = null)
    {
        using var sockets = new SocketsHttpHandler { UseProxy = false };
        using var recorder = new RecordingHttpHandler(sockets, 1_048_576);
        using var recordedHttp = new HttpClient(recorder, disposeHandler: false);
        if (origin is not null)
        {
            recordedHttp.DefaultRequestHeaders.Add("Origin", origin);
        }

        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
        }, recordedHttp);
        await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ProtocolVersion = "2026-07-28",
            DiscoverProbeTimeout = Timeout.InfiniteTimeSpan,
            InitializationTimeout = TimeSpan.FromSeconds(30),
        }, cancellationToken: token);
        await action(client, recorder, token);
    }

    private static async Task AssertBootstrapAsync(McpClient client, RecordingHttpHandler recorder, CancellationToken token)
    {
        await Assert.That(client.NegotiatedProtocolVersion).IsEqualTo("2026-07-28");
        var identity = await Assert.That(client.ServerInfo).IsNotNull();
        await Assert.That(identity.Name).IsEqualTo("opencode-mcp");
        await Assert.That(identity.Version).IsEqualTo(ExpectedVersion());
        var capabilities = await Assert.That(client.ServerCapabilities).IsNotNull();
        await Assert.That(capabilities.Tools).IsNotNull();
        var tools = await client.ListToolsAsync(cancellationToken: token);
        await Assert.That(tools.Select(static tool => tool.Name)).IsEquivalentTo(["bootstrap_ping"], CollectionOrdering.Matching);
        var tool = tools.Single().ProtocolTool;
        await Assert.That(tool.Description).IsEqualTo("Returns pong with this server's name and version to verify connectivity; performs no external work.");
        var hints = await Assert.That(tool.Annotations).IsNotNull();
        await Assert.That(hints.ReadOnlyHint).IsTrue();
        await Assert.That(hints.IdempotentHint).IsTrue();
        await Assert.That(hints.DestructiveHint).IsFalse();
        await Assert.That(hints.OpenWorldHint).IsFalse();
        await Assert.That(tool.InputSchema.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(tool.InputSchema.GetProperty("properties").EnumerateObject()).IsEmpty();
        var result = await client.CallToolAsync("bootstrap_ping", cancellationToken: token);
        await Assert.That(result.IsError).IsNotEqualTo(true);
        await Assert.That(result.Content.Select(static content => content.Type)).IsEquivalentTo(["text"], CollectionOrdering.Matching);
        await Assert.That(result.Content.OfType<TextContentBlock>().Select(static content => content.Text))
            .IsEquivalentTo([$"{Pong} {ExpectedVersion()}"], CollectionOrdering.Matching);
        await Assert.That(recorder.StatusCodes).IsNotEmpty();
    }

    private static string ExpectedVersion() => typeof(McpApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion.Split('+')[0] ?? throw new InvalidOperationException("Product informational version is missing.");

    private static async Task AssertRefusalAsync(Uri endpoint, string? origin, string? host, HttpStatusCode expected, CancellationToken token)
    {
        await WithClientAsync(new Uri(endpoint, "/mcp"), async (client, recorder, cancellation) =>
        {
            await AssertBootstrapAsync(client, recorder, cancellation);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new StringContent(recorder.Requests[0].GetRawText(), Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Host = host;
            request.Headers.Add("Origin", origin);
            using var sockets = new SocketsHttpHandler { UseProxy = false };
            using var http = new HttpClient(sockets);
            using var response = await http.SendAsync(request, cancellation);
            await Assert.That(response.StatusCode).IsEqualTo(expected);
            if (expected == HttpStatusCode.Forbidden)
            {
                await Assert.That(await response.Content.ReadAsStringAsync(cancellation)).IsEmpty();
            }

            var control = await client.CallToolAsync("bootstrap_ping", cancellationToken: cancellation);
            await Assert.That(control.Content.OfType<TextContentBlock>().Single().Text).IsEqualTo($"{Pong} {ExpectedVersion()}");
        }, token);
    }

    private static async Task<string?> SendRawRequestAsync(Uri endpoint, string request, CancellationToken token)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, endpoint.Port, token);
        await using var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        return await reader.ReadLineAsync(token);
    }

    private static async Task AssertHealthAsync(Uri endpoint, HttpStatusCode expected, CancellationToken token)
    {
        using var sockets = new SocketsHttpHandler { UseProxy = false };
        using var http = new HttpClient(sockets);
        foreach (var path in new[] { "/health", "/alive" })
        {
            using var response = await http.GetAsync(new Uri(endpoint, path), token);
            await Assert.That(response.StatusCode).IsEqualTo(expected);
            if (expected == HttpStatusCode.OK)
            {
                await Assert.That(await response.Content.ReadAsStringAsync(token)).IsEqualTo("Healthy");
            }
        }

        await WithClientAsync(endpoint, AssertBootstrapAsync, token);
    }
}
