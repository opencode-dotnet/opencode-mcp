using OpenCode.Mcp.Tools.Native;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeArgumentsTests
{
    [Test]
    public async Task Parse_Should_Select_Root_Packing_And_Require_Both_Directories()
    {
        await Assert.That(NativeArguments.Parse(["pack-root", "--receipts", "completed evidence", "--feed", "complete feed"]))
            .IsEqualTo(new NativeCommand("pack-root", null, null, null, null, "completed evidence", Feed: "complete feed"));
        await Assert.That(() => NativeArguments.Parse(["pack-root"]))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Arguments: missing --receipts.", StringComparison.Ordinal);
        await Assert.That(() => NativeArguments.Parse(["pack-root", "--receipts", "completed"]))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Arguments: missing --feed.", StringComparison.Ordinal);
        await Assert.That(() => NativeArguments.Parse(["pack-root", "--receipts", "completed", "--feed", "one", "--feed", "two"]))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Arguments: duplicate --feed.", StringComparison.Ordinal);
        await Assert.That(() => NativeArguments.Parse(["pack-root", "--receipts", "completed", "--feed"]))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Arguments: missing value for --feed.", StringComparison.Ordinal);
        await Assert.That(() => NativeArguments.Parse(["pack-root", "--rid", "win-x64"]))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Arguments: unknown option --rid for pack-root.", StringComparison.Ordinal);
    }

    [Test]
    public async Task Parse_Should_Select_Child_Packing_And_Require_Its_Evidence()
    {
        await Assert.That(NativeArguments.Parse(["pack-child", "--rid", "win-x64", "--evidence", "staged evidence"]))
            .IsEqualTo(new NativeCommand("pack-child", "win-x64", null, null, null, null, Evidence: "staged evidence"));
        await Assert.That(() => NativeArguments.Parse(["pack-child"]))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Arguments: missing --rid.", StringComparison.Ordinal);
        await Assert.That(() => NativeArguments.Parse(["pack-child", "--rid", "win-x64"]))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Arguments: missing --evidence.", StringComparison.Ordinal);
        await Assert.That(() => NativeArguments.Parse([])).ThrowsExactly<InvalidOperationException>()
            .WithMessage("Arguments: use verify, stage, inspect, validate, matrix, control, prove-linux, pack-child, pack-root or snapshot.", StringComparison.Ordinal);
    }

    [Test]
    [Arguments("control", "--rid", "win-x64")]
    [Arguments("snapshot", "--output", "owned")]
    [Arguments("matrix", "--manifest", "manifest.json", "--github-output", "github-output")]
    [Arguments("prove-linux", "--rid", "linux-x64", "--evidence", "downloaded")]
    public async Task Parse_Should_Select_The_New_Command(params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        NativeCommand? command = null;
        await Assert.That(() => command = NativeArguments.Parse(arguments)).ThrowsNothing();
        var selected = await Assert.That(command).IsNotNull();
        var expected = arguments[0] switch
        {
            "control" => new NativeCommand("control", "win-x64", null, null, null, null),
            "snapshot" => new NativeCommand("snapshot", null, "owned", null, null, null),
            "matrix" => new NativeCommand("matrix", null, null, null, null, null, "manifest.json", "github-output"),
            _ => new NativeCommand("prove-linux", "linux-x64", null, null, null, null, Evidence: "downloaded"),
        };
        await Assert.That(selected).IsEqualTo(expected);
    }

    [Test]
    [Arguments("matrix", "Arguments: missing --manifest.")]
    [Arguments("snapshot", "Arguments: missing --output.")]
    [Arguments("control", "Arguments: missing --rid.")]
    [Arguments("prove-linux", "Arguments: missing --rid.")]
    public async Task Parse_Should_Require_The_New_Command_Inputs(string operation, string diagnostic) =>
        await Assert.That(() => NativeArguments.Parse([operation])).ThrowsExactly<InvalidOperationException>().WithMessage(diagnostic, StringComparison.Ordinal);

    [Test]
    public async Task Parse_Should_Reject_Cut_Options_And_Duplicate_New_Options()
    {
        await Assert.That(() => NativeArguments.Parse(["control", "--rid", "win-x64", "--evidence", "owned"]))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Arguments: unknown option --evidence for control.", StringComparison.Ordinal);
        await Assert.That(() => NativeArguments.Parse(["prove-linux", "--rid", "linux-x64", "--evidence", "one", "--evidence", "two"]))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Arguments: duplicate --evidence.", StringComparison.Ordinal);
        await Assert.That(() => NativeArguments.Parse(["matrix", "--manifest", "manifest.json", "--github-output"]))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Arguments: missing value for --github-output.", StringComparison.Ordinal);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Parse_Should_Refuse_A_Missing_Value_Before_The_Next_Option(bool followedByOption)
    {
        string[] arguments = followedByOption ? ["verify", "--rid", "--output", "owned"] : ["verify", "--rid"];
        await Assert.That(() => NativeArguments.Parse(arguments)).ThrowsExactly<InvalidOperationException>()
            .WithMessage("Arguments: missing value for --rid.", StringComparison.Ordinal);
    }

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
