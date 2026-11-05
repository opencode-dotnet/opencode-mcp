using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace OpenCode.Mcp.ServiceDefaults;

/// <summary>Composes the shared telemetry, health and factory-client defaults.</summary>
public static class ServiceDefaultsExtensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    /// <summary>Adds the standard telemetry, health, discovery and resilience registrations.</summary>
    /// <typeparam name="TBuilder">The host builder type.</typeparam>
    /// <param name="builder">The host receiving the defaults.</param>
    /// <returns>The supplied builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(static http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>Configures application logs, metrics and traces, with optional OTLP export.</summary>
    /// <typeparam name="TBuilder">The host builder type.</typeparam>
    /// <param name="builder">The host receiving telemetry registrations.</param>
    /// <returns>The supplied builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Logging.AddOpenTelemetry(static logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(static metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(static instrumentation =>
                        instrumentation.Filter = static context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath, StringComparison.OrdinalIgnoreCase)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath, StringComparison.OrdinalIgnoreCase))
                    .AddHttpClientInstrumentation();
            });

        return builder.AddOpenTelemetryExporters();
    }

    /// <summary>Registers the self check used to determine process liveness.</summary>
    /// <typeparam name="TBuilder">The host builder type.</typeparam>
    /// <param name="builder">The host receiving health check registrations.</param>
    /// <returns>The supplied builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHealthChecks()
            .AddCheck("self", static () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>Maps readiness and liveness endpoints in Development only.</summary>
    /// <remarks>
    /// Exposing health check endpoints outside Development has security implications.
    /// </remarks>
    /// <param name="app">The application receiving the health endpoints.</param>
    /// <returns>The supplied application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks(HealthEndpointPath);
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = static registration => registration.Tags.Contains("live"),
            });
        }

        return app;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }
}
