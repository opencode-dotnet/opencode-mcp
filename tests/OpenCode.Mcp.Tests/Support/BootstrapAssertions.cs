using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Support;

internal sealed class BootstrapAssertions
{
    internal static readonly string?[] ModernMethods = [ProtocolData.DiscoverMethod, ProtocolData.ListMethod, ProtocolData.CallMethod];
    private static readonly string?[] CompleteResults = [ProtocolData.CompleteResult, ProtocolData.CompleteResult, ProtocolData.CompleteResult];
    private static readonly string?[] SupportedVersions = [ProtocolData.Revision];
    public string ExpectedVersion { get; } = typeof(McpApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion.Split('+')[0] ?? throw new InvalidOperationException("Product informational version is missing.");

    public async Task AssertBootstrapAsync(McpClient client, CancellationToken token)
    {
        await Assert.That(client.NegotiatedProtocolVersion).IsEqualTo(ProtocolData.Revision);
        var identity = await Assert.That(client.ServerInfo).IsNotNull();
        await Assert.That(identity.Name).IsEqualTo("opencode-mcp");
        await Assert.That(identity.Version).IsEqualTo(ExpectedVersion);
        var capabilities = await Assert.That(client.ServerCapabilities).IsNotNull();
        await Assert.That(capabilities.Tools).IsNotNull();
        var tools = await client.ListToolsAsync(cancellationToken: token);
        await Assert.That(tools.Select(static tool => tool.Name)).IsEquivalentTo([ProtocolData.PingTool], CollectionOrdering.Matching);
        var tool = tools.Single().ProtocolTool;
        await Assert.That(tool.Description).IsEqualTo("Returns pong with this server's name and version to verify connectivity; performs no external work.");
        var hints = await Assert.That(tool.Annotations).IsNotNull();
        await Assert.That(hints.ReadOnlyHint).IsTrue();
        await Assert.That(hints.IdempotentHint).IsTrue();
        await Assert.That(hints.DestructiveHint).IsFalse();
        await Assert.That(hints.OpenWorldHint).IsFalse();
        await Assert.That(tool.InputSchema.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(tool.InputSchema.GetProperty("properties").EnumerateObject()).IsEmpty();
        var result = await client.CallToolAsync(ProtocolData.PingTool, cancellationToken: token);
        await Assert.That(result.IsError).IsNotEqualTo(true);
        await Assert.That(result.Content.Select(static content => content.Type)).IsEquivalentTo(["text"], CollectionOrdering.Matching);
        await Assert.That(result.Content.OfType<TextContentBlock>().Select(static content => content.Text))
            .IsEquivalentTo([$"pong opencode-mcp {ExpectedVersion}"], CollectionOrdering.Matching);
    }

    public async Task AssertResultsAsync(IReadOnlyList<JsonElement> results)
    {
        await Assert.That(results.Select(static result => result.GetProperty("resultType").GetString()))
            .IsEquivalentTo(CompleteResults, CollectionOrdering.Matching);
        await Assert.That(results[0].GetProperty("supportedVersions").EnumerateArray().Select(static value => value.GetString()))
            .IsEquivalentTo(SupportedVersions, CollectionOrdering.Matching);
        var identity = results[0].GetProperty("_meta").GetProperty(MetaKeys.ServerInfo);
        await Assert.That(identity.GetProperty("name").GetString()).IsEqualTo("opencode-mcp");
        await Assert.That(identity.GetProperty("version").GetString()).IsEqualTo(ExpectedVersion);
        await Assert.That(results[0].GetProperty("capabilities").EnumerateObject().Select(static property => property.Name)).Contains("tools");
    }
}
