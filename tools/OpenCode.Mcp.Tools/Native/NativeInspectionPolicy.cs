namespace OpenCode.Mcp.Tools.Native;

internal static class NativeInspectionPolicy
{
    public static IReadOnlyList<string> Diagnostics(IReadOnlyList<string> forbiddenFiles, string rid, string exports)
    {
        var diagnostics = forbiddenFiles.Order(StringComparer.Ordinal).Select(static path => "Forbidden runtime file: " + path).ToList();
        var symbol = new NativeRid(rid).IsMacOS ? "_DotNetRuntimeDebugHeader" : "DotNetRuntimeDebugHeader";
        if (!exports.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(symbol, StringComparer.Ordinal))
        {
            diagnostics.Add("Missing native export: " + symbol);
        }
        return diagnostics.AsReadOnly();
    }
}
