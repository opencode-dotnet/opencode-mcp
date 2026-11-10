using System.Reflection;
using OpenCode.Mcp.Tests.Packaging.Support;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Packaging;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Packaging;

[Category("Package")]
public sealed class PackageContentsTests
{
    private readonly RealFileSystem _files = new();
    private const string Instruction = "Build native-package-feed with pack-root after the native pass and set OPENCODE_MCP_TEST_FEED to its directory.";

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("artifacts/nonexistent-package-test-feed")]
    public async Task Resolve_Should_Refuse_An_Unconfigured_Or_Missing_Feed(string? input)
    {
        await using var fixture = new PackageFeedFixture(_files, input);
        var expected = string.IsNullOrWhiteSpace(input) ? "OPENCODE_MCP_TEST_FEED is absent or blank. " + Instruction
            : "OPENCODE_MCP_TEST_FEED directory does not exist: " + _files.Path.GetFullPath(input, fixture.RepositoryRoot) + ". " + Instruction;
        await Assert.That(fixture.Resolve).ThrowsExactly<InvalidOperationException>().WithMessage(expected, StringComparison.Ordinal);
    }

    [Test]
    public async Task ValidateAsync_Should_Accept_The_Real_Feed(CancellationToken token)
    {
        await using var fixture = ConfiguredFeed();
        var manifest = await ManifestAsync(fixture, token);
        await Assert.That(await new McpPackageFeed(_files).ValidateAsync(fixture.Resolve(), manifest, Identity(manifest), token)).IsEmpty();
    }

    [Test]
    public async Task Packages_Should_Match_The_Product_Contract(CancellationToken token)
    {
        await using var fixture = ConfiguredFeed();
        var manifest = await ManifestAsync(fixture, token);
        var identity = Identity(manifest);
        var packages = new List<PackageInspection>();
        foreach (var id in new[] { identity.Id }.Concat(manifest.Runtimes.Select(runtime => identity.Id + "." + runtime.Rid)))
        {
            packages.Add(await new McpPackageInspector(_files).InspectAsync(_files.Path.Combine(fixture.Resolve(), id + "." + identity.Version + ".nupkg"), token));
        }
        await Assert.That(packages.Select(static package => package.Id))
            .IsEquivalentTo(new[] { identity.Id }.Concat(manifest.Runtimes.Select(runtime => identity.Id + "." + runtime.Rid)), CollectionOrdering.Matching);
        await Assert.That(packages.Select(static package => package.Version).Distinct(StringComparer.Ordinal)).IsEquivalentTo([BuildVersion()], CollectionOrdering.Matching);
        var root = packages[0];
        await Assert.That(root.PackageTypes).IsEquivalentTo(["DotnetTool", "McpServer"], CollectionOrdering.Matching);
        await Assert.That(root.CommandName).IsEqualTo("opencode-mcp");
        await Assert.That(root.Runner).IsNull();
        await Assert.That(root.EntryPoint).IsNull();
        await Assert.That(root.Payload).IsEmpty();
        await Assert.That(root.RuntimeIdentifierPackages).IsEquivalentTo(manifest.Runtimes.Select(runtime => new PackageRidMapping(runtime.Rid, identity.Id + "." + runtime.Rid)), CollectionOrdering.Matching);
        for (var index = 0; index < manifest.Runtimes.Count; index++)
        {
            var child = packages[index + 1];
            var rid = manifest.Runtimes[index].Rid;
            await Assert.That(child.PackageTypes).IsEquivalentTo(["DotnetToolRidPackage"], CollectionOrdering.Matching);
            await Assert.That(child.CommandName).IsEqualTo("opencode-mcp");
            await Assert.That(child.Runner).IsEqualTo("executable");
            await Assert.That(child.EntryPoint).IsEqualTo(rid.StartsWith("win-", StringComparison.Ordinal) ? "opencode-mcp.exe" : "opencode-mcp");
            var payload = await new NativeEvidenceStore(_files).ReadAsync<PayloadFile[]>(_files.Path.Combine(fixture.Resolve(), "evidence", rid, "payload-manifest.json"), token);
            await Assert.That(child.Payload.Select(static file => file.Path)).IsEquivalentTo(payload.Select(static file => file.Path), CollectionOrdering.Matching);
            await Assert.That(child.Payload.Select(static file => file.Sha256)).IsEquivalentTo(payload.Select(static file => file.Sha256), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Packages_Should_Carry_Seven_Byte_Identical_Tracked_Manifests(CancellationToken token)
    {
        await using var fixture = ConfiguredFeed();
        var manifest = await ManifestAsync(fixture, token);
        var identity = Identity(manifest);
        var expected = await _files.File.ReadAllBytesAsync(_files.Path.Combine(fixture.RepositoryRoot, "src", "OpenCode.Mcp", ".mcp", "server.json"), token);
        foreach (var id in new[] { identity.Id }.Concat(manifest.Runtimes.Select(runtime => identity.Id + "." + runtime.Rid)))
        {
            var path = _files.Path.Combine(fixture.Resolve(), id + "." + identity.Version + ".nupkg");
            await Assert.That(await fixture.ManifestBytesAsync(path, token)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    [Test]
    [Arguments("type", "Root package types must be exactly DotnetTool, McpServer.")]
    [Arguments("version", "version")]
    [Arguments("missing child", "missing child")]
    [Arguments("manifest path", "Package manifest must be .mcp/server.json.\nUnexpected package entry: other/server.json")]
    [Arguments("any", "any")]
    [Arguments("missing payload", "Missing payload file: THIRD-PARTY-NOTICES.md")]
    [Arguments("changed payload", "Changed payload file: THIRD-PARTY-NOTICES.md")]
    [Arguments("source", "Mixed receipt field: SourceCommit")]
    [Arguments("inputs", "Mixed receipt field: CommonInputsSha256")]
    public async Task ValidateAsync_Should_Refuse_Each_Mutated_Real_Feed(string fault, string diagnostic, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        await using var fixture = ConfiguredFeed();
        var manifest = await ManifestAsync(fixture, token);
        var identity = Identity(manifest);
        var copy = await fixture.CopyAsync(token);
        var root = identity.Id + "." + identity.Version + ".nupkg";
        var child = identity.Id + "." + manifest.Runtimes[0].Rid + "." + identity.Version + ".nupkg";
        string[] expected;
        if (fault == "missing child")
        {
            _files.File.Delete(_files.Path.Combine(copy, child));
            expected = ["Missing feed file: " + child];
        }
        else if (fault is "source" or "inputs")
        {
            await fixture.MutateReceiptAsync(copy, manifest.Runtimes[0].Rid, fault, token);
            expected = [diagnostic];
        }
        else
        {
            var name = fault is "missing payload" or "changed payload" ? child : root;
            await fixture.MutatePackageAsync(_files.Path.Combine(copy, name), fault, token);
            if (fault == "any")
            {
                diagnostic = "Root package RID mappings must match the native manifest in order; observed: "
                    + string.Join(", ", manifest.Runtimes.Select(runtime => runtime.Rid + "=" + identity.Id + "." + runtime.Rid)) + ", any=" + identity.Id + ".any";
            }
            else if (fault == "version")
            {
                diagnostic = "Package version must be " + BuildVersion() + ".";
            }
            expected = [.. diagnostic.Split('\n').Select(message => name + ": " + message)];
        }
        await Assert.That(await new McpPackageFeed(_files).ValidateAsync(copy, manifest, identity, token)).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    private PackageFeedFixture ConfiguredFeed() => new(_files, Environment.GetEnvironmentVariable("OPENCODE_MCP_TEST_FEED"));

    private async Task<NativeManifest> ManifestAsync(PackageFeedFixture fixture, CancellationToken token) =>
        await new NativeManifestStore(_files).ReadAsync(_files.Path.Combine(fixture.RepositoryRoot, "packaging", "native-manifest.json"), token);

    private static PackageIdentity Identity(NativeManifest manifest) => new("OpenCodeDotNet.Mcp", BuildVersion(), "opencode-mcp", [.. manifest.Runtimes.Select(static runtime => runtime.Rid)]);

    private static string BuildVersion() => typeof(McpApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? throw new InvalidOperationException("Missing product informational version.");
}
