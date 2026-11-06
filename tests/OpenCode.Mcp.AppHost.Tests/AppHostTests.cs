using System.Net;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenCode.Mcp.AppHost.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.AppHost.Tests;

/// <summary>Checks the actual AppHost graph and its closed-box product boundary.</summary>
[NotInParallel("processes")]
public sealed class AppHostTests
{
    private const string PingTool = "bootstrap_ping";
    private const string LocalOrigin = "https://localhost:1234";
    private readonly RealFileSystem _files = new();

    /// <summary>Declares exactly the product project with generated metadata and dynamic loopback HTTP wiring.</summary>
    [Test]
    public async Task Configure_Should_Declare_Only_The_Product_Project_When_The_AppHost_Is_Created()
    {
        using var wholeTest = new CancellationTokenSource(TimeSpan.FromMinutes(20)); // Complete graph test.
        using var creation = CancellationTokenSource.CreateLinkedTokenSource(wholeTest.Token);
        creation.CancelAfter(TimeSpan.FromSeconds(360)); // Actual entry-point resolution.
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.OpenCode_Mcp_AppHost>(
            new DistributedApplicationTestingBuilderOptions { EnableDashboard = false }, [], creation.Token);
        await Assert.That(builder.Resources.OfType<ProjectResource>().Select(static resource => resource.Name))
            .IsEquivalentTo(["mcp"], CollectionOrdering.Matching);
        var product = builder.Resources.OfType<ProjectResource>().Single();
        await Assert.That(product.GetProjectMetadata()).IsTypeOf<Projects.OpenCode_Mcp>();
        var arguments = new List<object>();
        var argumentContext = new CommandLineArgsCallbackContext(arguments, product, creation.Token);
        foreach (var annotation in product.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await annotation.Callback(argumentContext);
        }
        await Assert.That(arguments).IsEquivalentTo(new object[] { "--transport", "http" }, CollectionOrdering.Matching);
        var endpoints = product.Annotations.OfType<EndpointAnnotation>().ToArray();
        await Assert.That(endpoints.Select(static endpoint => endpoint.Name)).IsEquivalentTo(["http"], CollectionOrdering.Matching);
        var endpoint = endpoints.Single();
        await Assert.That(endpoint.UriScheme).IsEqualTo("http");
        await Assert.That(endpoint.Port).IsNull();
        await Assert.That(endpoint.TargetPort).IsNull();
        await Assert.That(endpoint.TargetHost).IsEqualTo("127.0.0.1");
        await Assert.That(endpoint.IsProxied).IsFalse();
        var environment = await OwnedAppHostTests.ReadAuthoredEnvironmentAsync(builder, creation.Token);
        await Assert.That(environment["ASPNETCORE_ENVIRONMENT"]).IsEqualTo("Development");
        await Assert.That(environment["ASPNETCORE_URLS"]).IsTypeOf<ReferenceExpression>();
        var urls = (ReferenceExpression)environment["ASPNETCORE_URLS"];
        await Assert.That(urls.ValueExpression).IsEqualTo("http://127.0.0.1:{mcp.bindings.http.targetPort}");
        await Assert.That(product.Annotations.OfType<HealthCheckAnnotation>().Select(static check => check.Key))
            .IsEquivalentTo(["mcp_http_/health_200_check"], CollectionOrdering.Matching);
        await Assert.That(product.Annotations.OfType<DisableForwardedHeadersAnnotation>()).Count().IsEqualTo(1);
        var rebuilder = builder.Resources.Single(static resource => resource.Name == "mcp-rebuilder");
        await Assert.That(rebuilder.Annotations.OfType<ExplicitStartupAnnotation>()).Count().IsEqualTo(1);
    }

