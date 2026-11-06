using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeNoticeComposerTests
{
    [Test]
    [Timeout(30_000)]
    public async Task ComposeAsync_Should_Refuse_A_Resolved_Package_Without_Its_Notice(CancellationToken token)
    {
        var fixture = new NoticeFixture();
        await fixture.PrepareAsync(token);
        await Assert.That(async () => await new NativeNoticeComposer(fixture.Files).ComposeAsync(fixture.Root, NoticeFixture.Packages, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Missing required notice: Polly.Core/8.4.2", StringComparison.Ordinal);
        await fixture.AddPollyAsync(token);
        var notice = await new NativeNoticeComposer(fixture.Files).ComposeAsync(fixture.Root, NoticeFixture.Packages, token);
        await Assert.That(notice).Contains(NoticeFixture.PollyNotice);
    }
}
