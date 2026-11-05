using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using NSubstitute;
using OpenCode.Mcp.Hosting;
using OpenCode.Mcp.Hosting.Models;
using OpenTelemetry.Logs;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests;

/// <summary>Checks both transports through the shared production composition.</summary>
public sealed class McpAppTests
{
    private const string InvalidSelector = "Select 'stdio' or 'http' once using '--transport <value>'. (Parameter 'arguments')";

    /// <summary>The caller's writer receives a selection refusal before host composition begins.</summary>
    [Test]
    public async Task CreateApplication_Should_Report_Refusal_Without_Composing_A_Host_When_Selection_Is_Invalid()
    {
        using var errors = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var compositions = 0;
        var application = McpApp.CreateApplication(errors, _ => compositions++);
        var context = await Assert.That(TestContext.Current).IsNotNull();

        await Assert.That(await application.RunAsync(["--transport", "HTTP"], context.Execution.CancellationToken)).IsEqualTo(2);
        await Assert.That(errors.ToString()).IsEqualTo(InvalidSelector + Environment.NewLine);
        await Assert.That(compositions).IsEqualTo(0);
    }

    /// <summary>A missing diagnostic writer fails at the composition boundary.</summary>
    [Test]
    public async Task CreateApplication_Should_Reject_Null_When_Error_Writer_Is_Missing()
    {
        // Deliberately violate the public precondition.
        await Assert.That(() => McpApp.CreateApplication(null!)).ThrowsExactly<ArgumentNullException>()
            .WithParameterName("errors");
    }

