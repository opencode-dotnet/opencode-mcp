using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCode.Mcp.Hosting.Models;
using OpenCode.Mcp.ServiceDefaults;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.ServiceDefaults;

/// <summary>Checks the adapted Aspire defaults at their public registration boundaries.</summary>
public sealed class ServiceDefaultsExtensionsTests
{
    /// <summary>Application logs retain formatted messages and scopes.</summary>
    [Test]
    public async Task AddServiceDefaults_Should_Keep_Formatted_And_Scoped_Logs_When_Configured()
    {
        var builder = CreateBuilder();
        McpApp.Configure(builder, McpTransport.Stdio);
        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<OpenTelemetryLoggerOptions>>().Value;

        await Assert.That(options.IncludeFormattedMessage).IsTrue();
        await Assert.That(options.IncludeScopes).IsTrue();
    }

    /// <summary>Metrics and traces exist even without an export endpoint.</summary>
    [Test]
    public async Task AddServiceDefaults_Should_Register_Metrics_And_Traces_When_Otlp_Is_Absent()
    {
        var builder = CreateBuilder();
        McpApp.Configure(builder, McpTransport.Stdio, static services =>
            services.AddOpenTelemetry().WithMetrics(static metrics => metrics.AddReader(new NonExportingMetricReader())));
        using var services = builder.Services.BuildServiceProvider();
        using var traces = services.GetRequiredService<TracerProvider>();

        await Assert.That(traces).IsNotNull();

        using var metrics = services.GetRequiredService<MeterProvider>();
        using var runtimeMeter = new Meter("System.Runtime");
        var counter = runtimeMeter.CreateCounter<long>("runtime_probe");

        await Assert.That(metrics).IsNotNull();
        await Assert.That(counter.Enabled).IsTrue();
    }

