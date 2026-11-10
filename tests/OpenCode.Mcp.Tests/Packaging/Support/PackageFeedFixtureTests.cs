using OpenCode.Mcp.Tools.Native;

namespace OpenCode.Mcp.Tests.Packaging.Support;

public sealed class PackageFeedFixtureTests
{
    [Test]
    [Arguments(null, "linux-x64", "OPENCODE_MCP_TEST_RID is absent or blank. Set it to the manifest RID matching the host and private dotnet process.")]
    [Arguments("", "linux-x64", "OPENCODE_MCP_TEST_RID is absent or blank. Set it to the manifest RID matching the host and private dotnet process.")]
    [Arguments(" ", "linux-x64", "OPENCODE_MCP_TEST_RID is absent or blank. Set it to the manifest RID matching the host and private dotnet process.")]
    [Arguments("unknown", "linux-x64", "OPENCODE_MCP_TEST_RID is not in the native manifest: unknown")]
    [Arguments("win-x64", "linux-x64", "OPENCODE_MCP_TEST_RID win-x64 must match host linux-x64 and private dotnet linux-x64.")]
    [Arguments("linux-x64", "linux-arm64", "OPENCODE_MCP_TEST_RID linux-x64 must match host linux-x64 and private dotnet linux-arm64.")]
    public async Task ResolveRid_Should_Refuse_An_Absent_Unknown_Or_Mismatched_Rid(string? rid, string sdkRid, string message)
    {
        var manifest = new NativeManifest(1, "", "", [new NativeRuntime("win-x64", "windows"), new NativeRuntime("linux-x64", "linux")]);
        await Assert.That(() => PackageFeedFixture.ResolveRid(rid, manifest, "linux-x64", sdkRid)).ThrowsExactly<InvalidOperationException>()
            .WithMessage(message, StringComparison.Ordinal);
    }

    [Test]
    public async Task ResolveRid_Should_Select_The_Explicit_Matching_Manifest_Rid()
    {
        var manifest = new NativeManifest(1, "", "", [new NativeRuntime("win-x64", "windows"), new NativeRuntime("linux-x64", "linux")]);
        await Assert.That(PackageFeedFixture.ResolveRid("linux-x64", manifest, "linux-x64", "linux-x64")).IsEqualTo("linux-x64");
    }
}
