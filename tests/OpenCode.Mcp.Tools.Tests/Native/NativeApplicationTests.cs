using System.Globalization;
using System.Text.Json;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeApplicationTests
{
    [Test]
    [Timeout(30_000)]
    [Arguments("timeout", "Native command timed out.")]
    [Arguments("cancel", "Native command cancelled (Ctrl+C).")]
    [Arguments("json", "Malformed JSON input: fixture")]
    [Arguments("format", "Malformed toolchain or assets input: fixture")]
    [Arguments("key", "Missing toolchain or assets field: fixture")]
    public async Task RunAsync_Should_Name_Expected_Input_And_Cancellation_Failures(string failure, string diagnostic, CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var manifest = await new NativeManifestFixture().WriteAsync(scenario.Files, token);
        scenario.Files.Directory.CreateDirectory(scenario.Files.Path.Combine(request.Root, "packaging"));
        scenario.Files.File.Copy(manifest, scenario.Files.Path.Combine(request.Root, "packaging", "native-manifest.json"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        scenario.Runner.Response = async (_, _) =>
        {
            if (failure == "cancel")
            {
                await cancellation.CancelAsync();
            }
            throw failure switch
            {
                "json" => new JsonException("fixture"),
                "format" => new FormatException("fixture"),
                "key" => new KeyNotFoundException("fixture"),
                _ => new OperationCanceledException("fixture"),
            };
        };
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Runner, new FixtureHostReader(request.Host with { WorkingDirectory = request.Root }), scenario.Modes, output, errors);
        int? result = null;
        await Assert.That(async () => result = await application.RunAsync(["verify", "--rid", "win-x64", "--output", scenario.Output], cancellation.Token)).ThrowsNothing();
        await Assert.That(result).IsEqualTo(2);
        await Assert.That(output.ToString()).IsEmpty();
        await Assert.That(errors.ToString().TrimEnd('\r', '\n')).IsEqualTo(diagnostic);
    }

    [Test]
    [Timeout(30_000)]
    public async Task RunAsync_Should_Use_The_Explicit_Manifest_And_Report_The_Full_Incomplete_Set(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var manifest = await new NativeManifestFixture().WriteAsync(scenario.Files, token);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Runner, new FixtureHostReader(request.Host with { WorkingDirectory = request.Root }), scenario.Modes, output, errors);
        var result = await application.RunAsync(["validate", "--receipts", scenario.Output, "--manifest", manifest], token);
        await Assert.That(errors.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)).IsEqualTo(
            "Missing receipt: win-x64\nMissing receipt: win-arm64\nMissing receipt: linux-x64\nMissing receipt: linux-arm64\nMissing receipt: osx-x64\nMissing receipt: osx-arm64\n");
        await Assert.That(output.ToString()).IsEmpty();
        await Assert.That(result).IsEqualTo(2);
        await Assert.That(scenario.Runner.Calls).IsEmpty();
    }
}
