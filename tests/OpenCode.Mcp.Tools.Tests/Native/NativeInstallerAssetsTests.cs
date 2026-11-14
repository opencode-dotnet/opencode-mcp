using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeInstallerAssetsTests
{
    private static readonly string[] Names = ["install.sh", "install.ps1"];

    [Test]
    public async Task ReadAsync_Should_Capture_Unchanged_Scripts_In_Release_Order(CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        var assets = await new NativeInstallerAssets(scenario.Files).ReadAsync(scenario.Installers, scenario.Version, token);
        await Assert.That(assets.Select(static asset => asset.Name)).IsEquivalentTo(["install.sh", "install.ps1"], CollectionOrdering.Matching);
        foreach (var asset in assets)
        {
            await Assert.That(asset.Bytes).IsEquivalentTo(scenario.Scripts[asset.Name], CollectionOrdering.Matching);
            await Assert.That(asset.Sha256).IsEqualTo(scenario.ScriptDigest(asset.Name));
            await Assert.That(await scenario.Files.File.ReadAllBytesAsync(scenario.Files.Path.Combine(scenario.Installers, asset.Name), token))
                .IsEquivalentTo(scenario.Scripts[asset.Name], CollectionOrdering.Matching);
        }
    }

    [Test]
    [Arguments("missing")]
    [Arguments("version")]
    [Arguments("duplicate")]
    [Arguments("prefix")]
    [Arguments("spacing")]
    [Arguments("indented")]
    [Arguments("trailing")]
    [Arguments("mixed")]
    public async Task ReadAsync_Should_Require_Both_Scripts_And_Their_Exact_Default_Version(string fault, CancellationToken token)
    {
        var scenario = await NativeReleaseAssetsScenario.CreateAsync(token);
        var before = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in new[] { "install.ps1", "install.sh" })
        {
            var path = scenario.Files.Path.Combine(scenario.Installers, name);
            if (fault == "missing" || (fault == "mixed" && name == "install.sh"))
            {
                scenario.Files.File.Delete(path);
                continue;
            }
            var text = await scenario.Files.File.ReadAllTextAsync(path, token);
            var assignment = name == "install.sh" ? "OPENCODE_MCP_RELEASE_VERSION='" + scenario.Version + "'"
                : "$OpenCodeMcpReleaseVersion = '" + scenario.Version + "'";
            var changed = fault switch
            {
                "duplicate" => text + assignment + "\n",
                "prefix" => text + (name == "install.sh" ? "OPENCODE_MCP_RELEASE_VERSION='different'" : "$OpenCodeMcpReleaseVersionOther = 'different'") + "\n",
                "spacing" => text.Replace(assignment, assignment.Replace("=", "= ", StringComparison.Ordinal), StringComparison.Ordinal),
                "indented" => text.Replace(assignment, " " + assignment, StringComparison.Ordinal),
                "trailing" => text.Replace(assignment, assignment + " # trailing", StringComparison.Ordinal),
                _ => text.Replace(scenario.Version, "different", StringComparison.Ordinal),
            };
            await scenario.Files.File.WriteAllTextAsync(path, changed, token);
            before.Add(name, await scenario.Files.File.ReadAllBytesAsync(path, token));
        }
        var expected = Names.Select(name =>
            fault == "missing" || (fault == "mixed" && name == "install.sh") ? "Missing installer script: " + name + "."
                : "Installer default version must be " + scenario.Version + ": " + name + ".");
        await Assert.That(async () =>
        {
            await new NativeInstallerAssets(scenario.Files).ReadAsync(scenario.Installers, scenario.Version, token);
        })
            .ThrowsExactly<InvalidOperationException>().WithMessage(string.Join('\n', expected), StringComparison.Ordinal);
        foreach (var pair in before)
        {
            await Assert.That(await scenario.Files.File.ReadAllBytesAsync(scenario.Files.Path.Combine(scenario.Installers, pair.Key), token))
                .IsEquivalentTo(pair.Value, CollectionOrdering.Matching);
        }
        await Assert.That(scenario.Files.Directory.GetFiles(scenario.Installers).Select(path => scenario.Files.Path.GetFileName(path)).Order(StringComparer.Ordinal))
            .IsEquivalentTo(before.Keys.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
    }
}
