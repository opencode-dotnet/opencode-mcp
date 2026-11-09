using OpenCode.Mcp.Tools.Native;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeInspectionPolicyTests
{
    [Test]
    [Arguments("linux-x64", "DotNetRuntimeDebugHeader@@V1.0")]
    [Arguments("linux-arm64", "DotNetRuntimeDebugHeader@@V1.0")]
    [Arguments("linux-x64", "DotNetRuntimeDebugHeader@V1.0")]
    [Arguments("linux-arm64", "DotNetRuntimeDebugHeader@V1.0")]
    public async Task Diagnostics_Should_Accept_The_Linux_Export_With_A_Symbol_Version(string rid, string symbol)
    {
        await Assert.That(NativeInspectionPolicy.Diagnostics([], rid, LinuxExports(symbol))).IsEmpty();
    }

    [Test]
    [Arguments("linux-x64", "DotNetRuntimeDebugHeaderX@@V1.0", "DotNetRuntimeDebugHeader")]
    [Arguments("linux-arm64", "DotNetRuntimeDebugHeaderX@V1.0", "DotNetRuntimeDebugHeader")]
    [Arguments("linux-x64", "XDotNetRuntimeDebugHeader@@V1.0", "DotNetRuntimeDebugHeader")]
    [Arguments("linux-arm64", "@DotNetRuntimeDebugHeader", "DotNetRuntimeDebugHeader")]
    [Arguments("win-x64", "DotNetRuntimeDebugHeader@@V1.0", "DotNetRuntimeDebugHeader")]
    [Arguments("win-arm64", "DotNetRuntimeDebugHeader@V1.0", "DotNetRuntimeDebugHeader")]
    [Arguments("osx-x64", "_DotNetRuntimeDebugHeader@@V1.0", "_DotNetRuntimeDebugHeader")]
    [Arguments("osx-arm64", "_DotNetRuntimeDebugHeader@V1.0", "_DotNetRuntimeDebugHeader")]
    public async Task Diagnostics_Should_Reject_Near_Miss_Exports_And_Versions_Outside_Linux(string rid, string symbol, string required)
    {
        await Assert.That(NativeInspectionPolicy.Diagnostics([], rid, LinuxExports(symbol)))
            .IsEquivalentTo(["Missing native export: " + required], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Diagnostics_Should_Reject_Managed_Companions_And_Runtime_Libraries_In_Path_Order()
    {
        string[] paths = ["z/coreclr.dll", "OpenCode.Mcp.runtimeconfig.json", "a/managed.dll", "OpenCode.Mcp.deps.json", "hostfxr.dll", "hostpolicy.dll"];
        await Assert.That(NativeInspectionPolicy.Diagnostics(paths, "win-x64", "DotNetRuntimeDebugHeader"))
            .IsEquivalentTo(["Forbidden runtime file: OpenCode.Mcp.deps.json", "Forbidden runtime file: OpenCode.Mcp.runtimeconfig.json",
                "Forbidden runtime file: a/managed.dll", "Forbidden runtime file: hostfxr.dll", "Forbidden runtime file: hostpolicy.dll",
                "Forbidden runtime file: z/coreclr.dll"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("win-x64", "Missing native export: DotNetRuntimeDebugHeader")]
    [Arguments("osx-arm64", "Missing native export: _DotNetRuntimeDebugHeader")]
    public async Task Diagnostics_Should_Reject_A_Framework_Dependent_Apphost_Without_The_Aot_Export(string rid, string diagnostic)
    {
        await Assert.That(NativeInspectionPolicy.Diagnostics([], rid, "main hostfxr_main"))
            .IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
    }

    private static string LinuxExports(string symbol) => "0000000000004028 D " + symbol + "\n0000000000000000 A V1.0\n";
}
