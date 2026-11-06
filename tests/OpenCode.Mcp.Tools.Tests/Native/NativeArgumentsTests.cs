using OpenCode.Mcp.Tools.Native;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeArgumentsTests
{
    [Test]
    public async Task Parse_Should_Select_The_Requested_Command_And_Manifest_Without_Changing_Paths()
    {
        await Assert.That(NativeArguments.Parse(["validate", "--receipts", "receipts with spaces", "--manifest", "owned manifest.json"]))
            .IsEqualTo(new NativeCommand("validate", null, null, null, null, "receipts with spaces", "owned manifest.json"));
        await Assert.That(NativeArguments.Parse(["verify", "--rid", "win-x64", "--output", "owned output"]))
            .IsEqualTo(new NativeCommand("verify", "win-x64", "owned output", null, null, null));
        await Assert.That(NativeArguments.Parse(["stage", "--rid", "win-x64", "--publish-directory", "published"]))
            .IsEqualTo(new NativeCommand("stage", "win-x64", null, "published", null, null));
        await Assert.That(NativeArguments.Parse(["inspect", "--rid", "win-x64", "--path", "command.exe"]))
            .IsEqualTo(new NativeCommand("inspect", "win-x64", null, null, "command.exe", null));
    }

    [Test]
    public async Task Parse_Should_Refuse_Missing_Duplicate_And_Unknown_Options()
    {
        await Assert.That(() => NativeArguments.Parse(["verify"]))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Arguments: missing --rid.", StringComparison.Ordinal);
        await Assert.That(() => NativeArguments.Parse(["verify", "--rid", "win-x64", "--rid", "win-x64"]))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Arguments: duplicate --rid.", StringComparison.Ordinal);
        await Assert.That(() => NativeArguments.Parse(["inspect", "--rid", "win-x64", "--unexpected", "value"]))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Arguments: unknown option --unexpected for inspect.", StringComparison.Ordinal);
    }
}
