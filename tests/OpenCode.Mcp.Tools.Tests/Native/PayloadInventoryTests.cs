using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class PayloadInventoryTests
{
    [Test]
    [Timeout(30_000)]
    public async Task CompareAsync_Should_Reject_A_Changed_Required_Payload_File(CancellationToken token)
    {
        var fixture = new PayloadFixture();
        await fixture.PrepareAsync(token);
        await fixture.ChangeAsync("LICENSE", "changed", token);
        await Assert.That(await new PayloadInventory(fixture.Files, fixture.Modes, fixture.Rid).CompareAsync(fixture.Root, fixture.Inventory, token))
            .IsEquivalentTo(["Changed payload file: LICENSE"], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task CompareAsync_Should_Reject_Missing_And_Extra_Archived_Payload_Files_In_Path_Order(CancellationToken token)
    {
        var fixture = new PayloadFixture();
        await fixture.PrepareAsync(token);
        fixture.Remove("LICENSE");
        await fixture.ChangeAsync("unexpected", "extra", token);
        await Assert.That(await new PayloadInventory(fixture.Files, fixture.Modes, fixture.Rid).CompareAsync(fixture.Root, fixture.Inventory, token))
            .IsEquivalentTo(["Missing payload file: LICENSE", "Unexpected payload file: unexpected"], CollectionOrdering.Matching);
    }
}
