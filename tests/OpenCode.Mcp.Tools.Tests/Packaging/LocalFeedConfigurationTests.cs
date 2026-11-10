using System.Xml.Linq;
using OpenCode.Mcp.Tools.Tests.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Packaging;

public sealed class LocalFeedConfigurationTests
{
    [Test]
    public async Task Configuration_Should_Clear_Inherited_Sources_And_Name_Only_The_Complete_Feed(CancellationToken token)
    {
        var document = XDocument.Parse(await PackageArchiveBuilder.FixtureAsync("local-feed.nuget.config", token));
        var sources = document.Root?.Element("packageSources") ?? throw new InvalidOperationException("Missing packageSources.");
        await Assert.That(sources.Elements().Select(static element => element.Name.LocalName)).IsEquivalentTo(["clear", "add"], CollectionOrdering.Matching);
        var source = sources.Elements("add").Single();
        await Assert.That(source.Attribute("key")?.Value).IsEqualTo("local");
        await Assert.That(source.Attribute("value")?.Value).IsEqualTo("../artifacts/feed");
        var files = new MockFileSystem();
        var root = files.Path.Combine(files.Path.GetTempPath(), "repository");
        await Assert.That(files.Path.GetFullPath(source.Attribute("value")?.Value ?? throw new InvalidOperationException(), files.Path.Combine(root, "packaging")))
            .IsEqualTo(files.Path.Combine(root, "artifacts", "feed"));
    }
}
