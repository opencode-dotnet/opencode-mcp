using OpenCode.Mcp.Tools.Native;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeInspectionPolicyTests
{
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
}
