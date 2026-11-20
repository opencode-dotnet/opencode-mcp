using OpenCode.Mcp.Tools.Ci;
using OpenCode.Mcp.Tools.Tests.Support;

namespace OpenCode.Mcp.Tools.Tests.Ci;

public sealed class MergeGroupReuseDecisionTests
{
    [Test]
    public async Task Reason_Should_Allow_Reuse_When_All_Rules_Hold() =>
        await Assert.That(MergeGroupReuseDecision.Reason(new MergeGroupReuseFixture().Facts)).IsEqualTo(MergeGroupReuseFixture.ApprovedReason);

    [Test]
    [Arguments("artifact-at-start")]
    [Arguments("artifact-at-end")]
    [Arguments("crlf-tested-tree")]
    public async Task Reason_Should_Allow_Reuse_When_Evidence_Is_On_An_Accepted_Boundary(string row) =>
        await Assert.That(MergeGroupReuseDecision.Reason(new MergeGroupReuseFixture().WithRow(row).Facts)).IsEqualTo(MergeGroupReuseFixture.ApprovedReason);

    [Test]
    [Arguments("one-parent", MergeGroupReuseFixture.InvalidGroupReason)]
    [Arguments("invalid-queue-ref", MergeGroupReuseFixture.InvalidGroupReason)]
    [Arguments("invalid-pr-number", MergeGroupReuseFixture.InvalidGroupReason)]
    [Arguments("non-pr-queue-ref", MergeGroupReuseFixture.InvalidGroupReason)]
    [Arguments("invalid-head", MergeGroupReuseFixture.InvalidGroupReason)]
    [Arguments("invalid-base", MergeGroupReuseFixture.InvalidGroupReason)]
    [Arguments("moved-head", MergeGroupReuseFixture.ChangedPullRequestReason)]
    [Arguments("feature-base", MergeGroupReuseFixture.ChangedPullRequestReason)]
    [Arguments("different-pr", MergeGroupReuseFixture.ChangedPullRequestReason)]
    [Arguments("no-run", MergeGroupReuseFixture.NoGreenResultReason)]
    [Arguments("failed-attempt", MergeGroupReuseFixture.NoGreenResultReason)]
    [Arguments("running-attempt", MergeGroupReuseFixture.NoGreenResultReason)]
    [Arguments("invalid-attempt", MergeGroupReuseFixture.NoGreenResultReason)]
    [Arguments("newer-failed-run", MergeGroupReuseFixture.NoGreenResultReason)]
    [Arguments("other-workflow", MergeGroupReuseFixture.NoGreenResultReason)]
    [Arguments("other-event", MergeGroupReuseFixture.NoGreenResultReason)]
    [Arguments("other-run-head", MergeGroupReuseFixture.NoGreenResultReason)]
    [Arguments("failed-result", MergeGroupReuseFixture.NoGreenResultReason)]
    [Arguments("old-result", MergeGroupReuseFixture.NoGreenResultReason)]
    [Arguments("other-run-result", MergeGroupReuseFixture.NoGreenResultReason)]
    [Arguments("duplicate-result", MergeGroupReuseFixture.NoGreenResultReason)]
    [Arguments("no-artifact", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("old-artifact", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("different-tree", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("different-base-tree", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("non-hash-tree", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("non-hash-base-tree", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("missing-base-tree", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("extra-tested-tree-line", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("blank-tested-tree-line", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("extra-tested-tree-newline", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("empty-base-tree", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("replacement-after", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("replacement-before", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("duplicate-artifact", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("expired-artifact", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("failed-changes", MergeGroupReuseFixture.UnprovenTreeReason)]
    [Arguments("malformed", MergeGroupReuseFixture.MalformedFactsReason)]
    [Arguments("wrong-json-type", MergeGroupReuseFixture.MalformedFactsReason)]
    [Arguments("null-run-time", MergeGroupReuseFixture.MalformedFactsReason)]
    public async Task Reason_Should_Report_The_Refusal_When_A_Fact_Changes(string row, string expected)
    {
        var facts = new MergeGroupReuseFixture().WithRow(row).Facts;
        string? reason = null;
        await Assert.That(() => reason = MergeGroupReuseDecision.Reason(facts)).ThrowsNothing();
        await Assert.That(reason).IsEqualTo(expected);
    }

    [Test]
    [Arguments(".github/workflows/ci.yml")]
    [Arguments(".GITHUB/workflows/ci.yml")]
    [Arguments("tools/example.cs")]
    [Arguments("TOOLS/example.cs")]
    [Arguments("tools/Directory.Build.props")]
    [Arguments("Directory.Build.user")]
    [Arguments("nested/directory.build.props")]
    [Arguments("nested/Directory.Build.targets")]
    [Arguments("nested/Directory.Build.rsp")]
    [Arguments("nested/Directory.Packages.props")]
    [Arguments("global.json")]
    [Arguments("nested/Global.json")]
    [Arguments("nested/nuget.config")]
    [Arguments("nested/NuGet.Config")]
    public async Task Reason_Should_Refuse_Reuse_When_Any_Path_Is_Guarded(string path) =>
        await Assert.That(MergeGroupReuseDecision.Reason(new MergeGroupReuseFixture().WithChangedPath(path).Facts)).IsEqualTo(MergeGroupReuseFixture.GuardedPathReason);

    [Test]
    public async Task Reason_Should_Refuse_Reuse_When_A_Fact_Is_Missing() =>
        await Assert.That(MergeGroupReuseDecision.Reason(new MergeGroupReuseFixture().WithoutTree().Facts)).IsEqualTo(MergeGroupReuseFixture.MalformedFactsReason);
}
