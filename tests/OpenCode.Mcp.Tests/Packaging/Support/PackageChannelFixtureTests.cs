using System.Text.Json.Nodes;
using System.Xml.Linq;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Packaging.Support;

public sealed class PackageChannelFixtureTests
{
    [Test]
    [Arguments("nuget", "none")]
    [Arguments("github-packages", "none")]
    [Arguments("github-packages", "fallback")]
    [Arguments("github-packages", "source")]
    [Arguments("nuget", "source")]
    [Arguments("nuget", "extra")]
    public async Task Resolve_Should_Map_Product_Packages_Only_To_GitHub(string channel, string fault, CancellationToken token)
    {
        var files = new MockFileSystem();
        var fixture = await CreateFixtureAsync(files, channel, fault, token);
        var config = files.Path.Combine(AppContext.BaseDirectory, "selected.config");
        if (fault != "none")
        {
            await Assert.That(async () => { await fixture.ResolveAsync(token); }).ThrowsExactly<InvalidOperationException>().WithMessage(
                "Channel configuration must match packaging/" + (channel == "nuget" ? "nuget-org" : "github-packages")
                + ".nuget.config (sources and mapping).", StringComparison.Ordinal);
            return;
        }
        await Assert.That(await fixture.ResolveAsync(token)).IsEqualTo(config);
        await using var stream = files.File.OpenRead(config);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, token);
        string?[] expectedSources = channel == "nuget" ? ["nuget.org"] : ["opencode-dotnet", "nuget.org"];
        await Assert.That(document.Descendants("add").Select(static source => source.Attribute("key")?.Value))
            .IsEquivalentTo(expectedSources, CollectionOrdering.Matching);
        string?[] expectedPatterns = channel == "nuget" ? [] : ["OpenCodeDotNet.Mcp", "OpenCodeDotNet.Mcp.*", "*"];
        await Assert.That(document.Descendants("package").Select(static package => package.Attribute("pattern")?.Value))
            .IsEquivalentTo(expectedPatterns, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("channel", "OPENCODE_MCP_TEST_CHANNEL must be github-packages or nuget: unknown")]
    [Arguments("config", "OPENCODE_MCP_TEST_CHANNEL_CONFIG file does not exist.")]
    [Arguments("rid", "OPENCODE_MCP_TEST_RID is not in the native manifest: unknown")]
    public async Task Resolve_Should_Refuse_Unknown_Inputs(string fault, string message, CancellationToken token)
    {
        var files = new MockFileSystem();
        var fixture = await CreateFixtureAsync(files, "nuget", fault, token);
        await Assert.That(async () => { await fixture.ResolveAsync(token); }).ThrowsExactly<InvalidOperationException>().WithMessage(message, StringComparison.Ordinal);
    }

    [Test]
    public async Task Resolve_Should_Preserve_Credentials_Without_Using_Them_As_Sources(CancellationToken token)
    {
        var files = new MockFileSystem();
        var fixture = await CreateFixtureAsync(files, "github-packages", "credentials", token);
        var path = files.Path.Combine(AppContext.BaseDirectory, "selected.config");
        await Assert.That(await fixture.ResolveAsync(token)).IsEqualTo(path);
        await using var stream = files.File.OpenRead(path);
        var selected = await XDocument.LoadAsync(stream, LoadOptions.None, token);
        await Assert.That(selected.Descendants("opencode-dotnet").Elements("add").Select(static element => element.Attribute("value")?.Value))
            .IsEquivalentTo((string?[])["actor", "credential"], CollectionOrdering.Matching);
    }

