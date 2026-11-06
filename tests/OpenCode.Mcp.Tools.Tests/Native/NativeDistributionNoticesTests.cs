using System.Text.Json;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeDistributionNoticesTests
{
    [Test]
    [Timeout(30_000)]
    public async Task ComposeAsync_Should_Refuse_An_Empty_Runtime_Distribution_Set(CancellationToken token)
    {
        var fixture = new DistributionNoticeFixture();
        await fixture.PrepareAsync(token);
        fixture.Assets.Remove("project");
        using var assets = JsonDocument.Parse(fixture.Assets.ToJsonString());
        await Assert.That(async () => await new NativeDistributionNotices(fixture.Files).ComposeAsync(assets.RootElement, "win-x64", token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Missing runtime distributions: win-x64", StringComparison.Ordinal);
    }

    [Test]
    [Timeout(30_000)]
    public async Task ComposeAsync_Should_Include_The_Resolved_Framework_License_And_Notices(CancellationToken token)
    {
        var fixture = new DistributionNoticeFixture();
        await fixture.PrepareAsync(token);
        using var assets = JsonDocument.Parse(fixture.Assets.ToJsonString());
        var notices = await new NativeDistributionNotices(fixture.Files).ComposeAsync(assets.RootElement, "win-x64", token);
        await Assert.That(notices).IsEqualTo("\n## Microsoft.NETCore.App.Runtime.win-x64/10.0.14\n\nPackage content identity: framework-content\n\n### LICENSE.txt\n\nframework license\n\n### THIRD-PARTY-NOTICES.TXT\n\nframework third-party notices\n");
    }

    [Test]
    [Timeout(30_000)]
    public async Task ComposeAsync_Should_Refuse_Missing_Framework_License_Material(CancellationToken token)
    {
        var fixture = new DistributionNoticeFixture();
        await fixture.PrepareAsync(token);
        fixture.Files.File.Delete(fixture.Files.Path.Combine(fixture.Package, "LICENSE.txt"));
        using var assets = JsonDocument.Parse(fixture.Assets.ToJsonString());
        await Assert.That(async () => await new NativeDistributionNotices(fixture.Files).ComposeAsync(assets.RootElement, "win-x64", token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Missing required distribution license: Microsoft.NETCore.App.Runtime.win-x64/10.0.14", StringComparison.Ordinal);
    }
}
