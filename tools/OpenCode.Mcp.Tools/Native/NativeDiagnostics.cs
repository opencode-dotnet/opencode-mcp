namespace OpenCode.Mcp.Tools.Native;

internal static class NativeDiagnostics
{
    public static void Refuse(IReadOnlyList<string> diagnostics)
    {
        if (diagnostics.Count != 0)
        {
            throw new InvalidOperationException(string.Join('\n', diagnostics));
        }
    }
}
