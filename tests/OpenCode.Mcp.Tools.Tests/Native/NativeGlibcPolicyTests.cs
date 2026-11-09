using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeGlibcPolicyTests
{
    [Test]
    [Arguments("0000000000000000      DF *UND*\t0000000000000000 (GLIBC_2.9)  pipe2\n0000000000000000      DF *UND*\t0000000000000000 (GLIBC_2.35) __memcmpeq\n0000000000000000      DF *UND*\t0000000000000000 (GLIBC_2.10) accept4\n0000000000000000      DF *UND*\t0000000000000000 (GLIBC_2.2.5) __libc_start_main", "2.35")]
    [Arguments("0000000000000000      DF *UND*\t0000000000000000 (GLIBC_2.9)  pipe2\n0000000000000000      DF *UND*\t0000000000000000 (GLIBC_2.10) accept4\n0000000000000000      DF *UND*\t0000000000000000 (GLIBC_2.2.5) __libc_start_main", "2.10")]
    public async Task Measure_Should_Select_The_Highest_Numeric_Version(string symbols, string expected)
    {
        NativeGlibcFloor? floor = null;
        await Assert.That(() => floor = NativeGlibcPolicy.Measure(new NativeProcessResult(0, "GNU objdump fixture", ""), new NativeProcessResult(0, symbols, ""))).ThrowsNothing();
        var measured = await Assert.That(floor).IsNotNull();
        await Assert.That(measured.Version).IsEqualTo(expected);
        await Assert.That(measured.ToolVersion).IsEqualTo("GNU objdump fixture");
    }

    [Test]
    [Arguments(1, "GLIBC_2.35", "glibc measurement requires successful objdump output.")]
    [Arguments(0, "GLIBC_PRIVATE", "objdump output contains no numeric GLIBC requirement.")]
    [Arguments(0, "GLIBC_2.bad GLIBC_2.35", "Malformed GLIBC requirement: 2.bad")]
    [Arguments(0, "GLIBC_-2.35 GLIBC_2.35", "Malformed GLIBC requirement: -2.35")]
    [Arguments(0, "GLIBC_.35 GLIBC_2.35", "Malformed GLIBC requirement: .35")]
    public async Task Measure_Should_Refuse_Missing_Or_Malformed_Numeric_Evidence(int exitCode, string symbols, string diagnostic)
    {
        await Assert.That(() => NativeGlibcPolicy.Measure(new NativeProcessResult(0, "GNU objdump fixture", ""), new NativeProcessResult(exitCode, symbols, "")))
            .ThrowsExactly<InvalidOperationException>().WithMessage(diagnostic, StringComparison.Ordinal);
    }
}
