namespace OpenCode.Mcp.Tools.Native;

internal sealed record NativeTestRun(int ExitCode, int Passed, int Failed, int Skipped)
{
    public bool Succeeded => ExitCode == 0 && Passed > 0 && Failed == 0 && Skipped == 0;
}
