using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenCode.Mcp.Tests.Support;

public sealed class McpHostBuilderTests
{
    [Test]
    public async Task Build_Should_Use_Product_Application_Name_With_Default_Providers_Disabled()
    {
        var builder = new McpHostBuilder().Build();
        using var services = builder.Services.BuildServiceProvider();

        await Assert.That(builder.Environment.ApplicationName).IsEqualTo("OpenCode.Mcp");
        await Assert.That(services.GetServices<ILoggerProvider>()).IsEmpty();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("http://collector.invalid")]
    public async Task Build_Should_Keep_Optional_Otlp_And_In_Memory_Settings(string? endpoint)
    {
        var builder = new McpHostBuilder()
            .WithOtlpEndpoint(endpoint)
            .WithConfiguration("Probe:Mode", "enabled")
            .Build();

        await Assert.That(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]).IsEqualTo(endpoint);
        await Assert.That(builder.Configuration["Probe:Mode"]).IsEqualTo("enabled");
    }

    [Test]
    public async Task Build_Should_Index_Service_Discovery_Addresses_In_Order()
    {
        const string firstEndpoint = "http://z-backend.invalid";
        const string secondEndpoint = "http://a-backend.invalid";
        var builder = new McpHostBuilder().WithServiceEndpoints("backend", firstEndpoint, secondEndpoint).Build();

        await Assert.That(builder.Configuration["Services:backend:http:0"]).IsEqualTo(firstEndpoint);
        await Assert.That(builder.Configuration["Services:backend:http:1"]).IsEqualTo(secondEndpoint);
    }
}
