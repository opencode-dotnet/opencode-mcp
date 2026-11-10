using OpenCode.Mcp.Tools.Packaging;
using OpenCode.Mcp.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Packaging;

public sealed partial class McpPackagePolicyTests
{
    [Test]
    [Arguments(true, "authors", "wrong", PackagingData.AuthorsDiagnostic)]
    [Arguments(false, "authors", "wrong", PackagingData.AuthorsDiagnostic)]
    [Arguments(true, "authors", null, PackagingData.AuthorsDiagnostic)]
    [Arguments(false, "authors", null, PackagingData.AuthorsDiagnostic)]
    [Arguments(true, "authors", "deniz İrgin", PackagingData.AuthorsDiagnostic)]
    [Arguments(false, "authors", "deniz İrgin", PackagingData.AuthorsDiagnostic)]
    [Arguments(true, "authors", "Deniz İrgin Jr", PackagingData.AuthorsDiagnostic)]
    [Arguments(false, "authors", "Deniz İrgin Jr", PackagingData.AuthorsDiagnostic)]
    [Arguments(true, "authors", "Deniz Irgin", PackagingData.AuthorsDiagnostic)]
    [Arguments(false, "authors", "Deniz Irgin", PackagingData.AuthorsDiagnostic)]
    [Arguments(true, "authors", "Deniz I\u0307rgin", PackagingData.AuthorsDiagnostic)]
    [Arguments(false, "authors", "Deniz I\u0307rgin", PackagingData.AuthorsDiagnostic)]
    [Arguments(true, "projectUrl", "wrong", PackagingData.ProjectUrlDiagnostic)]
    [Arguments(false, "projectUrl", "wrong", PackagingData.ProjectUrlDiagnostic)]
    [Arguments(true, "projectUrl", null, PackagingData.ProjectUrlDiagnostic)]
    [Arguments(false, "projectUrl", null, PackagingData.ProjectUrlDiagnostic)]
    [Arguments(true, "projectUrl", "https://GitHub.com/opencode-dotnet/opencode-mcp", PackagingData.ProjectUrlDiagnostic)]
    [Arguments(false, "projectUrl", "https://GitHub.com/opencode-dotnet/opencode-mcp", PackagingData.ProjectUrlDiagnostic)]
    [Arguments(true, "projectUrl", "https://github.com/opencode-dotnet/opencode-mcp/", PackagingData.ProjectUrlDiagnostic)]
    [Arguments(false, "projectUrl", "https://github.com/opencode-dotnet/opencode-mcp/", PackagingData.ProjectUrlDiagnostic)]
    [Arguments(true, "projectUrl", "https://github.com/opencode-dotnet/opencode-mcp.git", PackagingData.ProjectUrlDiagnostic)]
    [Arguments(false, "projectUrl", "https://github.com/opencode-dotnet/opencode-mcp.git", PackagingData.ProjectUrlDiagnostic)]
    public async Task RootDiagnostics_And_ChildDiagnostics_Should_Pin_Authors_And_Project_Url(bool root, string field, string? value, string diagnostic, CancellationToken token)
    {
        var builder = root ? await PackageArchiveBuilder.CreateRootAsync(token) : await PackageArchiveBuilder.CreateAsync(token);
        var element = builder.Metadata.Element(builder.Metadata.Name.Namespace + field);
        if (value is null)
        {
            element?.Remove();
        }
        else
        {
            builder.Metadata.SetElementValue(builder.Metadata.Name.Namespace + field, value);
        }
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        var diagnostics = root ? McpPackagePolicy.RootDiagnostics(package, builder.Identity, builder.Receipt.SourceCommit)
            : McpPackagePolicy.ChildDiagnostics(package, builder.Identity, builder.Receipt with { Payload = builder.Inventory() });
        await Assert.That(diagnostics).IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
    }
}
