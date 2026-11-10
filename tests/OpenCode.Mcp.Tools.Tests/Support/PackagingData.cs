using System.Reflection;
using OpenCode.Mcp.Tools.Packaging;

namespace OpenCode.Mcp.Tools.Tests.Support;

internal static class PackagingData
{
    public const string PackWarning = "src/product.csproj(3,5): warning NU5100: fixture [src/product.csproj]";
    public const string PackWarningDiagnostic = "Child pack warning: " + PackWarning;
    public const string PackageIdDiagnostic = "Package ID must be OpenCodeDotNet.Mcp.win-x64.";
    public const string ManifestNameDiagnostic = "Manifest name must be io.github.opencode-dotnet/opencode-mcp.";
    public const string MissingManifestDiagnostic = "Package: expected exactly one server.json.";
    public const string MissingNuspecDiagnostic = "Package: expected exactly one nuspec.";
    public const string MissingSettingsDiagnostic = "Package: expected exactly one DotnetToolSettings.xml.";
    public const string MissingCommandDiagnostic = "Package: expected exactly one Command.";
    public const string MissingRootFile = "Missing package root file: ";
    public const string EntryPointDiagnostic = "Package entry point must be ";
    public const string UnexpectedEntry = "Unexpected package entry: ";
    public const string RootTypesDiagnostic = "Root package types must be exactly DotnetTool, McpServer.";
    public const string ChildTypesDiagnostic = "Package types must be exactly DotnetToolRidPackage.";
    public const string CommandDiagnostic = "Package command must be opencode-mcp.";
    public const string ManifestPathDiagnostic = "Package manifest must be .mcp/server.json.";
    public const string RepositoryCommitDiagnostic = "Package repository commit differs from native receipt.";
    public const string MappingsDiagnostic = "Root package RID mappings must match the native manifest in order; observed: ";
    public const string ManifestsDifferDiagnostic = "Packed manifests must be byte-identical.";
    public const string MissingLicenseDiagnostic = "Missing payload file: LICENSE";
    public const string ChangedNoticesDiagnostic = "Changed payload file: THIRD-PARTY-NOTICES.md";
    public const string InputDriftDiagnostic = "Source or common build inputs changed after verification.";
    public const string ToolRidsDiagnostic = "ToolPackageRuntimeIdentifiers must match the native manifest in order.";
    public const string RootRunnerDiagnostic = "Root package must have no runner.";
    public const string RootEntryPointDiagnostic = "Root package must have no entry point.";
    public const string RootPackFailureDiagnostic = "Root pack exited 7:\npack failed\n";
    public const string MissingFeedFile = "Missing feed file: ";
    public const string UnexpectedFeedFile = "Unexpected feed file: ";
    public const string FeedDirectoryDiagnostic = "Feed directory must be absent or empty: ";
    public static string BuildVersion { get; } = (typeof(PackageIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?? throw new InvalidOperationException("Missing tools informational version.")).InformationalVersion.Split('+')[0];
    public static string ChildFileName => "OpenCodeDotNet.Mcp.win-x64." + BuildVersion + ".nupkg";
    public static string PackageCountDiagnostic => "Expected exactly one child package named " + ChildFileName + ".";
    public static string PackageVersionDiagnostic => "Package version must be " + BuildVersion + ".";
}
