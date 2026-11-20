using System.Collections.Concurrent;
using System.Diagnostics;
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
using OpenCode.Mcp.Tests.Support;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.ServiceDefaults;

/// <summary>Checks the adapted Aspire defaults at their public registration boundaries.</summary>
public sealed class ServiceDefaultsExtensionsTests
{
    private const string BackendService = "backend";
    private const string BackendAddress = "http://127.0.0.1:59999";
    private const string AlternateBackendAddress = "http://127.0.0.1:59998";
    private const string BackendRequest = "http://backend/ping";
    private const string OtlpLogsPath = "/v1/logs";
    private const string TelemetryClient = "telemetry";
    private const string MetricsHost = "metrics-probe.invalid";
    private const string HealthPath = "/health";
    private const string AlivenessPath = "/alive";

    /// <summary>Application logs retain formatted messages and scopes.</summary>
    [Test]
    public async Task AddServiceDefaults_Should_Keep_Formatted_And_Scoped_Logs_When_Configured()
    {
        var builder = new McpHostBuilder().Build();
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
        var builder = new McpHostBuilder().Build();
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

    /// <summary>Only the configured application source contributes these probe spans.</summary>
    [Test]
    public async Task AddServiceDefaults_Should_Record_Activities_From_The_Application_Source()
    {
        const string applicationOperation = "application-source-probe";
        const string unrelatedOperation = "unrelated-source-probe";
        const string unrelatedSource = "Unrelated.Source.Probe";
        var activities = new ConcurrentQueue<Activity>();
        var builder = new McpHostBuilder().Build();
        McpApp.Configure(builder, McpTransport.Stdio, services =>
            services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(new RecordingActivityProcessor(activities))));
        using var services = builder.Services.BuildServiceProvider();
        _ = services.GetRequiredService<TracerProvider>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == unrelatedSource,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var application = new ActivitySource(HostData.ApplicationName);
        using var unrelated = new ActivitySource(unrelatedSource);

        application.StartActivity(applicationOperation)?.Dispose();
        using (var unrelatedActivity = unrelated.StartActivity(unrelatedOperation))
        {
            await Assert.That(unrelatedActivity).IsNotNull();
        }

        await Assert.That(activities.Select(static activity => activity.OperationName)
                .Where(static name => name is applicationOperation or unrelatedOperation))
            .IsEquivalentTo([applicationOperation], CollectionOrdering.Matching);
    }

    /// <summary>A factory request contributes its actual target and response status to a client span.</summary>
    [Test]
    public async Task AddServiceDefaults_Should_Record_Http_Client_Trace_With_Request_Uri_And_Status()
    {
        var activities = new ConcurrentQueue<Activity>();
        var builder = new McpHostBuilder().Build();
        using var handler = CreateTelemetryHandler();
        ConfigureTelemetryClient(builder, handler, services =>
            services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(new RecordingActivityProcessor(activities))));
        await using var services = builder.Services.BuildServiceProvider();
        _ = services.GetRequiredService<TracerProvider>();
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(TelemetryClient);
        using var parent = new Activity("http-trace-probe");
        parent.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        parent.Start();
        var uri = new Uri("http://trace-probe.invalid/ping");
        var context = await Assert.That(TestContext.Current).IsNotNull();

        using var response = await client.GetAsync(uri, context.Execution.CancellationToken);

