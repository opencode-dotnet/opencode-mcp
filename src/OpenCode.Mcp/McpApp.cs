using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using OpenCode.Mcp.Hosting;
using OpenCode.Mcp.Hosting.Models;
using OpenCode.Mcp.ServiceDefaults;
using OpenCode.Mcp.Tools.Bootstrap;

namespace OpenCode.Mcp;

/// <summary>Owns the shared production composition for both MCP transports.</summary>
public static class McpApp
{
    /// <summary>Configures the selected MCP transport and shared services, applying caller overrides last.</summary>
    /// <param name="builder">The host receiving the production registrations.</param>
    /// <param name="mode">The validated transport selection.</param>
    /// <param name="overrideServices">Optional replacements applied after production registrations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The product assembly has no informational version.</exception>
    public static void Configure(
        IHostApplicationBuilder builder, McpTransport mode, Action<IServiceCollection>? overrideServices = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(static options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.AddServiceDefaults();

        var identity = new ProductIdentity();
        builder.Services.AddSingleton(identity);
        var mcp = builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation { Name = identity.Name, Version = identity.Version };
            options.ProtocolVersion = "2026-07-28";
        }).WithTools<BootstrapTools>();

        if (mode == McpTransport.Stdio)
        {
            mcp.WithStdioServerTransport();
        }
        else
        {
            mcp.WithHttpTransport(static options => options.SessionMode = HttpServerSessionMode.Stateless);
        }

        overrideServices?.Invoke(builder.Services);
    }
}
