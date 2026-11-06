using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class PayloadInventoryTests
{
    [Test]
    [Timeout(30_000)]
    public async Task CaptureAsync_Should_Not_Record_A_Unix_Mode_For_A_Windows_Payload(CancellationToken token)
    {
        var fixture = new PayloadFixture();
        await fixture.PrepareAsync(token);
        fixture.Modes.Write(fixture.Files.Path.Combine(fixture.Root, "LICENSE"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var inventory = await new PayloadInventory(fixture.Files, fixture.Modes, fixture.Rid).CaptureAsync(fixture.Root, token);
        await Assert.That(inventory).IsEquivalentTo(fixture.Inventory, CollectionOrdering.Matching);
        await Assert.That(inventory.Single(static file => file.Path == "LICENSE").UnixMode).IsNull();
    }

    [Test]
    [Timeout(30_000)]
    public async Task CompareAsync_Should_Name_Duplicate_Declared_Payload_Paths(CancellationToken token)
    {
        var fixture = new PayloadFixture();
        await fixture.PrepareAsync(token);
        IReadOnlyList<string>? diagnostics = null;
        await Assert.That(async () => diagnostics = await new PayloadInventory(fixture.Files, fixture.Modes, fixture.Rid)
            .CompareAsync(fixture.Root, [.. fixture.Inventory, fixture.Inventory[1], fixture.Inventory[0]], token)).ThrowsNothing();
        await Assert.That(diagnostics).IsEquivalentTo(["Duplicate payload path: LICENSE", "Duplicate payload path: z-asset"], CollectionOrdering.Matching);
    }

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
