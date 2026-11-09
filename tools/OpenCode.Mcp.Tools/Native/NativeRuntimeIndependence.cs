namespace OpenCode.Mcp.Tools.Native;

internal sealed record NativeRuntimeIndependence(string Kind, NativeTestRun? Tests = null,
    string? ImageReference = null, string? ImageId = null, bool? DotnetAbsent = null, string? Inspection = null);
