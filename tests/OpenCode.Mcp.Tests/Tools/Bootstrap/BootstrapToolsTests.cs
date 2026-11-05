using OpenCode.Mcp.Hosting;
using OpenCode.Mcp.Tools.Bootstrap;

namespace OpenCode.Mcp.Tests.Tools.Bootstrap;

/// <summary>Checks the harmless connectivity tool's response and invocation shape.</summary>
public sealed class BootstrapToolsTests
{
    /// <summary>The connectivity probe includes the server identity in its deterministic response.</summary>
    [Test]
    public async Task Ping_Should_Return_Pong_With_Server_Identity_When_Invoked()
    {
        var identity = new ProductIdentity();
        var tool = new BootstrapTools(identity);

        var result = tool.Ping();

        await Assert.That(result).IsEqualTo($"pong {identity.Name} {identity.Version}");
        const string prefix = "pong opencode-mcp ";
        await Assert.That(result).StartsWith(prefix);
        await Assert.That(result.Length).IsGreaterThan(prefix.Length);
    }

    /// <summary>The SDK can construct a tool instance for each invocation.</summary>
    [Test]
    public async Task Ping_Should_Remain_An_Instance_Method_When_Exposed_As_A_Tool()
    {
        var method = await Assert.That(typeof(BootstrapTools).GetMethod(nameof(BootstrapTools.Ping))).IsNotNull();

        await Assert.That(method.IsStatic).IsFalse();
    }
}
