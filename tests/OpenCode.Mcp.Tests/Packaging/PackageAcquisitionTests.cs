using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Client;
using OpenCode.Mcp.Tests.Packaging.Support;
using OpenCode.Mcp.Tests.Support;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using OpenCode.Mcp.Tools.Packaging;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

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
        await AssertStdioAsync(owner, rid, guard.Token);
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
        await Assert.That(launch.ExpectedExecutable).IsEqualTo(owner.InstalledExecutable);
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
        await AssertStdioAsync(owner, rid, guard.Token);
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
        var address = await owner.Process.WaitForHttpEndpointAsync(guard.Token);
        await Assert.That(address.Host).IsEqualTo("127.0.0.1");
        await Assert.That(address.Port).IsGreaterThan(0);
        await AssertNativeAsync(owner, rid, entry, guard.Token);
        using (var sockets = new SocketsHttpHandler { UseProxy = false })
        using (var recorder = new RecordingHttpHandler(sockets, 1_048_576))
        using (var http = new HttpClient(recorder, disposeHandler: false))
        {
            foreach (var path in new[] { "/health", "/alive" })
            {
                using var response = await http.GetAsync(new Uri(address, path), guard.Token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(await response.Content.ReadAsStringAsync(guard.Token)).IsEqualTo("Healthy");
            }
            await using var transport = new HttpClientTransport(new HttpClientTransportOptions
            { Endpoint = new Uri(address, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http);
            await using var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = ProtocolData.Revision }, cancellationToken: guard.Token);
            await _bootstrap.AssertBootstrapAsync(client, guard.Token);
            await Assert.That(recorder.Requests.Select(static frame => frame.GetProperty("method").GetString()))
                .IsEquivalentTo(BootstrapAssertions.ModernMethods, CollectionOrdering.Matching);
            await _bootstrap.AssertResultsAsync([.. recorder.Responses.Select(static frame => frame.GetProperty("result"))]);
        }
        await owner.DisposeAsync();
        await Assert.That(owner.Process.Transcript.ServerByteCount).IsEqualTo(0);
        await Assert.That(_files.Directory.Exists(owner.OwnedRoot)).IsFalse();
        using var socket = new TcpClient();
        var failure = await Assert.That(async () => await socket.ConnectAsync(IPAddress.Loopback, address.Port, token)).ThrowsExactly<SocketException>();
        var refused = await Assert.That(failure).IsNotNull();
        await Assert.That(refused.SocketErrorCode).IsEqualTo(SocketError.ConnectionRefused);
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
        if (route == "dnx")
        {
            await owner.StartAsync(owner.CreateDnxLaunch(_bootstrap.ExpectedVersion), token);
            await owner.Process.CloseInputAsync(); // A wrongly launched server reaches EOF instead of hanging the refusal test.
            var exit = await owner.Process.WaitForExitAsync(token);
            result = new NativeProcessResult(exit, string.Join('\n', owner.Process.Transcript.ServerLines), string.Join('\n', owner.Process.CompleteStandardError));
        }
        else
        {
            await Assert.That(async () => { await owner.InstallAsync(_bootstrap.ExpectedVersion, token); }).ThrowsExactly<InvalidOperationException>();
            result = await Assert.That(owner.InstallResult).IsNotNull();
        }
        await owner.AssertMissingImplementationAsync(result, rid, _bootstrap.ExpectedVersion);
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
        await owner.PrepareAsync(Feed.RepositoryRoot, feed, token);
        PackageFeedFixture.ResolveRid(rid, manifest, host.Os + "-" + host.OsArchitecture, await owner.ReadSdkRidAsync(token));
        var receipt = await new NativeEvidenceStore(_files).ReadAsync<NativeReceipt>(_files.Path.Combine(Feed.Resolve(), "evidence", rid, "receipt.json"), token);
        await Assert.That(owner.SdkVersion).IsEqualTo(receipt.SdkVersion);
        await LogAsync(string.Create(CultureInfo.InvariantCulture, $"Private SDK: {sdk.Root}; {sdk.Size} bytes; copy {sdk.CopyDuration}; SDK {owner.SdkVersion}; Core {sdk.RuntimeVersion}; no ASP.NET shared runtime."));
        await LogAsync("Selected SDK: " + owner.SdkDirectory + "\n.version:\n" + await _files.File.ReadAllTextAsync(_files.Path.Combine(owner.SdkDirectory, ".version"), token));
        return rid;
    }

    private async Task AssertStdioAsync(OwnedToolAcquisition owner, string rid, CancellationToken token)
    {
        var client = await owner.Process.ConnectStdioAsync(token);
        await _bootstrap.AssertBootstrapAsync(client, token);
        var entry = owner.InstalledExecutable ?? await owner.FindNativeEntryAsync(_files.Path.Combine(owner.OwnedRoot, "cache", "packages"), token);
        var physicalEntry = await AssertNativeAsync(owner, rid, entry, token);
        using var native = Process.GetProcessById(owner.Process.ObserveOwnedExecutables().Single(pair => pair.Value == physicalEntry).Key);
        _ = native.SafeHandle;
        await Assert.That(native.HasExited).IsFalse();
        await Assert.That(owner.Process.HasExited).IsFalse();
        await owner.Process.CloseInputAsync();
        await Assert.That(await owner.Process.WaitForExitAsync(token)).IsEqualTo(0);
        await Assert.That(native.HasExited).IsTrue();
        await Assert.That(owner.Process.Transcript.Methods).IsEquivalentTo(BootstrapAssertions.ModernMethods, CollectionOrdering.Matching);
        await _bootstrap.AssertResultsAsync(owner.Process.Transcript.Results);
        await Assert.That(owner.Process.Transcript.MalformedLines).IsEmpty();
        await Assert.That(owner.Process.Transcript.ServerLines).IsNotEmpty();
        foreach (var line in owner.Process.Transcript.ServerLines)
        {
            using var message = JsonDocument.Parse(line);
            await Assert.That(message.RootElement.GetProperty("jsonrpc").GetString()).IsEqualTo("2.0");
        }
        await Assert.That(string.Join('\n', owner.Process.Transcript.ServerLines)).DoesNotContain("Skipping NuGet package signature verification.");
        await Assert.That(owner.Process.StandardError).Contains("      Application is shutting down...");
    }

    private async Task<string> AssertNativeAsync(OwnedToolAcquisition owner, string rid, string entry, CancellationToken token)
    {
        var directory = _files.Path.GetDirectoryName(entry) ?? throw new InvalidOperationException();
        var physicalEntry = _files.Path.Combine(new PhysicalPathResolver(_files).Resolve(directory), _files.Path.GetFileName(entry));
        await Assert.That(owner.Process.ObserveExpectedExecutable(physicalEntry)).IsEqualTo(physicalEntry);
        await Assert.That(entry.Replace('\\', '/').ToLowerInvariant()).Contains("opencodedotnet.mcp." + rid + "/" + _bootstrap.ExpectedVersion + "/");
        var expected = await new NativeEvidenceStore(_files).ReadAsync<PayloadFile[]>(_files.Path.Combine(Feed.Resolve(), "evidence", rid, "payload-manifest.json"), token);
        var inventory = await new PayloadInventory(_files, new FileUnixModes(_files), new NativeRid(rid)).CaptureAsync(directory, token);
        var observed = inventory.Where(static file => file.Path != "DotnetToolSettings.xml").ToArray();
        await Assert.That(observed.Select(static file => file.Path)).IsEquivalentTo(expected.Select(static file => file.Path), CollectionOrdering.Matching);
        await Assert.That(observed.Select(static file => file.Sha256)).IsEquivalentTo(expected.Select(static file => file.Sha256), CollectionOrdering.Matching);
        await LogAsync("Running native entry: " + physicalEntry + "\nPayload: " + string.Join('\n', observed.Select(static file => file.Path + " " + file.Sha256)));
        return physicalEntry;
    }

    private async Task RecordEnvironmentAsync(OwnedToolAcquisition owner, ProcessLaunch launch)
    {
        await LogAsync("Owned root: " + owner.OwnedRoot + "\n" + string.Join('\n', owner.Process.CreateEnvironment(launch).OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Key + "=" + pair.Value)));
        await LogAsync("Layout:\n" + string.Join('\n', _files.Directory.EnumerateFileSystemEntries(owner.OwnedRoot).Select(_files.Path.GetFileName).Order(StringComparer.Ordinal)));
        if (owner.InstallResult is { } install)
        {
            await LogAsync(string.Create(CultureInfo.InvariantCulture, $"With --framework exit={install.ExitCode}\n{install.Output}\nInstalled files:\n")
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
