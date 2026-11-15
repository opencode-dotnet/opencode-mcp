using System.Text;
using OpenCode.Mcp.Tools.Native;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Exceptions;

namespace OpenCode.Mcp.Tests.Support;

public sealed class PackagedStartupAssertionsTests
{
    [Test]
    [Arguments("changed")]
    [Arguments("missing")]
    [Arguments("additional")]
    [Arguments("renamed")]
    public async Task AssertPayloadAsync_Should_Refuse_Changed_Missing_And_Additional_Files(string fault, CancellationToken token)
    {
        var files = new MockFileSystem();
        var root = files.Path.GetFullPath("payload");
        files.Directory.CreateDirectory(root);
        await files.File.WriteAllBytesAsync(files.Path.Combine(root, "z"), Encoding.UTF8.GetBytes("abc"), token);
        await files.File.WriteAllBytesAsync(files.Path.Combine(root, "a"), Encoding.UTF8.GetBytes("abc"), token);
        PayloadFile[] expected = [new("a", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", 3, null),
            new("z", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", 3, null)];
        var assertions = new PackagedStartupAssertions(files, "unused-reference-feed");
        await assertions.AssertPayloadAsync(root, "win-x64", expected, token);
        if (fault == "changed")
        {
            await files.File.WriteAllTextAsync(files.Path.Combine(root, "a"), "changed", token);
        }
        else if (fault == "missing")
        {
            files.File.Delete(files.Path.Combine(root, "a"));
        }
        else if (fault == "additional")
        {
            await files.File.WriteAllTextAsync(files.Path.Combine(root, "extra"), "unexpected", token);
        }
        else
        {
            files.File.Move(files.Path.Combine(root, "a"), files.Path.Combine(root, "b"));
        }
        var failure = await Assert.That(async () => { await assertions.AssertPayloadAsync(root, "win-x64", expected, token); }).ThrowsExactly<AssertionException>();
        var error = await Assert.That(failure).IsNotNull();
        var expectedMessage = fault == "changed" ? "ba7816bf" : "Expected to be equivalent to [a, z]";
        await Assert.That(error.Message).Contains(expectedMessage);
    }
}
