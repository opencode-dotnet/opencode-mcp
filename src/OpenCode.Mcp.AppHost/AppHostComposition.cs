namespace OpenCode.Mcp.AppHost;

/// <summary>Declares the product's local development resource.</summary>
public static class AppHostComposition
{
    /// <summary>Connects the real HTTP product to its discovered endpoint and readiness check.</summary>
    /// <param name="builder">The distributed application receiving the product resource.</param>
    /// <exception cref="ArgumentNullException">The builder is null.</exception>
    public static void Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var server = builder.AddProject<Projects.OpenCode_Mcp>("mcp", launchProfileName: null)
            .WithArgs("--transport", "http")
            .WithHttpEndpoint(name: "http", isProxied: false)
            .WithEndpoint("http", static endpoint => endpoint.TargetHost = "127.0.0.1")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .DisableForwardedHeaders()
            .WithHttpHealthCheck("/health", endpointName: "http");

        server.WithEnvironment("ASPNETCORE_URLS", ReferenceExpression.Create(
            $"http://127.0.0.1:{server.GetEndpoint("http").Property(EndpointProperty.TargetPort)}"));
    }
}
