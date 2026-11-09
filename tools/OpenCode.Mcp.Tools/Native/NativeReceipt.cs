namespace OpenCode.Mcp.Tools.Native;

internal sealed record NativeReceipt
{
    public required string Rid { get; init; }
    public required string Version { get; init; }
    public required string SourceCommit { get; init; }
    public required string SourceStatus { get; init; }
    public required string SdkVersion { get; init; }
    public required string CommonInputsSha256 { get; init; }
    public required string DependenciesSha256 { get; init; }
    public required string RequestedRunner { get; init; }
    public required string ActualRunner { get; init; }
    public required string Host { get; init; }
    public required string Os { get; init; }
    public required string OsArchitecture { get; init; }
    public required string Toolchain { get; init; }
    public required string AssetsSha256 { get; init; }
    public IReadOnlyList<string> CompilerPackages { get; init; } = [];
    public string? RunnerImage { get; init; }
    public required string ProcessArchitecture { get; init; }
    public required IReadOnlyDictionary<string, string> Properties { get; init; }
    public required string PublishDirectory { get; init; }
    public required string Executable { get; init; }
    public required string ExecutableSha256 { get; init; }
    public required IReadOnlyList<string> PublishCommand { get; init; }
    public required int PublishExitCode { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required string Inspection { get; init; }
    public required NativeTestRun PublishedTests { get; init; }
    public IReadOnlyList<DependencyNotice> Dependencies { get; init; } = [];
    public string? BuilderImage { get; init; }
    public string? RuntimeImage { get; init; }
    public NativeRuntimeIndependence? RuntimeIndependence { get; init; }
    public NativeGlibcFloor? GlibcFloor { get; init; }
    public string? PayloadDirectory { get; init; }
    public string? PayloadManifest { get; init; }
    public IReadOnlyList<PayloadFile> Payload { get; init; } = [];
    public NativeTestRun? NormalizedTests { get; init; }
    public NativeTestRun? ExtractedTests { get; init; }
    public string? Archive { get; init; }
    public string? ArchiveSha256 { get; init; }
}