    private static async Task<PackageChannelFixture> CreateFixtureAsync(MockFileSystem files, string channel, string fault, CancellationToken token)
    {
        var repository = AppContext.BaseDirectory;
        files.Directory.CreateDirectory(files.Path.Combine(repository, "packaging"));
        await files.File.WriteAllTextAsync(files.Path.Combine(repository, "OpenCode.Mcp.slnx"), "solution marker", token);
        var feed = files.Path.Combine(repository, "reference-feed");
        files.Directory.CreateDirectory(feed);
        var sources = new XElement("packageSources", new XElement("clear"));
        var document = new XDocument(new XElement("configuration", sources));
        if (channel == "github-packages")
        {
            sources.Add(new XElement("add", new XAttribute("key", "opencode-dotnet"), new XAttribute("value", "https://nuget.pkg.github.com/opencode-dotnet/index.json")));
            document.Root?.Add(new XElement("packageSourceMapping", new XElement("packageSource", new XAttribute("key", "opencode-dotnet"),
                new XElement("package", new XAttribute("pattern", "OpenCodeDotNet.Mcp")), new XElement("package", new XAttribute("pattern", "OpenCodeDotNet.Mcp.*"))),
                new XElement("packageSource", new XAttribute("key", "nuget.org"), new XElement("package", new XAttribute("pattern", "*")))));
        }
        sources.Add(new XElement("add", new XAttribute("key", "nuget.org"), new XAttribute("value", "https://api.nuget.org/v3/index.json")));
        await files.File.WriteAllTextAsync(files.Path.Combine(repository, "packaging", channel == "nuget" ? "nuget-org.nuget.config" : "github-packages.nuget.config"), document.ToString(), token);
        if (fault == "source")
        {
            sources.Elements("add").First().SetAttributeValue("value", feed);
        }
        if (fault == "fallback")
        {
            document.Descendants("packageSource").First().Remove();
        }
        if (fault == "extra")
        {
            document.Root?.Add(new XElement("disabledPackageSources", new XElement("clear")));
        }
        if (fault == "credentials")
        {
            document.Root?.Add(new XElement("packageSourceCredentials", new XElement("opencode-dotnet",
                new XElement("add", new XAttribute("key", "Username"), new XAttribute("value", "actor")),
                new XElement("add", new XAttribute("key", "ClearTextPassword"), new XAttribute("value", "credential")))));
        }
        var config = files.Path.Combine(repository, "selected.config");
        await files.File.WriteAllTextAsync(config, document.ToString(), token);
        if (fault == "config")
        {
            files.File.Delete(config);
        }
        await files.File.WriteAllTextAsync(files.Path.Combine(repository, "packaging", "native-manifest.json"),
            new JsonObject { ["runtimes"] = new JsonArray(new JsonObject { ["rid"] = "linux-x64" }) }.ToJsonString(), token);
        return new PackageChannelFixture(files, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OPENCODE_MCP_TEST_CHANNEL"] = fault == "channel" ? "unknown" : channel,
            ["OPENCODE_MCP_TEST_CHANNEL_CONFIG"] = config,
            ["OPENCODE_MCP_TEST_FEED"] = feed,
            ["OPENCODE_MCP_TEST_RID"] = fault == "rid" ? "unknown" : "linux-x64",
        });
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" ")]
    public async Task Resolve_Should_Require_Explicit_Channel_Configuration(string? missing, CancellationToken token)
    {
        var fixture = new PackageChannelFixture(new MockFileSystem(), new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OPENCODE_MCP_TEST_CHANNEL"] = missing,
            ["OPENCODE_MCP_TEST_CHANNEL_CONFIG"] = missing,
            ["OPENCODE_MCP_TEST_FEED"] = missing,
            ["OPENCODE_MCP_TEST_RID"] = missing,
        });
        await Assert.That(async () => { await fixture.ResolveAsync(token); }).ThrowsExactly<InvalidOperationException>().WithMessage(
            "OPENCODE_MCP_TEST_CHANNEL is absent or blank.\nOPENCODE_MCP_TEST_CHANNEL_CONFIG is absent or blank.\n"
            + "OPENCODE_MCP_TEST_FEED is absent or blank.\nOPENCODE_MCP_TEST_RID is absent or blank.", StringComparison.Ordinal);
    }
}
