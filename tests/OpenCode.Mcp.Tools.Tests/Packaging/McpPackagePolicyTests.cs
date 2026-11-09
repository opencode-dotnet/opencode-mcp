using OpenCode.Mcp.Tools.Packaging;
using OpenCode.Mcp.Tools.Tests.Packaging.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Packaging;

public sealed class McpPackagePolicyTests
{
    [Test]
    [Arguments("win-x64", "opencode-mcp.exe")]
    [Arguments("win-arm64", "opencode-mcp.exe")]
    [Arguments("linux-x64", "opencode-mcp")]
    [Arguments("linux-arm64", "opencode-mcp")]
    [Arguments("osx-x64", "opencode-mcp")]
    [Arguments("osx-arm64", "opencode-mcp")]
    public async Task ChildDiagnostics_Should_Accept_Only_The_Rid_Entry_Point_And_Ignore_Package_Modes(string rid, string entryPoint, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        builder.Metadata.Element(builder.Metadata.Name.Namespace + "id")?.SetValue("OpenCodeDotNet.Mcp." + rid);
        builder.Command.SetAttributeValue("EntryPoint", entryPoint);
        builder.Payload.Remove("opencode-mcp.exe");
        builder.Payload.Add(entryPoint, "native");
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        var receipt = builder.Receipt with { Rid = rid, Payload = [.. builder.Inventory().Select(static file => file with { UnixMode = 493 })] };
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package, builder.Identity, receipt)).IsEmpty();
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package with { EntryPoint = "wrong" }, builder.Identity, receipt))
            .IsEquivalentTo(["Package entry point must be " + entryPoint + "."], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ChildDiagnostics_Should_Report_All_Failures_In_Contract_Order(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var receipt = builder.Receipt with { Payload = builder.Inventory() };
        builder.Metadata.Element(builder.Metadata.Name.Namespace + "id")?.SetValue("wrong");
        builder.Metadata.Element(builder.Metadata.Name.Namespace + "version")?.SetValue("wrong");
        builder.Payload.Remove("LICENSE");
        builder.Payload["THIRD-PARTY-NOTICES.md"] = "NOTICES";
        builder.Manifest["name"] = "wrong";
        builder.Omitted.Add("README.md");
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package, builder.Identity, receipt)).IsEquivalentTo([
            "Package ID must be OpenCodeDotNet.Mcp.win-x64.", "Package version must be 0.1.0-preview.1.",
            "Missing payload file: LICENSE", "Changed payload file: THIRD-PARTY-NOTICES.md",
            "Manifest name must be io.github.opencode-dotnet/opencode-mcp.", "Missing package root file: README.md"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("missing", "Missing payload file: LICENSE")]
    [Arguments("extra", "Unexpected payload file: extra.dat")]
    [Arguments("changed", "Changed payload file: THIRD-PARTY-NOTICES.md")]
    public async Task ChildDiagnostics_Should_Compare_The_Whole_Payload(string fault, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var receipt = builder.Receipt with { Payload = builder.Inventory() };
        switch (fault)
        {
            case "missing": builder.Payload.Remove("LICENSE"); break;
            case "extra": builder.Payload.Add("extra.dat", "extra"); break;
            default: builder.Payload["THIRD-PARTY-NOTICES.md"] = "NOTICES"; break;
        }
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package, builder.Identity, receipt))
            .IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("README.md")]
    [Arguments("LICENSE")]
    [Arguments("THIRD-PARTY-NOTICES.md")]
    public async Task ChildDiagnostics_Should_Require_Root_Channel_Files(string name, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        builder.Omitted.Add(name);
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package, builder.Identity, builder.Receipt with { Payload = builder.Inventory() }))
            .IsEquivalentTo(["Missing package root file: " + name], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ChildDiagnostics_Should_Require_The_Exact_Manifest_Path(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        builder.ManifestPath = "elsewhere/server.json";
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package, builder.Identity, builder.Receipt with { Payload = builder.Inventory() }))
            .IsEquivalentTo(["Package manifest must be .mcp/server.json."], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("version", "wrong", "Package version must be 0.1.0-preview.1.")]
    [Arguments("packageType", "DotnetTool", "Package types must be exactly DotnetToolRidPackage.")]
    [Arguments("secondType", "McpServer", "Package types must be exactly DotnetToolRidPackage.")]
    [Arguments("Name", "wrong", "Package command must be opencode-mcp.")]
    [Arguments("owner command", "wrong", "Package command must be opencode-mcp.")]
    [Arguments("Runner", "dotnet", "Package runner must be executable.")]
    [Arguments("EntryPoint", "wrong", "Package entry point must be opencode-mcp.exe.")]
    [Arguments("commit", "wrong", "Package repository commit differs from native receipt.")]
    public async Task ChildDiagnostics_Should_Refuse_Metadata_Drift(string field, string value, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var ns = builder.Metadata.Name.Namespace;
        switch (field)
        {
            case "version": builder.Metadata.Element(ns + field)?.SetValue(value); break;
            case "packageType": builder.Metadata.Descendants(ns + field).Single().SetAttributeValue("name", value); break;
            case "secondType": builder.Metadata.Element(ns + "packageTypes")?.Add(new System.Xml.Linq.XElement(ns + "packageType", new System.Xml.Linq.XAttribute("name", value))); break;
            case "commit": builder.Metadata.Element(ns + "repository")?.SetAttributeValue(field, value); break;
            case "owner command":
                builder.Identity = builder.Identity with { CommandName = value };
                builder.Command.SetAttributeValue("Name", value);
                break;
            default: builder.Command.SetAttributeValue(field, value); break;
        }
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package, builder.Identity, builder.Receipt with { Payload = builder.Inventory() }))
            .IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ChildDiagnostics_Should_Refuse_A_Wrong_Id(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        builder.Metadata.Element(builder.Metadata.Name.Namespace + "id")?.SetValue("wrong");
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package, builder.Identity, builder.Receipt with { Payload = builder.Inventory() }))
            .IsEquivalentTo(["Package ID must be OpenCodeDotNet.Mcp.win-x64."], CollectionOrdering.Matching);
    }
}
