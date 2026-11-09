using System.IO.Abstractions;
using NSubstitute;
using OpenCode.Mcp.Tools.Packaging;
using OpenCode.Mcp.Tools.Tests.Packaging.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Packaging;

public sealed class McpPackageInspectorTests
{
    [Test]
    [Arguments("extra.nuspec", "nuspec")]
    [Arguments("another/DotnetToolSettings.xml", "DotnetToolSettings.xml")]
    [Arguments("another/server.json", "server.json")]
    public async Task InspectAsync_Should_Refuse_Metadata_In_Two_Directories(string path, string name, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        builder.AdditionalFiles.Add(path, name switch
        {
            "nuspec" => builder.Nuspec.ToString(),
            "DotnetToolSettings.xml" => builder.Settings.ToString(),
            _ => builder.Manifest.ToJsonString(),
        });
        await builder.WriteAsync(token);
        await Assert.That(async () => { await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token); })
            .ThrowsExactly<InvalidOperationException>().WithMessage("Package: expected exactly one " + name + ".", StringComparison.Ordinal);
    }

    [Test]
    [Arguments("missing", "OpenCodeDotNet.Mcp.win-x64.nuspec", "Package: expected exactly one nuspec.")]
    [Arguments("missing", "tools/any/win-x64/DotnetToolSettings.xml", "Package: expected exactly one DotnetToolSettings.xml.")]
    [Arguments("missing", ".mcp/server.json", "Package: expected exactly one server.json.")]
    [Arguments("duplicate", "OpenCodeDotNet.Mcp.win-x64.nuspec", "Duplicate package path: OpenCodeDotNet.Mcp.win-x64.nuspec")]
    [Arguments("duplicate", "tools/any/win-x64/DotnetToolSettings.xml", "Duplicate package path: tools/any/win-x64/DotnetToolSettings.xml")]
    [Arguments("duplicate", ".mcp/server.json", "Duplicate package path: .mcp/server.json")]
    public async Task InspectAsync_Should_Refuse_Ambiguous_Or_Missing_Metadata(string fault, string path, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        if (fault == "missing")
        {
            builder.Omitted.Add(path);
        }
        else
        {
            builder.Duplicate = path;
        }
        await builder.WriteAsync(token);
        await Assert.That(async () => { await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token); })
            .ThrowsExactly<InvalidOperationException>().WithMessage(diagnostic, StringComparison.Ordinal);
    }

    [Test]
    public async Task InspectAsync_Should_Refuse_An_Unreadable_Archive(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        await builder.WriteAsync(token);
        await builder.Files.File.WriteAllTextAsync(builder.Path, "not a ZIP archive", token);
        await Assert.That(async () => { await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token); }).ThrowsExactly<InvalidOperationException>()
            .WithMessage("Package: invalid archive " + builder.Path + ".", StringComparison.Ordinal);
    }

    [Test]
    public async Task InspectAsync_Should_Preserve_The_Abstracted_Read_Failure(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        await builder.WriteAsync(token);
        var failure = new IOException("fixture read failure");
        var file = Substitute.For<IFile>();
        file.OpenRead(Arg.Any<string>()).Returns(call => call.Arg<string>() == builder.Path ? throw failure : builder.Files.File.OpenRead(call.Arg<string>()));
        var files = Substitute.For<IFileSystem>();
        files.File.Returns(file);
        files.Directory.Returns(builder.Files.Directory);
        files.Path.Returns(builder.Files.Path);
        var observed = await Assert.That(async () => { await new McpPackageInspector(files).InspectAsync(builder.Path, token); }).ThrowsExactly<IOException>();
        await Assert.That(observed).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task Inspect_Should_Read_The_Sdk_Metadata_And_Whole_Payload(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        builder.SettingsPath = "tools/fixture/win-x64/DotnetToolSettings.xml";
        await builder.WriteAsync(token);
        var result = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(result.Id).IsEqualTo("OpenCodeDotNet.Mcp.win-x64");
        await Assert.That(result.Version).IsEqualTo("0.1.0-preview.1");
        await Assert.That(result.PackageTypes).IsEquivalentTo(["DotnetToolRidPackage"], CollectionOrdering.Matching);
        await Assert.That(result.SettingsPath).IsEqualTo("tools/fixture/win-x64/DotnetToolSettings.xml");
        await Assert.That(result.CommandName).IsEqualTo("opencode-mcp");
        await Assert.That(result.Runner).IsEqualTo("executable");
        await Assert.That(result.EntryPoint).IsEqualTo("opencode-mcp.exe");
        await Assert.That(result.RepositoryCommit).IsEqualTo("3aea200c08618f7d12dd5dd336f584ec3ebc5684");
        await Assert.That(result.ManifestPath).IsEqualTo(".mcp/server.json");
        await Assert.That(result.Manifest.GetProperty("name").GetString()).IsEqualTo("io.github.opencode-dotnet/opencode-mcp");
        await Assert.That(result.ManifestSize).IsEqualTo(System.Text.Encoding.UTF8.GetByteCount(builder.Manifest.ToJsonString()));
        await Assert.That(result.Payload).IsEquivalentTo(builder.Inventory(), CollectionOrdering.Matching);
        await Assert.That(result.Paths).IsEquivalentTo([".mcp/server.json", "LICENSE", "OpenCodeDotNet.Mcp.win-x64.nuspec", "README.md", "THIRD-PARTY-NOTICES.md",
            "tools/fixture/win-x64/DotnetToolSettings.xml", "tools/fixture/win-x64/LICENSE", "tools/fixture/win-x64/THIRD-PARTY-NOTICES.md",
            "tools/fixture/win-x64/opencode-mcp.exe", "tools/fixture/win-x64/payload.json"], CollectionOrdering.Matching);
    }
}
