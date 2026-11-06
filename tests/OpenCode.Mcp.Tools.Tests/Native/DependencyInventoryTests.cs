using System.Text.Json;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class DependencyInventoryTests
{
    [Test]
    public async Task Read_Should_Include_Polly_And_Only_Resolved_Runtime_Packages_In_Name_Order()
    {
        using var assets = JsonDocument.Parse(AssetsFixture.Create().ToJsonString());
        var packages = DependencyInventory.Read(assets.RootElement, "win-x64");
        await Assert.That(packages.Select(static package => package.Id + "/" + package.Version + ":" + package.ContentIdentity))
            .IsEquivalentTo(["ModelContextProtocol/2.3.1:mcp-content", "Polly.Core/8.4.2:polly-content"], CollectionOrdering.Matching);
        await Assert.That(packages.Select(static package => package.Id)).Contains("Polly.Core");
    }
}
