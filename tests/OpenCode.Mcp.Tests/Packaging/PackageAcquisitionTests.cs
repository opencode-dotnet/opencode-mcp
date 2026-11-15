using System.Globalization;
using OpenCode.Mcp.Tests.Packaging.Support;
using OpenCode.Mcp.Tests.Support;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using OpenCode.Mcp.Tools.Packaging;
using Testably.Abstractions;

namespace OpenCode.Mcp.Tests.Packaging;

[Category("Package")]
[NotInParallel("processes")]
[ClassDataSource<PrivateSdkCopy>(Shared = SharedType.PerTestSession)]
public sealed class PackageAcquisitionTests(PrivateSdkCopy sdk) : IAsyncDisposable
{
    private readonly RealFileSystem _files = new();
    private readonly BootstrapAssertions _bootstrap = new();
    private PackageFeedFixture? _feed;
    private TestContext? _context;
    private PackageFeedFixture Feed => _feed ??= new PackageFeedFixture(_files, Environment.GetEnvironmentVariable("OPENCODE_MCP_TEST_FEED"));

    [Test]
    public async Task CreateDnxLaunch_Should_Start_The_Selected_Native_Package_From_Cold_State(CancellationToken token)
    {
        await using var owner = new OwnedToolAcquisition(_files, sdk: sdk);
        var rid = await PrepareAsync(owner, Feed.Resolve(), token);
        var launch = owner.CreateDnxLaunch(_bootstrap.ExpectedVersion);
        await RecordEnvironmentAsync(owner, launch);
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        guard.CancelAfter(TimeSpan.FromSeconds(45)); // Cold acquisition, protocol exchanges and EOF completion.
        await owner.StartAsync(launch, guard.Token);
        await new PackagedStartupAssertions(_files, Feed.Resolve()).AssertStdioAsync(owner, rid, guard.Token);
        await owner.DisposeAsync();
        await Assert.That(_files.Directory.Exists(owner.OwnedRoot)).IsFalse();
    }

