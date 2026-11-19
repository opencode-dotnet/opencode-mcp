using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace OpenCode.Mcp.Tests.Support;

internal sealed class McpHostBuilder
{
    private const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private readonly Dictionary<string, string?> _settings = new(StringComparer.Ordinal);

    public McpHostBuilder WithConfiguration(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        _settings[key] = value;
        return this;
    }

    public McpHostBuilder WithOtlpEndpoint(string? endpoint)
    {
        if (endpoint is not null)
        {
            WithConfiguration(OtlpEndpointKey, endpoint);
        }

        return this;
    }

    public McpHostBuilder WithServiceEndpoints(string serviceName, params string[] endpoints)
    {
        ArgumentNullException.ThrowIfNull(serviceName);
        ArgumentNullException.ThrowIfNull(endpoints);
        for (var index = 0; index < endpoints.Length; index++)
        {
            WithConfiguration(string.Create(CultureInfo.InvariantCulture, $"Services:{serviceName}:http:{index}"), endpoints[index]);
        }

        return this;
    }

    public HostApplicationBuilder Build()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ApplicationName = HostData.ApplicationName,
        });
        if (_settings.Count > 0)
        {
            builder.Configuration.AddInMemoryCollection(_settings);
        }

        return builder;
    }
}
