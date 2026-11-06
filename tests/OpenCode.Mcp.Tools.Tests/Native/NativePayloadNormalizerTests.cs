using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativePayloadNormalizerTests
{
    [Test]
    [Timeout(30_000)]
    [Arguments("linux-x64", "opencode-mcp", 493)]
    [Arguments("osx-arm64", "opencode-mcp", 493)]
    [Arguments("win-x64", "opencode-mcp.exe", null)]
    public async Task NormalizeAsync_Should_Copy_Modes_Only_For_A_Unix_Rid(string rid, string command, int? expectedMode, CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var receipt = await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token);
        scenario.Modes.Write(receipt.Executable, (UnixFileMode)493);
        var payload = await new NativePayloadNormalizer(scenario.Files, scenario.Modes).NormalizeAsync(request.Output, receipt with { Rid = rid }, token);
        var normalized = scenario.Files.Path.Combine(payload, command);
        await Assert.That(await scenario.Files.File.ReadAllTextAsync(normalized, token)).IsEqualTo("fixture-native-image");
        await Assert.That((int?)scenario.Modes.Read(normalized)).IsEqualTo(expectedMode);
    }
}
