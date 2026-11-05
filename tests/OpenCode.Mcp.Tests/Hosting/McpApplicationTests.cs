using System.Globalization;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using NSubstitute.Extensions;
using OpenCode.Mcp.Hosting;
using OpenCode.Mcp.Hosting.Abstractions;
using OpenCode.Mcp.Hosting.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Hosting;

/// <summary>Checks selection, failure propagation and owned host lifetime through the launcher.</summary>
public sealed class McpApplicationTests
{
    private const string InvalidSelector = "Select 'stdio' or 'http' once using '--transport <value>'. (Parameter 'arguments')";

    /// <summary>Invalid selectors produce one diagnostic and never reach host creation.</summary>
    [Test]
    [Arguments((object)new[] { "--transport", "other" })]
    [Arguments((object)new[] { "--transport", "HTTP" })]
    [Arguments((object)new[] { "--transport" })]
    [Arguments((object)new[] { "--transport", "stdio", "--transport", "stdio" })]
    [Arguments((object)new[] { "--transport=http" })]
    public async Task RunAsync_Should_Return_Two_Without_Creating_A_Host_When_Selection_Is_Invalid(string[] arguments)
    {
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var factory = Substitute.For<IMcpHostFactory>();
        var context = await Assert.That(TestContext.Current).IsNotNull();
        var result = await new McpApplication(factory, errors).RunAsync(arguments, context.Execution.CancellationToken);

        await Assert.That(result).IsEqualTo(2);
        await Assert.That(errors.ToString()).IsEqualTo(InvalidSelector + Environment.NewLine);
        factory.DidNotReceive().Create(Arg.Any<LaunchOptions>());
    }

    /// <summary>Valid selection preserves transport and the exact remaining argument order.</summary>
    [Test]
    [Arguments(new[] { "--alpha", "two", "--beta", "one" }, McpTransport.Stdio)]
    [Arguments(new[] { "--alpha", "two", "--transport", "stdio", "--beta", "one" }, McpTransport.Stdio)]
    [Arguments(new[] { "--alpha", "two", "--transport", "http", "--beta", "one" }, McpTransport.Http)]
    public async Task RunAsync_Should_Forward_Validated_Options_When_Selection_Is_Valid(string[] arguments, McpTransport expected)
    {
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        using var stopping = new CancellationTokenSource();
        var events = new List<string>();
        var factory = Substitute.For<IMcpHostFactory>();
        LaunchOptions? observed = null;
        factory.Create(Arg.Any<LaunchOptions>()).Returns(call =>
        {
            observed = call.Arg<LaunchOptions>();
            return CreateHost(stopping, events);
        });

        var context = await Assert.That(TestContext.Current).IsNotNull();
        var result = await new McpApplication(factory, errors).RunAsync(arguments, context.Execution.CancellationToken);

        var options = await Assert.That(observed).IsNotNull();
        await Assert.That(options.Transport).IsEqualTo(expected);
        await Assert.That(options.HostArguments).IsEquivalentTo(["--alpha", "two", "--beta", "one"], CollectionOrdering.Matching);
        factory.Received(1).Create(Arg.Any<LaunchOptions>());
        await Assert.That(result).IsEqualTo(0);
        await Assert.That(events).IsEquivalentTo(["start", "stop", "dispose"], CollectionOrdering.Matching);
    }

    /// <summary>Success follows actual host start, shutdown and disposal, with no diagnostic.</summary>
    [Test]
    public async Task RunAsync_Should_Return_Zero_After_Host_Shutdown_When_Selection_Is_Valid()
    {
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        using var stopping = new CancellationTokenSource();
        var events = new List<string>();
        var factory = Substitute.For<IMcpHostFactory>();
        factory.Create(Arg.Any<LaunchOptions>()).Returns(_ => CreateHost(stopping, events));

        var context = await Assert.That(TestContext.Current).IsNotNull();
        var result = await new McpApplication(factory, errors).RunAsync([], context.Execution.CancellationToken);

        await Assert.That(events).IsEquivalentTo(["start", "stop", "dispose"], CollectionOrdering.Matching);
        await Assert.That(result).IsEqualTo(0);
        await Assert.That(errors.ToString()).IsEmpty();
    }

