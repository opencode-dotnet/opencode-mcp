using System.Text.Json.Nodes;
using OpenCode.Mcp.Tools.Packaging;
using OpenCode.Mcp.Tools.Tests.Packaging.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Packaging;

public sealed class McpManifestPolicyTests
{
    [Test]
    [Arguments(1)]
    [Arguments(100)]
    public async Task Diagnostics_Should_Accept_The_Description_And_Size_Boundaries(int length, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        builder.Manifest["description"] = new string('a', length);
        builder.ManifestSize = 19999;
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(package.ManifestSize).IsEqualTo(19999);
        await Assert.That(McpManifestPolicy.Diagnostics(package.Manifest, package.ManifestSize, builder.Identity)).IsEmpty();
    }

    [Test]
    [Arguments("name", "Manifest name must be io.github.opencode-dotnet/opencode-mcp.")]
    [Arguments("empty description", "Manifest description must have 1 to 100 characters.")]
    [Arguments("long description", "Manifest description must have 1 to 100 characters.")]
    [Arguments("version", "Manifest version must be 0.1.0-preview.1.")]
    [Arguments("no packages", "Manifest must contain exactly one package.")]
    [Arguments("two packages", "Manifest must contain exactly one package.")]
    [Arguments("size", "Manifest UTF-8 size must be under 20000 bytes.")]
    public async Task Diagnostics_Should_Refuse_Listing_Drift(string fault, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        switch (fault)
        {
            case "empty description": builder.Manifest["description"] = ""; break;
            case "long description": builder.Manifest["description"] = new string('a', 101); break;
            case "no packages": builder.Manifest["packages"] = new JsonArray(); break;
            case "two packages": builder.Manifest["packages"]?.AsArray().Add(builder.Manifest["packages"]?[0]?.DeepClone()); break;
            case "size": builder.ManifestSize = 20000; break;
            default: builder.Manifest[fault] = "wrong"; break;
        }
        await AssertRefusalAsync(builder, diagnostic, token);
    }

    [Test]
    [Arguments("version", "Manifest package version must be 0.1.0-preview.1.")]
    [Arguments("registryType", "Manifest registryType must be nuget.")]
    [Arguments("identifier", "Manifest identifier must be OpenCodeDotNet.Mcp.")]
    [Arguments("registryBaseUrl", "Manifest registryBaseUrl must be https://api.nuget.org/v3/index.json.")]
    [Arguments("runtimeHint", "Manifest runtimeHint must be dnx.")]
    [Arguments("transport", "Manifest transport.type must be stdio.")]
    public async Task Diagnostics_Should_Refuse_Package_Drift(string field, string diagnostic, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var package = builder.Manifest["packages"]?[0] ?? throw new InvalidOperationException();
        if (field == "transport")
        {
            var transport = package[field] ?? throw new InvalidOperationException();
            transport["type"] = "http";
        }
        else
        {
            package[field] = "wrong";
        }
        await AssertRefusalAsync(builder, diagnostic, token);
    }

    [Test]
    [Arguments("runtimeArguments", "type")]
    [Arguments("runtimeArguments", "name")]
    [Arguments("runtimeArguments", "value")]
    [Arguments("runtimeArguments", "second")]
    [Arguments("packageArguments", "type")]
    [Arguments("packageArguments", "name")]
    [Arguments("packageArguments", "value")]
    [Arguments("packageArguments", "second")]
    public async Task Diagnostics_Should_Require_Exact_Arguments(string name, string fault, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var arguments = builder.Manifest["packages"]?[0]?[name]?.AsArray() ?? throw new InvalidOperationException();
        var argument = arguments[0] ?? throw new InvalidOperationException();
        if (fault == "second")
        {
            arguments.Add(argument.DeepClone());
        }
        else
        {
            argument[fault] = "wrong";
        }
        var expected = name == "runtimeArguments" ? "Manifest runtimeArguments must be exactly named -v=minimal."
            : "Manifest packageArguments must be exactly named --transport=stdio.";
        await AssertRefusalAsync(builder, expected, token);
    }

    private static async Task AssertRefusalAsync(PackageArchiveBuilder builder, string diagnostic, CancellationToken token)
    {
        await builder.WriteAsync(token);
        var package = await new McpPackageInspector(builder.Files).InspectAsync(builder.Path, token);
        await Assert.That(McpManifestPolicy.Diagnostics(package.Manifest, package.ManifestSize, builder.Identity))
            .IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
        await Assert.That(McpPackagePolicy.ChildDiagnostics(package, builder.Identity, builder.Receipt with { Payload = builder.Inventory() }))
            .IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
    }
}
