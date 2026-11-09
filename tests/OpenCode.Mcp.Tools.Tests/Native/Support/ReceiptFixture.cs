using OpenCode.Mcp.Tools.Native;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal static class ReceiptFixture
{
    public static NativeReceipt Create(NativeRuntime runtime) => new()
    {
        Rid = runtime.Rid,
        Version = "fixture-version",
        SourceCommit = "fixture-source",
        SourceStatus = "",
        SdkVersion = "fixture-sdk",
        CommonInputsSha256 = "fixture-inputs",
        DependenciesSha256 = "fixture-dependencies",
        RequestedRunner = runtime.Runner,
        ActualRunner = runtime.Runner,
        Host = runtime.Rid,
        Os = runtime.Rid.Split('-')[0],
        OsArchitecture = runtime.Rid.Split('-')[1],
        Toolchain = "fixture-toolchain",
        AssetsSha256 = "fixture-assets",
        ProcessArchitecture = runtime.Rid.Split('-')[1],
        Properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PublishAot"] = "true",
            ["InvariantGlobalization"] = "true",
            ["IlcTreatWarningsAsErrors"] = "true",
            ["DebuggerSupport"] = "true",
            ["NoWarn"] = "",
            ["SuppressTrimAnalysisWarnings"] = "",
            ["SuppressAotAnalysisWarnings"] = "",
        },
        PublishDirectory = "published",
        Executable = "published/command",
        ExecutableSha256 = "fixture-executable",
        PublishCommand = ["publish"],
        PublishExitCode = 0,
        Warnings = [],
        Inspection = "DotNetRuntimeDebugHeader",
        PublishedTests = new NativeTestRun(0, 12, 0, 0),
        NormalizedTests = new NativeTestRun(0, 12, 0, 0),
        ExtractedTests = new NativeTestRun(0, 12, 0, 0),
        PayloadDirectory = "payload",
        PayloadManifest = "payload-manifest.json",
        Payload = [new PayloadFile("LICENSE", "fixture-license", 7, null),
            new PayloadFile("THIRD-PARTY-NOTICES.md", "fixture-notices", 7, null),
            new PayloadFile(runtime.Rid.StartsWith("win-", StringComparison.Ordinal) ? "opencode-mcp.exe" : "opencode-mcp", "fixture-executable", 7, null),
            new PayloadFile("payload.json", "fixture-metadata", 7, null)],
        Archive = "payload.zip",
        ArchiveSha256 = "fixture-archive",
        RuntimeIndependence = Proof(runtime.Rid),
        GlibcFloor = runtime.Rid.StartsWith("linux-", StringComparison.Ordinal) ? new NativeGlibcFloor("2.35", "GNU objdump fixture") : null,
        BuilderImage = runtime.Rid.StartsWith("linux-", StringComparison.Ordinal) ? "registry.test/builder@sha256:" + new string('a', 64) : null,
        RuntimeImage = runtime.Rid.StartsWith("linux-", StringComparison.Ordinal) ? "registry.test/runtime@sha256:" + new string('b', 64) : null,
    };

    private static NativeRuntimeIndependence Proof(string rid)
    {
        if (rid.StartsWith("linux-", StringComparison.Ordinal))
        {
            return new NativeRuntimeIndependence("linux-clean-container", new NativeTestRun(0, 12, 0, 0),
                "registry.test/runtime@sha256:" + new string('b', 64), "sha256:" + new string('c', 64), true);
        }
        return new NativeRuntimeIndependence("matching-host-inspection", Inspection: rid.StartsWith("osx-", StringComparison.Ordinal) ? "_DotNetRuntimeDebugHeader" : "DotNetRuntimeDebugHeader");
    }
}
