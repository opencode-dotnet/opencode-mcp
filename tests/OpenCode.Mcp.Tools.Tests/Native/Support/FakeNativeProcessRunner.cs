using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal sealed class FakeNativeProcessRunner : INativeProcessRunner
{
    private readonly List<NativeProcessRequest> _calls = [];
    public IReadOnlyList<NativeProcessRequest> Calls => _calls.AsReadOnly();
    public Func<NativeProcessRequest, CancellationToken, Task<NativeProcessResult>> Response { get; set; } =
        static (_, _) => Task.FromResult(new NativeProcessResult(0, "", ""));

    public Task<NativeProcessResult> RunAsync(NativeProcessRequest request, CancellationToken token)
    {
        _calls.Add(request);
        return Response(request, token);
    }
}
