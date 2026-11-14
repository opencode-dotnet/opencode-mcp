using System.Diagnostics;
using System.IO.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Client;
using OpenCode.Mcp.Tools.Native;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Support;

internal sealed class PackagedStartupAssertions(IFileSystem files, string referenceFeed)
{
    private readonly BootstrapAssertions _bootstrap = new();

    public async Task AssertStdioAsync(OwnedToolAcquisition owner, string rid, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var client = await owner.Process.ConnectStdioAsync(token);
        await _bootstrap.AssertBootstrapAsync(client, token);
        var entry = owner.InstalledExecutable ?? await owner.FindNativeEntryAsync(files.Path.Combine(owner.OwnedRoot, "cache", "packages"), token);
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

    public async Task AssertHttpAsync(OwnedToolAcquisition owner, string rid, CancellationToken token, CancellationToken cleanupToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var address = await owner.Process.WaitForHttpEndpointAsync(token);
        await Assert.That(address.Host).IsEqualTo("127.0.0.1");
        await Assert.That(address.Port).IsGreaterThan(0);
        var entry = owner.InstalledExecutable ?? await owner.FindNativeEntryAsync(files.Path.Combine(owner.OwnedRoot, "cache", "packages"), token);
        await AssertNativeAsync(owner, rid, entry, token);
        using (var sockets = new SocketsHttpHandler { UseProxy = false })
        using (var recorder = new RecordingHttpHandler(sockets, 1_048_576))
        using (var http = new HttpClient(recorder, disposeHandler: false))
        {
            foreach (var path in new[] { "/health", "/alive" })
            {
                using var response = await http.GetAsync(new Uri(address, path), token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(await response.Content.ReadAsStringAsync(token)).IsEqualTo("Healthy");
            }
            await using var transport = new HttpClientTransport(new HttpClientTransportOptions
            { Endpoint = new Uri(address, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http);
            await using var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = ProtocolData.Revision }, cancellationToken: token);
            await _bootstrap.AssertBootstrapAsync(client, token);
            await Assert.That(recorder.Requests.Select(static frame => frame.GetProperty("method").GetString()))
                .IsEquivalentTo(BootstrapAssertions.ModernMethods, CollectionOrdering.Matching);
            await _bootstrap.AssertResultsAsync([.. recorder.Responses.Select(static frame => frame.GetProperty("result"))]);
        }
        await owner.DisposeAsync();
        await Assert.That(owner.Process.Transcript.ServerByteCount).IsEqualTo(0);
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsFalse();
        using var socket = new TcpClient();
        var failure = await Assert.That(async () => await socket.ConnectAsync(IPAddress.Loopback, address.Port, cleanupToken)).ThrowsExactly<SocketException>();
        var refused = await Assert.That(failure).IsNotNull();
        await Assert.That(refused.SocketErrorCode).IsEqualTo(SocketError.ConnectionRefused);
    }

    public async Task<string> AssertNativeAsync(OwnedToolAcquisition owner, string rid, string entry, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(entry);
        var directory = files.Path.GetDirectoryName(entry) ?? throw new InvalidOperationException();
        var physicalEntry = files.Path.Combine(new PhysicalPathResolver(files).Resolve(directory), files.Path.GetFileName(entry));
        await Assert.That(owner.Process.ObserveExpectedExecutable(physicalEntry)).IsEqualTo(physicalEntry);
        await Assert.That(entry.Replace('\\', '/').ToLowerInvariant()).Contains("opencodedotnet.mcp." + rid + "/" + _bootstrap.ExpectedVersion + "/");
        var expected = await new NativeEvidenceStore(files).ReadAsync<PayloadFile[]>(files.Path.Combine(referenceFeed, "evidence", rid, "payload-manifest.json"), token);
        var observed = await AssertPayloadAsync(directory, rid, expected, token);
        var context = await Assert.That(TestContext.Current).IsNotNull();
        context.Output.WriteLine("Running native entry: " + physicalEntry + "\nPayload: " + string.Join('\n', observed.Select(static file => file.Path + " " + file.Sha256)));
        return physicalEntry;
    }

    public async Task<IReadOnlyList<PayloadFile>> AssertPayloadAsync(string directory, string rid, IReadOnlyList<PayloadFile> expected, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var inventory = await new PayloadInventory(files, new FileUnixModes(files), new NativeRid(rid)).CaptureAsync(directory, token);
        var observed = inventory.Where(static file => file.Path != "DotnetToolSettings.xml").ToArray();
        await Assert.That(observed.Select(static file => file.Path)).IsEquivalentTo(expected.Select(static file => file.Path), CollectionOrdering.Matching);
        await Assert.That(observed.Select(static file => file.Sha256)).IsEquivalentTo(expected.Select(static file => file.Sha256), CollectionOrdering.Matching);
        return observed;
    }
}
