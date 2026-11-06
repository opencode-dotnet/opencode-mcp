namespace OpenCode.Mcp.Tools.Native.Abstractions;

internal interface INativeProcessRunner
{
    public Task<NativeProcessResult> RunAsync(NativeProcessRequest request, CancellationToken token);
}
