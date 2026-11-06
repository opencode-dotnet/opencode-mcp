using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeStagerTests
{
    private static readonly string?[] TestedCommands = ["OpenCode.Mcp.exe", "opencode-mcp.exe", "opencode-mcp.exe"];
    [Test]
    [Timeout(30_000)]
    public async Task StageAsync_Should_Normalize_All_Runtime_Assets_And_Test_The_Archive_Round_Trip(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var published = await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token);
        await scenario.AddStagingInputsAsync(token);
        var staged = await new NativeStager(scenario.Files, scenario.Runner).StageAsync(request, published.PublishDirectory, token);
        await Assert.That(staged.Payload.Select(static file => file.Path))
            .IsEquivalentTo(["LICENSE", "THIRD-PARTY-NOTICES.md", "opencode-mcp.exe", "payload.json", "required.dat"], CollectionOrdering.Matching);
        await Assert.That(staged.NormalizedTests).IsEqualTo(new NativeTestRun(0, 12, 0, 0));
        await Assert.That(staged.ExtractedTests).IsEqualTo(new NativeTestRun(0, 12, 0, 0));
        await Assert.That(staged.ExecutableSha256).IsEqualTo(published.ExecutableSha256);
        var archive = await Assert.That(staged.Archive).IsNotNull();
        await Assert.That(scenario.Files.File.Exists(scenario.Files.Path.Combine(request.Output, archive))).IsTrue();
        await Assert.That(scenario.Runner.Calls.Where(static call => call.Arguments[0] == "test")
            .Select(call => scenario.Files.Path.GetFileName(call.Environment["OPENCODE_MCP_TEST_EXECUTABLE"])))
            .IsEquivalentTo(TestedCommands, CollectionOrdering.Matching);
    }
}
