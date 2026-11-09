using System.Text.Json;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using Testably.Abstractions.Testing;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal sealed class LinuxProofScenario
{
    private readonly NativeBuildScenario _build = new();
    public MockFileSystem Files => _build.Files;
    public FakeNativeProcessRunner Runner => _build.Runner;
    public FakeUnixFileModes Modes => _build.Modes;
    public NativeProcessResult Lookup { get; set; } = new(127, "", "");
    public NativeProcessResult Scan { get; set; } = new(0, "", "");
    public bool OmitTestReport { get; set; }
    public string Output => _build.Output;

    public async Task<NativeBuildRequest> PrepareAsync(CancellationToken token)
    {
        var request = await _build.PrepareAsync(token);
        var manifestPath = await _build.PrepareUnixPayloadAsync(request, changedMode: false, token);
        var manifest = await new NativeManifestStore(Files).ReadAsync(manifestPath, token);
        var respond = Runner.Response;
        Runner.Response = (call, cancellation) =>
        {
            if (call.FileName == "docker")
            {
                return Task.FromResult(Docker(call));
            }
            if (call.FileName == "objdump")
            {
                return Task.FromResult(new NativeProcessResult(0, call.Arguments[0] == "--version" ? "GNU objdump fixture"
                    : "0000000000000000      DF *UND*\t0000000000000000 (GLIBC_2.9)  pipe2\n0000000000000000      DF *UND*\t0000000000000000 (GLIBC_2.10) accept4\n0000000000000000      DF *UND*\t0000000000000000 (GLIBC_2.2.5) __libc_start_main", ""));
            }
            return OmitTestReport && call.Arguments[0] == "test" ? Task.FromResult(new NativeProcessResult(0, "", "")) : respond(call, cancellation);
        };
        var store = new NativeEvidenceStore(Files);
        var receiptPath = Files.Path.Combine(Output, "receipt.json");
        var receipt = await store.ReadAsync<NativeReceipt>(receiptPath, token);
        var source = await new NativeSourceReader(Files, new NativeProcesses(Runner, request.Root)).ReadAsync(request.Root, token);
        await store.WriteAsync(receiptPath, receipt with { SourceCommit = source.Commit, CommonInputsSha256 = source.InputsSha256, SdkVersion = "10.0.403" }, token);
        return request with { Rid = "linux-x64", Host = request.Host with { Os = "linux" }, Manifest = manifest };
    }

    private NativeProcessResult Docker(NativeProcessRequest request)
    {
        var arguments = request.Arguments;
        if (arguments[0] == "image")
        {
            return new NativeProcessResult(0, JsonSerializer.Serialize(new { Id = "sha256:" + new string('c', 64), Os = "linux", Architecture = "amd64" }), "");
        }
        if (arguments[0] == "run")
        {
            return arguments[^1] == "command -v dotnet" ? Lookup : Scan;
        }
        if (arguments[0] == "container" && arguments[1] == "inspect")
        {
            return new NativeProcessResult(1, "[]", "Error: No such container: " + arguments[2]);
        }
        return new NativeProcessResult(0, "", "");
    }
}
