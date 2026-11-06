using OpenCode.Mcp.Tools.Native;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeTestRunTests
{
    [Test]
    [Arguments(0, 12, 0, 0, true)]
    [Arguments(7, 12, 0, 0, false)]
    [Arguments(0, 12, 0, 1, false)]
    [Arguments(0, 0, 0, 0, false)]
    [Arguments(0, 12, 1, 0, false)]
    public async Task Succeeded_Should_Require_A_Zero_Exit_And_Only_Passing_Tests(int exitCode, int passed, int failed, int skipped, bool expected)
    {
        await Assert.That(new NativeTestRun(exitCode, passed, failed, skipped).Succeeded).IsEqualTo(expected);
    }
}
