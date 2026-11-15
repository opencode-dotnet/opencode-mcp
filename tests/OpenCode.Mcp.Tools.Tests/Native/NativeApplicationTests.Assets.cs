using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed partial class NativeApplicationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunAsync_Should_Report_Assets_Or_Their_Complete_Refusal(bool refused, CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        if (refused)
        {
            scenario.Files.Directory.Delete(scenario.Evidence, recursive: true);
        }
        else
        {
            await scenario.DetachReceiptsAsync(token);
        }
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Runner, new FixtureHostReader(scenario.Build.Host), scenario.Builder.Native.Modes, output, errors);
        var result = await application.RunAsync(["assets", "--receipts", scenario.Files.Path.GetRelativePath(scenario.Build.Root, scenario.Receipts),
            "--evidence", scenario.Files.Path.GetRelativePath(scenario.Build.Root, scenario.Evidence), "--output", scenario.Files.Path.GetRelativePath(scenario.Build.Root, scenario.Output),
            "--version", scenario.Version], token);
        if (refused)
        {
            await Assert.That(errors.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)).IsEqualTo(
                "Missing receipt: win-x64\nMissing receipt: win-arm64\nMissing receipt: linux-x64\nMissing receipt: linux-arm64\nMissing receipt: osx-x64\nMissing receipt: osx-arm64\n");
            await Assert.That(output.ToString()).IsEmpty();
            await Assert.That(result).IsEqualTo(2);
            await Assert.That(scenario.Files.Directory.Exists(scenario.Output)).IsFalse();
        }
        else
        {
            string[] rids = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];
            string[] names = ["opencode-mcp-" + scenario.Version + "-win-x64.zip", "opencode-mcp-" + scenario.Version + "-win-arm64.zip",
                "opencode-mcp-" + scenario.Version + "-linux-x64.tar.gz", "opencode-mcp-" + scenario.Version + "-linux-arm64.tar.gz",
                "opencode-mcp-" + scenario.Version + "-osx-x64.tar.gz", "opencode-mcp-" + scenario.Version + "-osx-arm64.tar.gz"];
            var checksums = string.Concat(rids.Select((rid, index) => scenario.Digest(rid) + "  " + names[index] + "\n"))
                + scenario.ScriptDigest("install.sh") + "  install.sh\n" + scenario.ScriptDigest("install.ps1") + "  install.ps1\n";
            var report = new[] { scenario.Output }.Concat(rids.Select((rid, index) => names[index] + " SHA-256: " + scenario.Digest(rid)))
                .Concat(["install.sh SHA-256: " + scenario.ScriptDigest("install.sh"), "install.ps1 SHA-256: " + scenario.ScriptDigest("install.ps1")])
                .Append("SHA256SUMS SHA-256: " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(checksums))));
            await Assert.That(output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)).IsEqualTo(string.Join('\n', report) + "\n");
            await Assert.That(scenario.Files.Directory.GetFiles(scenario.Output).Select(path => scenario.Files.Path.GetFileName(path) ?? throw new InvalidOperationException()).Order(StringComparer.Ordinal))
                .IsEquivalentTo(names.Concat(["install.sh", "install.ps1", "SHA256SUMS"]).Order(StringComparer.Ordinal), CollectionOrdering.Matching);
            await Assert.That(errors.ToString()).IsEmpty();
            await Assert.That(result).IsEqualTo(0);
        }
        await Assert.That(scenario.Runner.Calls).IsEmpty();
    }

    [Test]
    public async Task RunAsync_Should_Resolve_Installer_Inputs_From_The_Checkout(CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        scenario.Files.Directory.Delete(scenario.Installers, recursive: true);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Runner, new FixtureHostReader(scenario.Build.Host), scenario.Builder.Native.Modes, output, errors);
        var result = await application.RunAsync(["assets", "--receipts", scenario.Receipts, "--evidence", scenario.Evidence,
            "--output", scenario.Output, "--version", scenario.Version], token);
        await Assert.That(result).IsEqualTo(2);
        await Assert.That(errors.ToString().Replace("\r\n", "\n", StringComparison.Ordinal))
            .IsEqualTo("Missing installer script: install.sh.\nMissing installer script: install.ps1.\n");
        await Assert.That(output.ToString()).IsEmpty();
        await Assert.That(scenario.Files.Directory.Exists(scenario.Output)).IsFalse();
        await Assert.That(scenario.Runner.Calls).IsEmpty();
    }
}
