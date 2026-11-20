using System.Globalization;
using System.IO.Abstractions;
using NSubstitute;
using OpenCode.Mcp.Tools.Ci;
using OpenCode.Mcp.Tools.Tests.Support;
using Testably.Abstractions.Testing;

namespace OpenCode.Mcp.Tools.Tests.Ci;

public sealed class MergeGroupReuseRunnerTests
{
    [Test]
    [Arguments("all", "true", MergeGroupReuseFixture.ApprovedReason)]
    [Arguments("different-tree", "false", MergeGroupReuseFixture.UnprovenTreeReason)]
    public async Task RunAsync_Should_Append_The_Decision_And_Report_Its_Reason(string row, string reuse, string reason, CancellationToken token)
    {
        var files = new MockFileSystem();
        var root = files.Path.Combine(files.Path.GetTempPath(), "facts");
        var outputFile = files.Path.Combine(root, "github-output");
        await new MergeGroupReuseFixture().WithRow(row).WriteAsync(files, root, token);
        await files.File.WriteAllTextAsync(outputFile, "existing=value\n", token);
        using var output = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };

        await new MergeGroupReuseRunner(files, output).RunAsync(root, outputFile, token);

        await Assert.That(await files.File.ReadAllTextAsync(outputFile, token)).IsEqualTo($"existing=value\nreuse={reuse}\nreason={reason}\n");
        await Assert.That(output.ToString()).IsEqualTo(reason + "\n");
    }

    [Test]
    public async Task RunAsync_Should_Report_And_Append_A_Refusal_When_A_Fact_Is_Unreadable(CancellationToken token)
    {
        var files = new MockFileSystem();
        var root = files.Path.Combine(files.Path.GetTempPath(), "facts");
        var outputFile = files.Path.Combine(root, "github-output");
        await new MergeGroupReuseFixture().WithoutTree().WriteAsync(files, root, token);
        await files.File.WriteAllTextAsync(outputFile, "", token);
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => files.File.ReadAllTextAsync(files.Path.Combine(root, "tree"), token));
        var diagnostic = await Assert.That(missing).IsNotNull();
        using var output = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };

        await new MergeGroupReuseRunner(files, output).RunAsync(root, outputFile, token);

        await Assert.That(await files.File.ReadAllTextAsync(outputFile, token)).IsEqualTo($"reuse=false\nreason={MergeGroupReuseFixture.UnreadableFactsReason}\n");
        await Assert.That(output.ToString()).IsEqualTo($"Cannot read reuse facts: {diagnostic.Message}\n{MergeGroupReuseFixture.UnreadableFactsReason}\n");
    }

    [Test]
    public async Task RunAsync_Should_Report_The_Write_Error_And_Reason_When_Output_Is_Unwritable(CancellationToken token)
    {
        var files = new MockFileSystem();
        var root = files.Path.Combine(files.Path.GetTempPath(), "facts");
        var outputFile = files.Path.Combine(root, "github-output");
        await new MergeGroupReuseFixture().WriteAsync(files, root, token);
        var file = Substitute.For<IFile>();
        file.ReadAllTextAsync(Arg.Any<string>(), token).Returns(call => files.File.ReadAllTextAsync(call.Arg<string>(), token));
        file.AppendAllTextAsync(outputFile, Arg.Any<string>(), token).Returns(_ => Task.FromException(new IOException("read-only output")));
        var decorated = Substitute.For<IFileSystem>();
        decorated.File.Returns(file);
        decorated.Path.Returns(files.Path);
        using var output = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };

        await new MergeGroupReuseRunner(decorated, output).RunAsync(root, outputFile, token);

        await Assert.That(output.ToString()).IsEqualTo($"Cannot write reuse decision: read-only output\n{MergeGroupReuseFixture.ApprovedReason}\n");
        await Assert.That(files.File.Exists(outputFile)).IsFalse();
    }

    [Test]
    public async Task RunAsync_Should_Propagate_Cancellation_While_Reading_Facts(CancellationToken token)
    {
        var files = new MockFileSystem();
        var root = files.Path.Combine(files.Path.GetTempPath(), "facts");
        var outputFile = files.Path.Combine(root, "github-output");
        await new MergeGroupReuseFixture().WriteAsync(files, root, token);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var runner = new MergeGroupReuseRunner(files, output);

        await Assert.That(() => runner.RunAsync(root, outputFile, cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(output.ToString()).IsEmpty();
        await Assert.That(files.File.Exists(outputFile)).IsFalse();
    }
}