    /// <summary>Absent or blank endpoints do not construct an OTLP log exporter.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task AddServiceDefaults_Should_Not_Enable_Otlp_When_Endpoint_Is_Missing_Or_Blank(string? endpoint)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = CreateOtlpReceiver(received);
        try
        {
            var context = await Assert.That(TestContext.Current).IsNotNull();
            await receiver.StartAsync(context.Execution.CancellationToken);
            var builder = CreateOtlpBuilder(receiver, endpoint);

            var flushed = FlushLog(builder);

            await Assert.That(flushed).IsTrue();
            await Assert.That(received.Task.IsCompleted).IsFalse();
        }
        finally
        {
            await receiver.DisposeAsync();
        }
    }

    /// <summary>A configured endpoint exports a log to the owned loopback receiver.</summary>
    [Test]
    public async Task AddServiceDefaults_Should_Enable_Otlp_When_Endpoint_Is_Configured()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = CreateOtlpReceiver(received);
        try
        {
            var context = await Assert.That(TestContext.Current).IsNotNull();
            await receiver.StartAsync(context.Execution.CancellationToken);
            var builder = CreateOtlpBuilder(receiver, GetReceiverAddress(receiver));

            var flushed = FlushLog(builder);

            await Assert.That(flushed).IsTrue();
            await Assert.That(received.Task.IsCompletedSuccessfully).IsTrue()
                .Because("ForceFlush finished, so the receiver must have accepted the OTLP log request");
        }
        finally
        {
            await receiver.DisposeAsync();
        }
    }

    /// <summary>Trace exclusions respect complete health path segments, including descendants.</summary>
    [Test]
    [Arguments("/health", false)]
    [Arguments("/HEALTH", false)]
    [Arguments("/health/detail", false)]
    [Arguments("/alive", false)]
    [Arguments("/alive/detail", false)]
    [Arguments("/ALIVE/detail", false)]
    [Arguments("/healthier", true)]
    [Arguments("/mcp", true)]
    public async Task AddServiceDefaults_Should_Filter_Health_Segments_When_Trace_Path_Is_Evaluated(string path, bool expected)
    {
        var builder = CreateBuilder();
        McpApp.Configure(builder, McpTransport.Stdio);
        using var services = builder.Services.BuildServiceProvider();
        _ = services.GetRequiredService<TracerProvider>();
        var filter = await Assert.That(services.GetRequiredService<IOptions<AspNetCoreTraceInstrumentationOptions>>()
            .Value.Filter).IsNotNull();
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await Assert.That(filter(context)).IsEqualTo(expected);
    }

    /// <summary>The self check is healthy and belongs to the liveness set.</summary>
    [Test]
    public async Task AddServiceDefaults_Should_Register_Healthy_Self_With_Live_Tag_When_Configured()
    {
        var builder = CreateBuilder();
        McpApp.Configure(builder, McpTransport.Stdio);
        using var services = builder.Services.BuildServiceProvider();
        var registrations = services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        await Assert.That(registrations.Select(static registration => registration.Name))
            .IsEquivalentTo(["self"], CollectionOrdering.Matching);
        await Assert.That(registrations.Single().Tags).IsEquivalentTo(["live"]);
        var context = await Assert.That(TestContext.Current).IsNotNull();
        var report = await services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(context.Execution.CancellationToken);
        await Assert.That(report.Entries.Keys).IsEquivalentTo(["self"], CollectionOrdering.Matching);
        await Assert.That(report.Entries["self"].Status).IsEqualTo(HealthStatus.Healthy);
    }

    /// <summary>Factory clients retain resilience and resolve service names before the primary handler.</summary>
    [Test]
    public async Task AddServiceDefaults_Should_Add_Resilience_And_Discovery_When_Factory_Client_Is_Created()
    {
        var builder = CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Services:backend:http:0"] = "http://127.0.0.1:59999",
        });
        using var recording = new RecordingHandler();
        McpApp.Configure(builder, McpTransport.Stdio, services =>
            services.AddHttpClient("backend").ConfigurePrimaryHttpMessageHandler(() => recording));
        using var services = builder.Services.BuildServiceProvider();
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("backend");
        using var handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("backend");
        var resilience = new List<ResilienceHandler>();
        for (var current = handler as DelegatingHandler; current is not null; current = current.InnerHandler as DelegatingHandler)
        {
            if (current is ResilienceHandler resilienceHandler)
            {
                resilience.Add(resilienceHandler);
            }
        }

        var context = await Assert.That(TestContext.Current).IsNotNull();
        using var response = await client.GetAsync(new Uri("http://backend/ping"), context.Execution.CancellationToken);

        Uri?[] expectedUris = [new Uri("http://127.0.0.1:59999/ping")];
        await Assert.That(recording.RequestUris).IsEquivalentTo(expectedUris, CollectionOrdering.Matching);
        await Assert.That(resilience).Count().IsEqualTo(1);
    }

    /// <summary>Fluent defaults return the original builder.</summary>
    [Test]
    public async Task AddServiceDefaults_Should_Return_The_Supplied_Builder_When_Configured()
    {
        var builder = CreateBuilder();

        await Assert.That(builder.AddServiceDefaults()).IsSameReferenceAs(builder);
        await Assert.That(builder.ConfigureOpenTelemetry()).IsSameReferenceAs(builder);
        await Assert.That(builder.AddDefaultHealthChecks()).IsSameReferenceAs(builder);
    }

    /// <summary>Development maps readiness before liveness without starting a listener.</summary>
    [Test]
    public async Task MapDefaultEndpoints_Should_Map_Health_And_Alive_When_Environment_Is_Development()
    {
        await using var app = CreateApplication(Environments.Development);
        string?[] expectedRoutes = ["/health", "/alive"];

        await Assert.That(app.MapDefaultEndpoints()).IsSameReferenceAs(app);
        await Assert.That(GetRoutes(app)).IsEquivalentTo(expectedRoutes, CollectionOrdering.Matching);
    }

    /// <summary>Production does not expose the development health routes.</summary>
    [Test]
    public async Task MapDefaultEndpoints_Should_Restrict_Mapping_To_Development_When_Environment_Is_Production()
    {
        await using var app = CreateApplication(Environments.Production);
        app.MapDefaultEndpoints();

        await Assert.That(GetRoutes(app)).DoesNotContain("/health");
        await Assert.That(GetRoutes(app)).DoesNotContain("/alive");
    }

    /// <summary>Defaults refuse a missing builder at their public boundary.</summary>
    [Test]
    public async Task AddServiceDefaults_Should_Refuse_Null_When_Builder_Is_Missing()
    {
        // Deliberately violate the non-null builder precondition.
        await Assert.That(() => ServiceDefaultsExtensions.AddServiceDefaults<HostApplicationBuilder>(null!))
            .ThrowsExactly<ArgumentNullException>().WithParameterName("builder");
    }

    /// <summary>Telemetry configuration refuses a missing builder at its public boundary.</summary>
    [Test]
    public async Task ConfigureOpenTelemetry_Should_Refuse_Null_When_Builder_Is_Missing()
    {
        // Deliberately violate the non-null builder precondition.
        await Assert.That(() => ServiceDefaultsExtensions.ConfigureOpenTelemetry<HostApplicationBuilder>(null!))
            .ThrowsExactly<ArgumentNullException>().WithParameterName("builder");
    }

    /// <summary>Health configuration refuses a missing builder at its public boundary.</summary>
    [Test]
    public async Task AddDefaultHealthChecks_Should_Refuse_Null_When_Builder_Is_Missing()
    {
        // Deliberately violate the non-null builder precondition.
        await Assert.That(() => ServiceDefaultsExtensions.AddDefaultHealthChecks<HostApplicationBuilder>(null!))
            .ThrowsExactly<ArgumentNullException>().WithParameterName("builder");
    }

    /// <summary>Endpoint mapping refuses a missing application at its public boundary.</summary>
    [Test]
    public async Task MapDefaultEndpoints_Should_Refuse_Null_When_Application_Is_Missing()
    {
        // Deliberately violate the non-null application precondition.
        await Assert.That(() => ServiceDefaultsExtensions.MapDefaultEndpoints(null!))
            .ThrowsExactly<ArgumentNullException>().WithParameterName("app");
    }

    private static HostApplicationBuilder CreateBuilder(string? endpoint = null)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ApplicationName = typeof(McpApp).Assembly.GetName().Name,
        });
        if (endpoint is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint,
            });
        }
        return builder;
    }

    private static WebApplication CreateOtlpReceiver(TaskCompletionSource received)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        // UseUrls needs a writable configuration provider after the sources are cleared.
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var receiver = builder.Build();
        receiver.MapPost("/v1/logs", () =>
        {
            received.TrySetResult();
            return Results.Ok();
        });
        return receiver;
    }

    private static string GetReceiverAddress(WebApplication receiver) => receiver.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
        ?? throw new InvalidOperationException("The OTLP receiver has no bound address.");

    private static HostApplicationBuilder CreateOtlpBuilder(WebApplication receiver, string? endpoint)
    {
        var builder = CreateBuilder(endpoint);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = GetReceiverAddress(receiver) + "/v1/logs",
        });
        McpApp.Configure(builder, McpTransport.Stdio);
        return builder;
    }

    private static bool FlushLog(HostApplicationBuilder builder)
    {
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OtlpActivationProbe");
        logger.LogInformation("OTLP activation probe");
        var provider = host.Services.GetRequiredService<LoggerProvider>();
        // Hang guard for completion of the log export, not a delay-based verdict.
        return provider.ForceFlush(30_000);
    }

    private static WebApplication CreateApplication(string environment)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(McpApp).Assembly.GetName().Name,
            EnvironmentName = environment,
        });
        builder.Configuration.Sources.Clear();
        McpApp.Configure(builder, McpTransport.Http);
        return builder.Build();
    }

    private static IEnumerable<string?> GetRoutes(WebApplication app) => ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>()
        .Select(static endpoint => endpoint.RoutePattern.RawText);

    private sealed class NonExportingMetricReader : MetricReader
    {
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri?> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
