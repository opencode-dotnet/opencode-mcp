using System.Globalization;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeApplicationTests
{
    [Test]
    [Timeout(30_000)]
    public async Task RunAsync_Should_Use_The_Explicit_Manifest_And_Report_The_Full_Incomplete_Set(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var manifest = await new NativeManifestFixture().WriteAsync(scenario.Files, token);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Runner, new FixtureHostReader(request.Host with { WorkingDirectory = request.Root }), output, errors);
        var result = await application.RunAsync(["validate", "--receipts", scenario.Output, "--manifest", manifest], token);
        await Assert.That(errors.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)).IsEqualTo(
            "Missing receipt: win-x64\nMissing receipt: win-arm64\nMissing receipt: linux-x64\nMissing receipt: linux-arm64\nMissing receipt: osx-x64\nMissing receipt: osx-arm64\n");
        await Assert.That(output.ToString()).IsEmpty();
        await Assert.That(result).IsEqualTo(2);
        await Assert.That(scenario.Runner.Calls).IsEmpty();
    }
}
