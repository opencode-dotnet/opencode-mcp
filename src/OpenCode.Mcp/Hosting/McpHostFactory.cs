using OpenCode.Mcp.Hosting.Abstractions;
using OpenCode.Mcp.Hosting.Models;
using OpenCode.Mcp.ServiceDefaults;

namespace OpenCode.Mcp.Hosting;

/// <summary>Builds the production hosts and their transport boundaries.</summary>
public sealed class McpHostFactory : IMcpHostFactory
{
    private readonly Action<IServiceCollection>? _overrideServices;

    /// <summary>Creates the production host adapter.</summary>
    /// <param name="overrideServices">Optional replacements applied after production host registrations.</param>
    public McpHostFactory(Action<IServiceCollection>? overrideServices = null) => _overrideServices = overrideServices;

    /// <summary>Creates the selected production host without starting it.</summary>
    /// <param name="options">The validated transport and remaining host arguments.</param>
    /// <returns>An unstarted host whose caller owns execution and disposal.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="InvalidOperationException">HTTP configuration violates the local endpoint boundary.</exception>
    public IHost Create(LaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Transport == McpTransport.Stdio)
        {
            var host = Host.CreateApplicationBuilder([.. options.HostArguments]);
            McpApp.Configure(host, McpTransport.Stdio, _overrideServices);
            return host.Build();
        }

        var builder = WebApplication.CreateSlimBuilder([.. options.HostArguments]);
        var addresses = HttpEndpointPolicy.ResolveUrls(builder.Configuration);
        builder.WebHost.UseUrls(string.Join(';', addresses.Select(static address => address.GetLeftPart(UriPartial.Authority))));
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Configure(builder.Configuration.GetSection("Kestrel"), reloadOnChange: false));
        builder.Services.AddHostFiltering(static filter =>
        {
            filter.AllowedHosts = ["localhost", "127.0.0.1", "[::1]"];
            filter.AllowEmptyHosts = false;
        });
        McpApp.Configure(builder, McpTransport.Http, _overrideServices);
        var app = builder.Build();
        app.Use(static (context, next) =>
        {
            if (!HttpEndpointPolicy.IsAllowedOrigin(context.Request.Headers.TryGetValue("Origin", out var origin) ? origin.ToString() : null))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            return next(context);
        });
        app.MapMcp("/mcp");
        app.MapDefaultEndpoints();
        return app;
    }
}