    /// <summary>The launcher does not complete while asynchronous host disposal is held.</summary>
    [Test]
    public async Task RunAsync_Should_Await_Asynchronous_Disposal_When_Host_Supports_It()
    {
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        using var stopping = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = Substitute.For<IHost, IAsyncDisposable>();
        ConfigureLifetime(host, stopping);
        ((IAsyncDisposable)host).ReturnsForAll(_ =>
        {
            entered.SetResult();
            return new ValueTask(release.Task);
        });
        var factory = Substitute.For<IMcpHostFactory>();
        factory.Create(Arg.Any<LaunchOptions>()).Returns(host);
        var context = await Assert.That(TestContext.Current).IsNotNull();
        var run = new McpApplication(factory, errors).RunAsync([], context.Execution.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(run.IsCompleted).IsFalse();
        }
        finally
        {
            release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(15));
        }

        await Assert.That(await run).IsEqualTo(0);
        await Assert.That(host.ReceivedCalls().Select(static call => call.GetMethodInfo().Name)).DoesNotContain("Dispose");
    }

    /// <summary>Factory failures, including argument failures, escape unchanged.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task RunAsync_Should_Propagate_The_Exception_When_Factory_Creation_Fails(bool argumentFailure)
    {
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var factory = Substitute.For<IMcpHostFactory>();
        Exception failure = argumentFailure ? new ArgumentException("factory", nameof(argumentFailure)) : new InvalidOperationException("factory");
        factory.Create(Arg.Any<LaunchOptions>()).Returns(_ => throw failure);

        var context = await Assert.That(TestContext.Current).IsNotNull();
        var actual = await Assert.That(() => new McpApplication(factory, errors).RunAsync([], context.Execution.CancellationToken))
            .Throws<Exception>();

        await Assert.That(actual).IsSameReferenceAs(failure);
        await Assert.That(errors.ToString()).IsEmpty();
    }

    /// <summary>A failed start still disposes the owned host and does not become a selection refusal.</summary>
    [Test]
    public async Task RunAsync_Should_Dispose_The_Host_And_Propagate_When_Startup_Fails()
    {
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var failure = new InvalidOperationException("start");
        var host = Substitute.For<IHost>();
        host.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));
        var factory = Substitute.For<IMcpHostFactory>();
        factory.Create(Arg.Any<LaunchOptions>()).Returns(host);

        var context = await Assert.That(TestContext.Current).IsNotNull();
        var actual = await Assert.That(() => new McpApplication(factory, errors).RunAsync([], context.Execution.CancellationToken))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(actual).IsSameReferenceAs(failure);
        await Assert.That(host.ReceivedCalls().Count(static call => call.GetMethodInfo().Name == "Dispose")).IsEqualTo(1);
        await Assert.That(errors.ToString()).IsEmpty();
    }

    /// <summary>Missing dependencies fail at the public constructor boundary.</summary>
    [Test]
    [Arguments("factory")]
    [Arguments("errors")]
    public async Task McpApplication_Should_Reject_Null_When_A_Constructor_Dependency_Is_Missing(string parameter)
    {
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var factory = Substitute.For<IMcpHostFactory>();
        // Deliberately violate the constructor precondition selected by this row.
        await Assert.That(() => new McpApplication(parameter == "factory" ? null! : factory, parameter == "errors" ? null! : errors))
            .ThrowsExactly<ArgumentNullException>().WithParameterName(parameter);
    }

    private static IHost CreateHost(CancellationTokenSource stopping, List<string> events)
    {
        var host = Substitute.For<IHost>();
        ConfigureLifetime(host, stopping);
        host.StartAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            events.Add("start");
            await stopping.CancelAsync();
        });
        host.StopAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            events.Add("stop");
            return Task.CompletedTask;
        });
        host.When(static value => value.Dispose()).Do(_ => events.Add("dispose"));
        return host;
    }

    private static void ConfigureLifetime(IHost host, CancellationTokenSource stopping)
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(stopping.Token);
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IHostApplicationLifetime)).Returns(lifetime);
        host.Services.Returns(services);
        host.StartAsync(Arg.Any<CancellationToken>()).Returns(_ => stopping.CancelAsync());
    }
}
