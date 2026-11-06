using System.Text.Json;
using System.Xml.Linq;
using OpenCode.Mcp.Tools.Native.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

public sealed class NativeBuildScenarioTests
{
    [Test]
    [Timeout(30_000)]
    public async Task PrepareAsync_Should_Provide_An_Isolated_Windows_Build_And_Complete_Assets(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        await Assert.That(request.Rid).IsEqualTo("win-x64");
        await Assert.That(request.Root).IsEqualTo(scenario.Root);
        await Assert.That(request.Output).IsEqualTo(scenario.Output);
        await Assert.That(request.Host.Os).IsEqualTo("win");
        await Assert.That(request.Host.OsArchitecture).IsEqualTo("x64");
        await Assert.That(request.Host.ProcessArchitecture).IsEqualTo("x64");
        await Assert.That(request.Host.Environment["PATH"]).IsEqualTo("original-path");
        await Assert.That(await scenario.Files.File.ReadAllTextAsync(scenario.Dumpbin, token)).IsEqualTo("fixture");
        using var assets = JsonDocument.Parse(await scenario.Files.File.ReadAllTextAsync(scenario.Files.Path.Combine(request.Root, "src", "OpenCode.Mcp", "obj", "project.assets.json"), token));
        await Assert.That(assets.RootElement.GetProperty("targets").GetProperty("net10.0/win-x64").EnumerateObject().Select(static package => package.Name))
            .IsEquivalentTo(["Polly.Core/8.4.2", "Analyzer/1.0.0", "ServiceDefaults/fixture", "ModelContextProtocol/2.3.1"], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task AddStagingInputsAsync_Should_Supply_Assets_Symbols_And_Required_Notices(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        await scenario.PrepareAsync(token);
        await scenario.AddStagingInputsAsync(token);
        await Assert.That(scenario.Files.Directory.EnumerateFiles(scenario.Files.Path.Combine(scenario.Output, "publish")).Select(path => scenario.Files.Path.GetFileName(path)).Order(StringComparer.Ordinal))
            .IsEquivalentTo(["debug.pdb", "required.dat"], CollectionOrdering.Matching);
        await Assert.That(await scenario.Files.File.ReadAllTextAsync(scenario.Files.Path.Combine(scenario.Root, "LICENSE"), token)).IsEqualTo("fixture license");
        var notices = await scenario.Files.File.ReadAllTextAsync(scenario.Files.Path.Combine(scenario.Root, "THIRD-PARTY-NOTICES.md"), token);
        await Assert.That(notices).Contains("<!-- native-package: Polly.Core/8.4.2 -->");
        await Assert.That(notices).Contains("<!-- native-package: ModelContextProtocol/2.3.1 -->");
    }

    [Test]
    [Timeout(30_000)]
    public async Task PrepareAsync_Should_Configure_Publish_And_Test_Responses(CancellationToken token)
    {
        var scenario = new NativeBuildScenario { PublishExitCode = 7, PublishOutput = "fixture warning" };
        await scenario.PrepareAsync(token);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var published = await scenario.Runner.RunAsync(new NativeProcessRequest("dotnet", ["publish"], scenario.Root, environment, TimeSpan.FromSeconds(1)), token);
        await Assert.That(published.ExitCode).IsEqualTo(7);
        await Assert.That(published.StandardError).IsEqualTo("fixture warning");
        await Assert.That(await scenario.Files.File.ReadAllTextAsync(scenario.Files.Path.Combine(scenario.Output, "publish", "OpenCode.Mcp.exe"), token)).IsEqualTo("fixture-native-image");
        var results = scenario.Files.Path.Combine(scenario.Root, "results");
        await scenario.Runner.RunAsync(new NativeProcessRequest("dotnet", ["test", "--results-directory", results], scenario.Root, environment, TimeSpan.FromSeconds(1)), token);
        var report = XDocument.Parse(await scenario.Files.File.ReadAllTextAsync(scenario.Files.Path.Combine(results, "fixture.trx"), token));
        await Assert.That(report.Descendants("Counters").Single().Attributes().Select(static attribute => attribute.Name + "=" + attribute.Value))
            .IsEquivalentTo(["passed=12", "failed=0", "notExecuted=0"], CollectionOrdering.Matching);
    }
}
