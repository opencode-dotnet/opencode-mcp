using System.Xml.Linq;
using OpenCode.Mcp.Tools.Packaging;
using OpenCode.Mcp.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Packaging;

public sealed class McpPackageRootPolicyTests
{
    [Test]
    public async Task RootDiagnostics_Should_Accept_The_Sdk_Pointer_Without_Implementation(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(package.RuntimeIdentifierPackages.Select(static mapping => mapping.RuntimeIdentifier))
            .IsEquivalentTo(builder.Identity.RuntimeIdentifiers, CollectionOrdering.Matching);
        await Assert.That(package.RuntimeIdentifierPackages.Select(static mapping => mapping.Id))
            .IsEquivalentTo(builder.Identity.RuntimeIdentifiers.Select(rid => builder.Identity.Id + "." + rid), CollectionOrdering.Matching);
        await Assert.That(package.Runner).IsNull();
        await Assert.That(package.EntryPoint).IsNull();
        await Assert.That(package.Payload).IsEmpty();
        await Assert.That(McpPackagePolicy.RootDiagnostics(package, builder.Identity, builder.Receipt.SourceCommit)).IsEmpty();
    }

    [Test]
    [Arguments("id", "Package ID must be OpenCodeDotNet.Mcp.")]
    [Arguments("version", "version")]
    [Arguments("type", PackagingData.RootTypesDiagnostic)]
    [Arguments("extra type", PackagingData.RootTypesDiagnostic)]
    [Arguments("command", PackagingData.CommandDiagnostic)]
    [Arguments("runner", PackagingData.RootRunnerDiagnostic)]
    [Arguments("entry", PackagingData.RootEntryPointDiagnostic)]
    [Arguments("payload", "Root package must carry no implementation: asset.dll")]
    [Arguments("manifest", PackagingData.ManifestPathDiagnostic + "\n" + PackagingData.UnexpectedEntry + "other/server.json")]
    [Arguments("readme", PackagingData.MissingRootFile + "README.md")]
    [Arguments("license", PackagingData.MissingRootFile + "LICENSE")]
    [Arguments("notices", PackagingData.MissingRootFile + "THIRD-PARTY-NOTICES.md")]
    [Arguments("commit", PackagingData.RepositoryCommitDiagnostic)]
    [Arguments("renamed nuspec", PackagingData.UnexpectedEntry + "renamed.nuspec")]
    [Arguments("nested nuspec", PackagingData.UnexpectedEntry + "nested/OpenCodeDotNet.Mcp.nuspec")]
    [Arguments("outside", PackagingData.UnexpectedEntry + "content/extra.dat")]
    [Arguments("manifest policy", PackagingData.ManifestNameDiagnostic)]
    public async Task RootDiagnostics_Should_Refuse_Each_Root_Contract_Defect(string fault, string diagnostic, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var ns = builder.Metadata.Name.Namespace;
        switch (fault)
        {
            case "id": builder.Metadata.Element(ns + "id")?.SetValue("wrong"); break;
            case "version": builder.Metadata.Element(ns + "version")?.SetValue("wrong"); diagnostic = PackagingData.PackageVersionDiagnostic; break;
            case "type": builder.Metadata.Descendants(ns + "packageType").First().Remove(); break;
            case "extra type": builder.Metadata.Element(ns + "packageTypes")?.Add(new XElement(ns + "packageType", new XAttribute("name", "DotnetToolRidPackage"))); break;
            case "command": builder.Command.SetAttributeValue("Name", "wrong"); break;
            case "runner": builder.Command.SetAttributeValue("Runner", ""); break;
            case "entry": builder.Command.SetAttributeValue("EntryPoint", ""); break;
            case "payload": builder.Payload.Add("asset.dll", "managed"); break;
            case "manifest": builder.ManifestPath = "other/server.json"; break;
            case "readme": builder.Omitted.Add("README.md"); break;
            case "license": builder.Omitted.Add("LICENSE"); break;
            case "notices": builder.Omitted.Add("THIRD-PARTY-NOTICES.md"); break;
            case "commit": builder.Metadata.Element(ns + "repository")?.SetAttributeValue("commit", "wrong"); break;
            case "renamed nuspec": builder.NuspecPath = "renamed.nuspec"; break;
            case "nested nuspec": builder.NuspecPath = "nested/OpenCodeDotNet.Mcp.nuspec"; break;
            case "outside": builder.AdditionalFiles.Add("content/extra.dat", "extra"); break;
            case "manifest policy": builder.Manifest["name"] = "wrong"; break;
        }
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpPackagePolicy.RootDiagnostics(package, builder.Identity, builder.Receipt.SourceCommit))
            .IsEquivalentTo(diagnostic.Split('\n'), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("any")]
    [Arguments("missing")]
    [Arguments("extra")]
    [Arguments("order")]
    [Arguments("id")]
    public async Task RootDiagnostics_Should_Require_The_Complete_Ordered_Mapping_Sequence(string fault, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var mappings = builder.Settings.Descendants("RuntimeIdentifierPackage").ToArray();
        switch (fault)
        {
            case "any": mappings[^1].AddAfterSelf(new XElement("RuntimeIdentifierPackage", new XAttribute("RuntimeIdentifier", "any"), new XAttribute("Id", builder.Identity.Id + ".any"))); break;
            case "extra": mappings[^1].AddAfterSelf(new XElement("RuntimeIdentifierPackage", new XAttribute("RuntimeIdentifier", "other"), new XAttribute("Id", builder.Identity.Id + ".other"))); break;
            case "missing": mappings[0].Remove(); break;
            case "id": mappings[0].SetAttributeValue("Id", "wrong"); break;
            case "order": mappings[0].Remove(); mappings[1].AddAfterSelf(mappings[0]); break;
        }
        var observed = builder.Settings.Descendants("RuntimeIdentifierPackage").Select(static mapping => mapping.Attribute("RuntimeIdentifier")?.Value + "=" + mapping.Attribute("Id")?.Value).ToArray();
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpPackagePolicy.RootDiagnostics(package, builder.Identity, builder.Receipt.SourceCommit))
            .IsEquivalentTo([PackagingData.MappingsDiagnostic + string.Join(", ", observed)], CollectionOrdering.Matching);
    }
}