        var spans = activities.Where(activity => activity.TraceId == parent.TraceId && activity.Kind == ActivityKind.Client).ToArray();
        string?[] expectedUrls = [uri.AbsoluteUri];
        await Assert.That(spans.Select(static span => span.GetTagItem("url.full") as string))
            .IsEquivalentTo(expectedUrls, CollectionOrdering.Matching);
        await Assert.That(spans.Single().GetTagItem("http.response.status_code")).IsEqualTo(204);
    }

    /// <summary>A factory request contributes a duration measurement for its destination.</summary>
    [Test]
    public async Task AddServiceDefaults_Should_Record_Http_Client_Request_Duration()
    {
        var metricNames = new ConcurrentQueue<string>();
        var builder = new McpHostBuilder().Build();
        using var handler = CreateTelemetryHandler();
        ConfigureTelemetryClient(builder, handler, services =>
            services.AddOpenTelemetry().WithMetrics(metrics =>
                metrics.AddReader(new BaseExportingMetricReader(new RecordingMetricExporter(metricNames)))));
        await using var services = builder.Services.BuildServiceProvider();
        var metrics = services.GetRequiredService<MeterProvider>();
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(TelemetryClient);
        var context = await Assert.That(TestContext.Current).IsNotNull();

        using var response = await client.GetAsync(new Uri($"http://{MetricsHost}/ping"), context.Execution.CancellationToken);

        await Assert.That(metrics.ForceFlush()).IsTrue();
        await Assert.That(metricNames).Contains("http.client.request.duration");
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
    [Arguments(HealthPath, false)]
    [Arguments("/HEALTH", false)]
    [Arguments(HealthPath + "/detail", false)]
    [Arguments(AlivenessPath, false)]
    [Arguments(AlivenessPath + "/detail", false)]
    [Arguments("/ALIVE/detail", false)]
    [Arguments("/healthier", true)]
    [Arguments("/mcp", true)]
    public async Task AddServiceDefaults_Should_Filter_Health_Segments_When_Trace_Path_Is_Evaluated(string path, bool expected)
    {
        var builder = new McpHostBuilder().Build();
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
        var builder = new McpHostBuilder().Build();
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
        var builder = new McpHostBuilder().WithServiceEndpoints(BackendService, BackendAddress).Build();
        using var recording = new RecordingHandler();
        McpApp.Configure(builder, McpTransport.Stdio, services =>
            services.AddHttpClient(BackendService).ConfigurePrimaryHttpMessageHandler(() => recording));
        using var services = builder.Services.BuildServiceProvider();
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(BackendService);
        using var handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(BackendService);
        var resilience = new List<ResilienceHandler>();
        for (var current = handler as DelegatingHandler; current is not null; current = current.InnerHandler as DelegatingHandler)
        {
            if (current is ResilienceHandler resilienceHandler)
            {
                resilience.Add(resilienceHandler);
            }
        }

        var context = await Assert.That(TestContext.Current).IsNotNull();
        using var response = await client.GetAsync(new Uri(BackendRequest), context.Execution.CancellationToken);

        Uri?[] expectedUris = [new Uri(BackendAddress + "/ping")];
        await Assert.That(recording.RequestUris).IsEquivalentTo(expectedUris, CollectionOrdering.Matching);
        await Assert.That(resilience).Count().IsEqualTo(1);
    }

    /// <summary>After a transient failure, the factory client selects another endpoint on retry.</summary>
    [Test]
    public async Task AddServiceDefaults_Should_Resolve_The_Service_Again_When_A_Factory_Client_Retries()
    {
        var builder = new McpHostBuilder().WithServiceEndpoints(BackendService, AlternateBackendAddress, BackendAddress).Build();
        using var recording = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        McpApp.Configure(builder, McpTransport.Stdio, services =>
        {
            services.AddHttpClient(BackendService).ConfigurePrimaryHttpMessageHandler(() => recording);
            services.PostConfigureAll<HttpStandardResilienceOptions>(static options =>
            {
                options.Retry.Delay = TimeSpan.Zero;
                options.Retry.UseJitter = false;
            });
        });
        await using var services = builder.Services.BuildServiceProvider();
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(BackendService);
        var context = await Assert.That(TestContext.Current).IsNotNull();

        using var response = await client.GetAsync(new Uri(BackendRequest), context.Execution.CancellationToken);

        Uri?[] expectedUris = [new Uri(BackendAddress + "/ping"), new Uri(AlternateBackendAddress + "/ping")];
        await Assert.That(recording.RequestUris).IsEquivalentTo(expectedUris, CollectionOrdering.Any);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>Fluent defaults return the original builder.</summary>
    [Test]
    public async Task AddServiceDefaults_Should_Return_The_Supplied_Builder_When_Configured()
    {
        var builder = new McpHostBuilder().Build();

        await Assert.That(builder.AddServiceDefaults()).IsSameReferenceAs(builder);
        await Assert.That(builder.ConfigureOpenTelemetry()).IsSameReferenceAs(builder);
        await Assert.That(builder.AddDefaultHealthChecks()).IsSameReferenceAs(builder);
    }

    /// <summary>Development maps readiness before liveness without starting a listener.</summary>
    [Test]
    public async Task MapDefaultEndpoints_Should_Map_Health_And_Alive_When_Environment_Is_Development()
    {
        await using var app = CreateApplication(Environments.Development);
        string?[] expectedRoutes = [HealthPath, AlivenessPath];

        await Assert.That(app.MapDefaultEndpoints()).IsSameReferenceAs(app);
        await Assert.That(GetRoutes(app)).IsEquivalentTo(expectedRoutes, CollectionOrdering.Matching);
    }

    /// <summary>Production does not expose the development health routes.</summary>
    [Test]
    public async Task MapDefaultEndpoints_Should_Restrict_Mapping_To_Development_When_Environment_Is_Production()
    {
        await using var app = CreateApplication(Environments.Production);
        app.MapDefaultEndpoints();

        await Assert.That(GetRoutes(app)).DoesNotContain(HealthPath);
        await Assert.That(GetRoutes(app)).DoesNotContain(AlivenessPath);
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

    private static SocketsHttpHandler CreateTelemetryHandler() => new()
    {
        UseProxy = false,
        ConnectCallback = static (_, _) => ValueTask.FromResult<Stream>(new HttpResponseStream()),
    };

    private static void ConfigureTelemetryClient(HostApplicationBuilder builder, HttpMessageHandler handler, Action<IServiceCollection> observe) =>
        McpApp.Configure(builder, McpTransport.Stdio, services =>
        {
            services.AddHttpClient(TelemetryClient).ConfigurePrimaryHttpMessageHandler(() => handler);
            observe(services);
        });

    private static WebApplication CreateOtlpReceiver(TaskCompletionSource received)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        // UseUrls needs a writable configuration provider after the sources are cleared.
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var receiver = builder.Build();
        receiver.MapPost(OtlpLogsPath, () =>
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
        var builder = new McpHostBuilder().WithOtlpEndpoint(endpoint)
            .WithConfiguration("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
            .WithConfiguration("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", GetReceiverAddress(receiver) + OtlpLogsPath)
            .Build();
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
            ApplicationName = HostData.ApplicationName,
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

    private sealed class RecordingHandler(HttpStatusCode firstStatusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri?> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(RequestUris.Count == 1 ? firstStatusCode : HttpStatusCode.OK));
        }
    }

    private sealed class RecordingActivityProcessor(ConcurrentQueue<Activity> activities) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => activities.Enqueue(data);
    }

    private sealed class RecordingMetricExporter(ConcurrentQueue<string> metricNames) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    foreach (var tag in point.Tags)
                    {
                        if (tag.Key == "server.address" && tag.Value is MetricsHost)
                        {
                            metricNames.Enqueue(metric.Name);
                        }
                    }
                }
            }

            return ExportResult.Success;
        }
    }

    /// <summary>Supplies HTTP response bytes without a socket while the real handler emits telemetry.</summary>
    private sealed class HttpResponseStream() : MemoryStream("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray(), writable: false)
    {
        public override bool CanWrite => true;

        public override void Write(byte[] buffer, int offset, int count) { }

        public override void Write(ReadOnlySpan<byte> buffer) { }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            cancellationToken.IsCancellationRequested ? ValueTask.FromCanceled(cancellationToken) : ValueTask.CompletedTask;
    }
}