    /// <summary>Real readiness exposes health bodies, modern discovery, exact tool metadata and a versioned pong.</summary>
    [Test]
    public async Task StartAsync_Should_Expose_Modern_Bootstrap_When_The_Product_Is_Healthy()
    {
        using var wholeTest = new CancellationTokenSource(TimeSpan.FromMinutes(20)); // Complete closed-box bootstrap.
        var builder = await OwnedAppHostTests.CreateBuilderAsync(wholeTest.Token);
        string? readyResource = null;
        try
        {
            builder.Eventing.Subscribe<ResourceReadyEvent>((resourceEvent, _) =>
            {
                readyResource = resourceEvent.Resource.Name;
                return Task.CompletedTask;
            });
        }
        catch
        {
            await builder.DisposeAsync();
            throw;
        }
        await using var owner = new OwnedAppHost(_files, builder);
        await owner.StartAsync(wholeTest.Token);
        await Assert.That(readyResource).IsEqualTo("mcp");
        await Assert.That(builder.Resources.Select(static resource => resource.Name)).DoesNotContain("aspire-dashboard");
        await Assert.That(owner.Endpoint.Host).IsEqualTo("127.0.0.1");
        await Assert.That(owner.Endpoint.Port).IsGreaterThan(0);
        using var sockets = new SocketsHttpHandler { UseProxy = false };
        using var http = new HttpClient(sockets) { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var path in new[] { "/health", "/alive" })
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(wholeTest.Token);
            request.CancelAfter(TimeSpan.FromSeconds(30)); // The named health route's response and body.
            using var response = await http.GetAsync(new Uri(owner.Endpoint, path), request.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await response.Content.ReadAsStringAsync(request.Token)).IsEqualTo("Healthy");
        }
        await AssertBootstrapAsync(owner.Endpoint, null, wholeTest.Token);
        await AssertBootstrapAsync(owner.Endpoint, LocalOrigin, wholeTest.Token);
    }

    /// <summary>Host and Origin refusals remain effective on the discovered endpoint, followed by a real control call.</summary>
    [Test]
    public async Task StartAsync_Should_Reject_Untrusted_Request_Headers_When_The_Resource_Is_Healthy()
    {
        using var wholeTest = new CancellationTokenSource(TimeSpan.FromMinutes(20)); // Header refusals and subsequent bootstrap.
        var builder = await OwnedAppHostTests.CreateBuilderAsync(wholeTest.Token);
        await using var owner = new OwnedAppHost(_files, builder);
        await owner.StartAsync(wholeTest.Token);
        using var sockets = new SocketsHttpHandler { UseProxy = false };
        using var http = new HttpClient(sockets) { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var path in new[] { "/mcp", "/health", "/alive" })
        {
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(wholeTest.Token);
            guard.CancelAfter(TimeSpan.FromSeconds(30)); // The hostile-Origin response and empty body.
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(owner.Endpoint, path));
            request.Headers.Add("Origin", "http://localhost.evil.example");
            using var response = await http.SendAsync(request, guard.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await response.Content.ReadAsStringAsync(guard.Token)).IsEmpty();
        }
        using var hostGuard = CancellationTokenSource.CreateLinkedTokenSource(wholeTest.Token);
        hostGuard.CancelAfter(TimeSpan.FromSeconds(30)); // The independent hostile-Host response.
        using var hostileHost = new HttpRequestMessage(HttpMethod.Get, new Uri(owner.Endpoint, "/health"));
        hostileHost.Headers.Host = "evil.example";
        hostileHost.Headers.Add("Origin", LocalOrigin);
        using var badHost = await http.SendAsync(hostileHost, hostGuard.Token);
        await Assert.That(badHost.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await AssertBootstrapAsync(owner.Endpoint, LocalOrigin, wholeTest.Token);
    }

    private async Task AssertBootstrapAsync(Uri endpoint, string? origin, CancellationToken token)
    {
        using var sockets = new SocketsHttpHandler { UseProxy = false };
        using var http = new HttpClient(sockets) { Timeout = Timeout.InfiniteTimeSpan };
        if (origin is not null)
        {
            http.DefaultRequestHeaders.Add("Origin", origin);
        }
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
        connect.CancelAfter(TimeSpan.FromSeconds(45)); // Modern MCP discovery completes within its initialization guard.
        await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ProtocolVersion = "2026-07-28",
            DiscoverProbeTimeout = Timeout.InfiniteTimeSpan,
            InitializationTimeout = TimeSpan.FromSeconds(30),
        }, cancellationToken: connect.Token);
        var version = await ExpectedVersionAsync(token);
        await Assert.That(client.NegotiatedProtocolVersion).IsEqualTo("2026-07-28");
        var identity = await Assert.That(client.ServerInfo).IsNotNull();
        await Assert.That(identity.Name).IsEqualTo("opencode-mcp");
        await Assert.That(identity.Version).IsEqualTo(version);
        var capabilities = await Assert.That(client.ServerCapabilities).IsNotNull();
        await Assert.That(capabilities.Tools).IsNotNull();
        using var list = CancellationTokenSource.CreateLinkedTokenSource(token);
        list.CancelAfter(TimeSpan.FromSeconds(30)); // The tools/list result.
        var tools = await client.ListToolsAsync(new ListToolsRequestParams(), list.Token);
        await Assert.That(tools.ResultType).IsEqualTo("complete");
        await Assert.That(tools.Tools.Select(static tool => tool.Name)).IsEquivalentTo([PingTool], CollectionOrdering.Matching);
        var tool = tools.Tools.Single();
        await Assert.That(tool.Description).IsEqualTo("Returns pong with this server's name and version to verify connectivity; performs no external work.");
        var hints = await Assert.That(tool.Annotations).IsNotNull();
        await Assert.That(hints.ReadOnlyHint).IsTrue();
        await Assert.That(hints.IdempotentHint).IsTrue();
        await Assert.That(hints.DestructiveHint).IsFalse();
        await Assert.That(hints.OpenWorldHint).IsFalse();
        await Assert.That(tool.InputSchema.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(tool.InputSchema.GetProperty("properties").EnumerateObject()).IsEmpty();
        using var call = CancellationTokenSource.CreateLinkedTokenSource(token);
        call.CancelAfter(TimeSpan.FromSeconds(30)); // The bootstrap_ping result.
        var result = await client.CallToolAsync(PingTool, cancellationToken: call.Token);
        await Assert.That(result.ResultType).IsEqualTo("complete");
        await Assert.That(result.IsError).IsNotEqualTo(true);
        await Assert.That(result.Content.Select(static content => content.Type)).IsEquivalentTo(["text"], CollectionOrdering.Matching);
        await Assert.That(result.Content.OfType<TextContentBlock>().Single().Text).IsEqualTo($"pong opencode-mcp {version}");
    }

    private async Task<string> ExpectedVersionAsync(CancellationToken token)
    {
        var project = new Projects.OpenCode_Mcp().ProjectPath;
        var directory = _files.Path.GetDirectoryName(project) ?? throw new InvalidOperationException("The product project has no directory.");
        var props = XDocument.Parse(await _files.File.ReadAllTextAsync(_files.Path.Combine(directory, "..", "..", "Directory.Build.props"), token));
        var prefix = props.Descendants("VersionPrefix").Single().Value;
        var suffix = props.Descendants("VersionSuffix").Single().Value;
        return string.IsNullOrEmpty(suffix) ? prefix : $"{prefix}-{suffix}";
    }
}
