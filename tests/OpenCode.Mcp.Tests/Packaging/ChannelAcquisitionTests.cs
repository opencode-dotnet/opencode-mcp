using OpenCode.Mcp.Tests.Packaging.Support;
using OpenCode.Mcp.Tests.Support;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Packaging;
using Testably.Abstractions;

namespace OpenCode.Mcp.Tests.Packaging;

[Category("Package")]
[NotInParallel("processes")]
[ClassDataSource<PrivateSdkCopy>(Shared = SharedType.PerTestSession)]
public sealed class ChannelAcquisitionTests(PrivateSdkCopy sdk)
{
    private readonly RealFileSystem _files = new();
    private readonly BootstrapAssertions _bootstrap = new();

    [Test]
    [Arguments("dnx", "stdio")]
    [Arguments("dnx", "http")]
    [Arguments("global", "stdio")]
    [Arguments("global", "http")]
    public async Task Acquisition_Should_Start_The_Selected_Remote_Native_Payload(string route, string transport, CancellationToken token)
    {
        string[] names = ["OPENCODE_MCP_TEST_CHANNEL", "OPENCODE_MCP_TEST_CHANNEL_CONFIG", "OPENCODE_MCP_TEST_FEED", "OPENCODE_MCP_TEST_RID"];
        var inputs = names.ToDictionary(static name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        var channel = new PackageChannelFixture(_files, inputs);
        var configuration = await channel.ResolveAsync(token);
        await using var owner = new OwnedToolAcquisition(_files, sdk: sdk);
        var rid = await PrepareAsync(owner, channel, configuration, token);
        var arguments = transport == "stdio" ? new[] { "--transport", "stdio" }
            : ["--transport", "http", "--urls", "http://127.0.0.1:0", "--environment", "Development"];
        var launch = route == "dnx" ? owner.CreateDnxLaunch(_bootstrap.ExpectedVersion, arguments)
            : await owner.InstallGlobalAsync(_bootstrap.ExpectedVersion, token);
        if (route == "global" && transport == "http")
        {
            var entry = await Assert.That(owner.InstalledExecutable).IsNotNull();
            launch = launch with { FileName = entry, VerbatimCommandLine = null, Arguments = arguments };
        }
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        guard.CancelAfter(TimeSpan.FromSeconds(45)); // Cold acquisition, protocol exchanges and EOF completion.
        await owner.StartAsync(launch, guard.Token);
        var assertions = new PackagedStartupAssertions(_files, channel.ReferenceFeed.Resolve());
        if (transport == "stdio")
        {
            await assertions.AssertStdioAsync(owner, rid, guard.Token);
        }
        else
        {
            await assertions.AssertHttpAsync(owner, rid, guard.Token, token);
        }
        await owner.DisposeAsync();
        await Assert.That(_files.Directory.Exists(owner.OwnedRoot)).IsFalse();
    }

    private async Task<string> PrepareAsync(OwnedToolAcquisition owner, PackageChannelFixture channel, string configuration, CancellationToken token)
    {
        var feed = channel.ReferenceFeed;
        var manifest = await new NativeManifestStore(_files).ReadAsync(_files.Path.Combine(feed.RepositoryRoot, "packaging", "native-manifest.json"), token);
        var identity = new PackageIdentity("OpenCodeDotNet.Mcp", _bootstrap.ExpectedVersion, "opencode-mcp", [.. manifest.Runtimes.Select(static runtime => runtime.Rid)]);
        await Assert.That(await new McpPackageFeed(_files).ValidateAsync(feed.Resolve(), manifest, identity, token)).IsEmpty();
        var host = new NativeHostReader().Read();
        var rid = PackageFeedFixture.ResolveRid(Environment.GetEnvironmentVariable("OPENCODE_MCP_TEST_RID"), manifest,
            host.Os + "-" + host.OsArchitecture, host.Os + "-" + host.ProcessArchitecture);
        var receipt = await new NativeEvidenceStore(_files).ReadAsync<NativeReceipt>(_files.Path.Combine(feed.Resolve(), "evidence", rid, "receipt.json"), token);
        await owner.PrepareRemoteAsync(feed.RepositoryRoot, configuration, receipt.SdkVersion, token);
        PackageFeedFixture.ResolveRid(rid, manifest, host.Os + "-" + host.OsArchitecture, await owner.ReadSdkRidAsync(token));
        await Assert.That(owner.SdkVersion).IsEqualTo(receipt.SdkVersion);
        return rid;
    }
}