    /// <summary>Both modes advertise the product assembly's version and the selected protocol.</summary>
    [Test]
    [Arguments(McpTransport.Stdio)]
    [Arguments(McpTransport.Http)]
    public async Task Configure_Should_Register_Product_Identity_And_Protocol_When_Transport_Is_Selected(McpTransport mode)
    {
        var builder = CreateBuilder();
        McpApp.Configure(builder, mode);
        using var services = builder.Services.BuildServiceProvider();
        var identity = services.GetRequiredService<ProductIdentity>();
        var options = services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var advertised = await Assert.That(options.ServerInfo).IsNotNull();
        var attribute = await Assert.That(typeof(ProductIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()).IsNotNull();
        var expectedVersion = attribute.InformationalVersion.Split('+')[0];

        await Assert.That(identity.Name).IsEqualTo("opencode-mcp");
        await Assert.That(identity.Version).IsEqualTo(expectedVersion);
        await Assert.That(services.GetRequiredService<ProductIdentity>()).IsSameReferenceAs(identity);
        await Assert.That(advertised.Name).IsEqualTo("opencode-mcp");
        await Assert.That(advertised.Version).IsEqualTo(expectedVersion);
        await Assert.That(options.ProtocolVersion).IsEqualTo("2026-07-28");
    }

    /// <summary>Typed registration exposes exactly the connectivity probe and its safety metadata.</summary>
    [Test]
    [Arguments(McpTransport.Stdio)]
    [Arguments(McpTransport.Http)]
    public async Task Configure_Should_Register_Only_Bootstrap_Ping_When_Transport_Is_Selected(McpTransport mode)
    {
        var builder = CreateBuilder();
        McpApp.Configure(builder, mode);
        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var tools = options.ToolCollection;
        Assert.NotNull(tools);

        await Assert.That(tools.Select(static tool => tool.ProtocolTool.Name))
            .IsEquivalentTo(["bootstrap_ping"], CollectionOrdering.Matching);
        var tool = tools.Single().ProtocolTool;
        await Assert.That(tool.Description)
            .IsEqualTo("Returns pong with this server's name and version to verify connectivity; performs no external work.");
        var annotations = await Assert.That(tool.Annotations).IsNotNull();
        await Assert.That(annotations.ReadOnlyHint).IsTrue();
        await Assert.That(annotations.IdempotentHint).IsTrue();
        await Assert.That(annotations.DestructiveHint).IsFalse();
        await Assert.That(annotations.OpenWorldHint).IsFalse();
    }

    /// <summary>HTTP explicitly replaces a valid stateful seed with stateless sessions.</summary>
    [Test]
    public async Task Configure_Should_Select_Stateless_Http_When_Http_Is_Requested()
    {
        var builder = CreateBuilder();
        builder.Services.Configure<HttpServerTransportOptions>(static options =>
            options.SessionMode = HttpServerSessionMode.Stateful);
        McpApp.Configure(builder, McpTransport.Http);
        using var services = builder.Services.BuildServiceProvider();

        await Assert.That(services.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value.SessionMode)
            .IsEqualTo(HttpServerSessionMode.Stateless);
    }

    /// <summary>Clearing precedes defaults, preserving OTel after the stderr console provider.</summary>
    [Test]
    [Arguments(McpTransport.Stdio)]
    [Arguments(McpTransport.Http)]
    public async Task Configure_Should_Preserve_OpenTelemetry_And_Route_Console_To_Standard_Error_When_Transport_Is_Selected(
        McpTransport mode)
    {
        var builder = CreateBuilder();
        using var oldProvider = Substitute.For<ILoggerProvider>();
        builder.Logging.AddProvider(oldProvider);
        McpApp.Configure(builder, mode);
        using var services = builder.Services.BuildServiceProvider();
        var providers = services.GetServices<ILoggerProvider>().ToArray();

        await Assert.That(providers.Select(static provider => provider.GetType()))
            .IsEquivalentTo([typeof(ConsoleLoggerProvider), typeof(OpenTelemetryLoggerProvider)], CollectionOrdering.Matching);
        await Assert.That(providers).DoesNotContain(oldProvider);
        await Assert.That(services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.LogToStandardErrorThreshold)
            .IsEqualTo(LogLevel.Trace);
    }

    /// <summary>Caller replacements survive production configuration and win single-service resolution.</summary>
    [Test]
    public async Task Configure_Should_Apply_Overrides_Last_When_Logging_Provider_Is_Replaced()
    {
        var builder = CreateBuilder();
        using var replacement = Substitute.For<ILoggerProvider>();
        McpApp.Configure(builder, McpTransport.Stdio, services => services.AddSingleton(replacement));
        using var services = builder.Services.BuildServiceProvider();
        var providers = services.GetServices<ILoggerProvider>().ToArray();

        await Assert.That(providers.Select(static provider => provider.GetType()))
            .IsEquivalentTo([typeof(ConsoleLoggerProvider), typeof(OpenTelemetryLoggerProvider), replacement.GetType()],
                CollectionOrdering.Matching);
        await Assert.That(providers[^1]).IsSameReferenceAs(replacement);
        await Assert.That(services.GetRequiredService<ILoggerProvider>()).IsSameReferenceAs(replacement);
    }

    /// <summary>Caller transport options override the final production registration.</summary>
    [Test]
    public async Task Configure_Should_Apply_Overrides_Last_When_Transport_Options_Are_Replaced()
    {
        var builder = CreateBuilder();
        McpApp.Configure(builder, McpTransport.Http, static services =>
            services.Configure<HttpServerTransportOptions>(static options =>
                options.SessionMode = HttpServerSessionMode.Stateful));
        using var services = builder.Services.BuildServiceProvider();

        await Assert.That(services.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value.SessionMode)
            .IsEqualTo(HttpServerSessionMode.Stateful);
    }

    /// <summary>A missing builder fails at the public boundary.</summary>
    [Test]
    public async Task Configure_Should_Refuse_Null_When_Builder_Is_Missing()
    {
        // Deliberately violate the non-null builder precondition.
        await Assert.That(() => McpApp.Configure(null!, McpTransport.Stdio))
            .ThrowsExactly<ArgumentNullException>().WithParameterName("builder");
    }

    private static HostApplicationBuilder CreateBuilder() => new(new HostApplicationBuilderSettings
    {
        DisableDefaults = true,
        ApplicationName = typeof(McpApp).Assembly.GetName().Name,
    });
}
