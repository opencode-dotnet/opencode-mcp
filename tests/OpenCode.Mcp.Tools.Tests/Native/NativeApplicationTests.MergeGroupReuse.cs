using System.Globalization;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using OpenCode.Mcp.Tools.Tests.Support;
using Testably.Abstractions.Testing;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed partial class NativeApplicationTests
{
    [Test]
    [Arguments(false, "true", MergeGroupReuseFixture.ApprovedReason)]
    [Arguments(true, "false", MergeGroupReuseFixture.UnreadableFactsReason)]
    public async Task RunAsync_Should_Report_Merge_Group_Reuse_Without_A_Native_Manifest(bool missingTree, string reuse, string reason, CancellationToken token)
    {
        var files = new MockFileSystem();
        var root = files.Path.Combine(files.Path.GetTempPath(), "checkout");
        var facts = files.Path.Combine(root, "facts folder");
        var outputFile = files.Path.Combine(root, "github output");
        var fixture = new MergeGroupReuseFixture();
        if (missingTree)
        {
            fixture.WithoutTree();
        }
        await fixture.WriteAsync(files, facts, token);
        await files.File.WriteAllTextAsync(outputFile, "existing=value\n", token);
        using var output = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var host = new NativeHost("fixture", "fixture", "fixture", "fixture", new Dictionary<string, string?>(StringComparer.Ordinal)) { WorkingDirectory = root };
        var runner = new FakeNativeProcessRunner();
        var application = new NativeApplication(files, runner, new FixtureHostReader(host), new FileUnixModes(files), output, errors);

        var result = await application.RunAsync(["merge-group-reuse", "--facts", "facts folder", "--output", "github output"], token);

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(await files.File.ReadAllTextAsync(outputFile, token)).IsEqualTo($"existing=value\nreuse={reuse}\nreason={reason}\n");
        if (missingTree)
        {
            var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => files.File.ReadAllTextAsync(files.Path.Combine(facts, "tree"), token));
            var diagnostic = await Assert.That(missing).IsNotNull();
            await Assert.That(output.ToString()).IsEqualTo($"Cannot read reuse facts: {diagnostic.Message}\n{reason}\n");
        }
        else
        {
            await Assert.That(output.ToString()).IsEqualTo(reason + "\n");
        }
        await Assert.That(errors.ToString()).IsEmpty();
        await Assert.That(runner.Calls).IsEmpty();
    }
}
