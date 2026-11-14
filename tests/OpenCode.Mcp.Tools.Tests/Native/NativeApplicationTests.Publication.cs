using System.Globalization;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed partial class NativeApplicationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunAsync_Should_Validate_Publication_Without_Changing_The_Candidate(bool refused, CancellationToken token)
    {
        var scenario = await NativePublicationScenario.CreateAsync(token);
        if (refused)
        {
            scenario.Files.File.Delete(scenario.Asset("win-x64.zip"));
        }
        var before = await scenario.CaptureAsync(token);
        var directories = scenario.Files.Directory.GetDirectories(scenario.Candidate.Build.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var host = scenario.Candidate.Build.Host with { Os = "unsupported", OsArchitecture = "unsupported", ProcessArchitecture = "unsupported" };
        var application = new NativeApplication(scenario.Files, scenario.Candidate.Runner, new FixtureHostReader(host), scenario.Candidate.Builder.Native.Modes, output, errors);
        var root = host.WorkingDirectory;
        var result = await application.RunAsync(["publication", "--receipts", scenario.Files.Path.GetRelativePath(root, scenario.Request.Receipts),
            "--feed", scenario.Files.Path.GetRelativePath(root, scenario.Feed), "--assets", scenario.Files.Path.GetRelativePath(root, scenario.Request.Assets),
            "--version", scenario.Request.Version, "--commit", scenario.Commit], token);
        await Assert.That(result).IsEqualTo(refused ? 2 : 0);
        await Assert.That(output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)).IsEqualTo(refused ? "" : "Publication candidate is complete.\n");
        await Assert.That(errors.ToString().Replace("\r\n", "\n", StringComparison.Ordinal))
            .IsEqualTo(refused ? "Missing release asset: opencode-mcp-" + scenario.Request.Version + "-win-x64.zip\n" : "");
        await scenario.AssertUnchangedAsync(before, token);
        await Assert.That(scenario.Files.Directory.GetDirectories(scenario.Candidate.Build.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            .IsEquivalentTo(directories, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(scenario.Candidate.Runner.Calls).IsEmpty();
    }

    [Test]
    public async Task RunAsync_Should_Validate_Installer_Projections_From_The_Candidate_Checkout(CancellationToken token)
    {
        var scenario = await NativePublicationScenario.CreateAsync(token);
        scenario.Files.Directory.Delete(scenario.Candidate.Installers, recursive: true);
        var before = await scenario.CaptureAsync(token);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Candidate.Runner, new FixtureHostReader(scenario.Candidate.Build.Host), scenario.Candidate.Builder.Native.Modes, output, errors);
        var result = await application.RunAsync(["publication", "--receipts", scenario.Request.Receipts, "--feed", scenario.Feed,
            "--assets", scenario.Request.Assets, "--version", scenario.Request.Version, "--commit", scenario.Commit], token);
        await Assert.That(result).IsEqualTo(2);
        await Assert.That(output.ToString()).IsEmpty();
        await Assert.That(errors.ToString().Replace("\r\n", "\n", StringComparison.Ordinal))
            .IsEqualTo("Missing installer script: install.sh.\nMissing installer script: install.ps1.\n");
        await scenario.AssertUnchangedAsync(before, token);
        await Assert.That(scenario.Candidate.Runner.Calls).IsEmpty();
    }
}
