using OpenCode.Mcp.Tools.Native.Abstractions;
using OpenCode.Mcp.Tools.Packaging;
using OpenCode.Mcp.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Packaging;

public sealed partial class McpPackagePolicyTests
{
    [Test]
    [Arguments("renamed.nuspec")]
    [Arguments("nested/OpenCodeDotNet.Mcp.win-x64.nuspec")]
    public async Task ChildDiagnostics_Should_Pin_The_Nuspec_To_The_Package_Id_At_Root(string path, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        builder.NuspecPath = path;
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package, builder.Identity, builder.Receipt with { Payload = builder.Inventory() }))
            .IsEquivalentTo([PackagingData.UnexpectedEntry + path], CollectionOrdering.Matching);
    }

    [Test]
    public async Task PackDiagnostics_Should_Report_Each_Distinct_Warning_Once()
    {
        const string second = "src/product.csproj(2,1): warning NU5000: another warning [src/product.csproj]";
        var result = new NativeProcessResult(0,
            PackagingData.PackWarning + "\n" + second + "\n" + PackagingData.PackWarning + "\r\n" + second, "");
        await Assert.That(McpPackagePolicy.PackDiagnostics(result)).IsEquivalentTo([
            PackagingData.PackWarningDiagnostic, "Child pack warning: " + second], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("content/extra.dat")]
    [Arguments("package/services/metadata/core-properties/not-metadata.txt")]
    public async Task ChildDiagnostics_Should_Refuse_An_Entry_Outside_The_Closed_Set(string path, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        builder.AdditionalFiles.Add(path, "extra");
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package, builder.Identity, builder.Receipt with { Payload = builder.Inventory() }))
            .IsEquivalentTo([PackagingData.UnexpectedEntry + path], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ChildDiagnostics_Should_Refuse_Repeated_Core_Properties_In_Ordinal_Order(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        builder.AdditionalFiles.Add("package/services/metadata/core-properties/earlier.psmdcp", "extra");
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package, builder.Identity, builder.Receipt with { Payload = builder.Inventory() }))
            .IsEquivalentTo([PackagingData.UnexpectedEntry + "package/services/metadata/core-properties/earlier.psmdcp",
                PackagingData.UnexpectedEntry + "package/services/metadata/core-properties/fixture.psmdcp"], CollectionOrdering.Matching);
    }

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
            .IsEquivalentTo([PackagingData.EntryPointDiagnostic + entryPoint + "."], CollectionOrdering.Matching);
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
            PackagingData.PackageIdDiagnostic, PackagingData.PackageVersionDiagnostic,
            PackagingData.MissingLicenseDiagnostic, PackagingData.ChangedNoticesDiagnostic,
            PackagingData.ManifestNameDiagnostic, PackagingData.MissingRootFile + "README.md"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("missing", PackagingData.MissingLicenseDiagnostic)]
    [Arguments("extra", "Unexpected payload file: extra.dat")]
    [Arguments("changed", PackagingData.ChangedNoticesDiagnostic)]
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
            .IsEquivalentTo([PackagingData.MissingRootFile + name], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ChildDiagnostics_Should_Require_The_Exact_Manifest_Path(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        builder.ManifestPath = "elsewhere/server.json";
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package, builder.Identity, builder.Receipt with { Payload = builder.Inventory() }))
            .IsEquivalentTo([PackagingData.ManifestPathDiagnostic, PackagingData.UnexpectedEntry + "elsewhere/server.json"], CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(ChildVersionCases))]
    [Arguments("packageType", "DotnetTool", PackagingData.ChildTypesDiagnostic)]
    [Arguments("secondType", "McpServer", PackagingData.ChildTypesDiagnostic)]
    [Arguments("Name", "wrong", PackagingData.CommandDiagnostic)]
    [Arguments("owner command", "wrong", PackagingData.CommandDiagnostic)]
    [Arguments("Runner", "dotnet", "Package runner must be executable.")]
    [Arguments("EntryPoint", "wrong", PackagingData.EntryPointDiagnostic + "opencode-mcp.exe.")]
    [Arguments("commit", "wrong", PackagingData.RepositoryCommitDiagnostic)]
    public async Task ChildDiagnostics_Should_Refuse_Metadata_Drift(string field, string value, string diagnostic, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
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

    public static IEnumerable<object[]> ChildVersionCases() => [["version", "wrong", PackagingData.PackageVersionDiagnostic]];

    [Test]
    public async Task ChildDiagnostics_Should_Refuse_A_Wrong_Id(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        builder.Metadata.Element(builder.Metadata.Name.Namespace + "id")?.SetValue("wrong");
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package, builder.Identity, builder.Receipt with { Payload = builder.Inventory() }))
            .IsEquivalentTo([PackagingData.PackageIdDiagnostic], CollectionOrdering.Matching);
    }
}
