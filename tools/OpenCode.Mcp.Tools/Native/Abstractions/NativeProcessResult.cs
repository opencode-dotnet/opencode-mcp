namespace OpenCode.Mcp.Tools.Native.Abstractions;

internal sealed record NativeProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => StandardOutput + "\n" + StandardError;
}