    [Test]
    public async Task InstallAsync_Should_Start_The_Installed_Native_Shim(CancellationToken token)
    {
        await using var owner = new OwnedToolAcquisition(_files, sdk: sdk);
        var rid = await PrepareAsync(owner, Feed.Resolve(), token);
        var launch = await owner.InstallAsync(_bootstrap.ExpectedVersion, token);
        await RecordEnvironmentAsync(owner, launch);
        var shim = _files.Path.Combine(owner.ToolPath, OperatingSystem.IsWindows() ? "opencode-mcp.cmd" : "opencode-mcp");
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(_files.FileInfo.New(shim).LinkTarget).IsNull();
            await Assert.That(await _files.File.ReadAllTextAsync(shim, token)).Contains("opencode-mcp.exe");
        }
        else
        {
            await Assert.That(_files.FileInfo.New(shim).LinkTarget).IsNotNull();
            await Assert.That(_files.File.ResolveLinkTarget(shim, returnFinalTarget: true)?.FullName).IsEqualTo(owner.InstalledExecutable);
        }
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        guard.CancelAfter(TimeSpan.FromSeconds(45)); // Shim bootstrap and EOF completion.
        await owner.StartAsync(launch, guard.Token);
        await new PackagedStartupAssertions(_files, Feed.Resolve()).AssertStdioAsync(owner, rid, guard.Token);
        await owner.DisposeAsync();
        await Assert.That(_files.Directory.Exists(owner.OwnedRoot)).IsFalse();
    }

    [Test]
    public async Task InstallAsync_Should_Start_The_Installed_Native_Entry_In_Http_Mode(CancellationToken token)
    {
        await using var owner = new OwnedToolAcquisition(_files, sdk: sdk);
        var rid = await PrepareAsync(owner, Feed.Resolve(), token);
        var installed = await owner.InstallAsync(_bootstrap.ExpectedVersion, token);
        var entry = await Assert.That(owner.InstalledExecutable).IsNotNull();
        var launch = installed with
        {
            FileName = entry,
            VerbatimCommandLine = null,
            Arguments = ["--transport", "http", "--urls", "http://127.0.0.1:0", "--environment", "Development"]
        };
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        guard.CancelAfter(TimeSpan.FromSeconds(45)); // Listening announcement, health and MCP bootstrap.
        await owner.StartAsync(launch, guard.Token);
        await new PackagedStartupAssertions(_files, Feed.Resolve()).AssertHttpAsync(owner, rid, guard.Token, token);
    }

    [Test]
    [Arguments("dnx")]
    [Arguments("tool-path")]
    public async Task Acquisition_Should_Refuse_The_Missing_Host_Implementation_Package(string route, CancellationToken token)
    {
        await using var owner = new OwnedToolAcquisition(_files, sdk: sdk);
        var copy = await Feed.CopyAsync(token);
        var rid = await PrepareAsync(owner, copy, token); // Validate the complete original before changing only the owned copy.
        _files.File.Delete(_files.Path.Combine(copy, "OpenCodeDotNet.Mcp." + rid + "." + _bootstrap.ExpectedVersion + ".nupkg"));
        NativeProcessResult result;
        IReadOnlyList<string> commands;
        if (route == "dnx")
        {
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
            guard.CancelAfter(TimeSpan.FromSeconds(45)); // Refusal exit and reader completion, not the refusal oracle.
            await owner.StartAsync(owner.CreateDnxLaunch(_bootstrap.ExpectedVersion), guard.Token);
            await owner.Process.CloseInputAsync(); // A wrongly launched server reaches EOF instead of hanging the refusal test.
            var exit = await owner.Process.WaitForExitAsync(guard.Token);
            result = new NativeProcessResult(exit, string.Join('\n', owner.Process.Transcript.ServerLines), string.Join('\n', owner.Process.CompleteStandardError));
            commands = owner.ObserveInstalledCommands();
            await Assert.That(owner.Process.ObserveOwnedExecutables()).IsEmpty();
        }
        else
        {
            await Assert.That(async () => { await owner.InstallAsync(_bootstrap.ExpectedVersion, token); }).ThrowsExactly<InvalidOperationException>();
            result = await Assert.That(owner.InstallResult).IsNotNull();
            commands = await Assert.That<IReadOnlyList<string>>(owner.InstalledCommands).IsNotNull();
        }
        await OwnedToolAcquisition.AssertMissingImplementationAsync(result, rid, _bootstrap.ExpectedVersion, commands);
        await LogAsync(string.Create(CultureInfo.InvariantCulture, $"{route} missing-child exit={result.ExitCode}\n{result.Output}"));
        await owner.DisposeAsync();
        await Assert.That(_files.Directory.Exists(owner.OwnedRoot)).IsFalse();
    }

    private async Task<string> PrepareAsync(OwnedToolAcquisition owner, string feed, CancellationToken token)
    {
        var manifest = await new NativeManifestStore(_files).ReadAsync(_files.Path.Combine(Feed.RepositoryRoot, "packaging", "native-manifest.json"), token);
        var identity = new PackageIdentity("OpenCodeDotNet.Mcp", _bootstrap.ExpectedVersion, "opencode-mcp", [.. manifest.Runtimes.Select(static runtime => runtime.Rid)]);
        await Assert.That(await new McpPackageFeed(_files).ValidateAsync(Feed.Resolve(), manifest, identity, token)).IsEmpty();
        var host = new NativeHostReader().Read();
        var rid = PackageFeedFixture.ResolveRid(Environment.GetEnvironmentVariable("OPENCODE_MCP_TEST_RID"), manifest,
            host.Os + "-" + host.OsArchitecture, host.Os + "-" + host.ProcessArchitecture);
        var receipt = await new NativeEvidenceStore(_files).ReadAsync<NativeReceipt>(_files.Path.Combine(Feed.Resolve(), "evidence", rid, "receipt.json"), token);
        await owner.PrepareAsync(Feed.RepositoryRoot, feed, receipt.SdkVersion, token);
        PackageFeedFixture.ResolveRid(rid, manifest, host.Os + "-" + host.OsArchitecture, await owner.ReadSdkRidAsync(token));
        await Assert.That(owner.SdkVersion).IsEqualTo(receipt.SdkVersion);
        await LogAsync($"Private SDK: {sdk.Root}; SDK {owner.SdkVersion}; Core {sdk.RuntimeVersion}; no ASP.NET shared runtime.");
        await LogAsync("Selected SDK: " + owner.SdkDirectory + "\n.version:\n" + await _files.File.ReadAllTextAsync(_files.Path.Combine(owner.SdkDirectory, ".version"), token));
        return rid;
    }

    private async Task RecordEnvironmentAsync(OwnedToolAcquisition owner, ProcessLaunch launch)
    {
        await LogAsync("Owned root: " + owner.OwnedRoot + "\n" + string.Join('\n', owner.Process.CreateEnvironment(launch).OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Key + "=" + pair.Value)));
        await LogAsync("Layout:\n" + string.Join('\n', _files.Directory.EnumerateFileSystemEntries(owner.OwnedRoot).Select(_files.Path.GetFileName).Order(StringComparer.Ordinal)));
        if (owner.InstallResult is { } install)
        {
            await LogAsync(string.Create(CultureInfo.InvariantCulture, $"Install exit={install.ExitCode}\n{install.Output}\nInstalled files:\n")
                + string.Join('\n', _files.Directory.EnumerateFiles(owner.ToolPath, "*", SearchOption.AllDirectories)));
        }
    }

    private async Task LogAsync(string message)
    {
        var context = _context ??= await Assert.That(TestContext.Current).IsNotNull();
        context.Output.WriteLine(message);
    }

    /// <summary>Releases this test's owned feed copy without touching the supplied feed.</summary>
    public ValueTask DisposeAsync() => _feed?.DisposeAsync() ?? ValueTask.CompletedTask;
}
