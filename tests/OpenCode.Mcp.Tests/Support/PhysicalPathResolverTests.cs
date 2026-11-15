using System.IO.Abstractions;
using NSubstitute;
using Testably.Abstractions.Testing;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Checks physical resolution of paths whose last components do not exist yet.</summary>
public sealed class PhysicalPathResolverTests
{
    [Test]
    public async Task Resolve_Should_Keep_Missing_Components_As_Written()
    {
        var files = new MockFileSystem();
        var root = files.Path.Combine(files.Path.GetTempPath(), "owned");
        files.Directory.CreateDirectory(root);

        var resolved = new PhysicalPathResolver(WithRuntimeMissingPathFailure(files)).Resolve(files.Path.Combine(root, "home", ".dotnet", "tools"));

        await Assert.That(resolved).IsEqualTo(files.Path.Combine(root, "home", ".dotnet", "tools"));
    }

    [Test]
    public async Task Resolve_Should_Follow_An_Existing_Link_Before_Missing_Components()
    {
        var files = new MockFileSystem();
        var root = files.Path.Combine(files.Path.GetTempPath(), "owned");
        files.Directory.CreateDirectory(files.Path.Combine(root, "real"));
        files.Directory.CreateSymbolicLink(files.Path.Combine(root, "link"), files.Path.Combine(root, "real"));

        var resolved = new PhysicalPathResolver(WithRuntimeMissingPathFailure(files)).Resolve(files.Path.Combine(root, "link", "home", "tools"));

        await Assert.That(resolved).IsEqualTo(files.Path.Combine(root, "real", "home", "tools"));
    }

    /// <summary>
    /// The in-memory fake returns null for a missing path, but the runtime's ResolveLinkTarget throws
    /// DirectoryNotFoundException for one; this decorator restores the runtime behaviour.
    /// </summary>
    private static IFileSystem WithRuntimeMissingPathFailure(MockFileSystem files)
    {
        var directory = Substitute.For<IDirectory>();
        directory.Exists(Arg.Any<string>()).Returns(call => files.Directory.Exists(call.Arg<string>()));
        directory.ResolveLinkTarget(Arg.Any<string>(), Arg.Any<bool>()).Returns(call =>
        {
            var path = call.ArgAt<string>(0);
            return files.Directory.Exists(path) || files.File.Exists(path) || files.FileInfo.New(path).LinkTarget is not null
                ? files.Directory.ResolveLinkTarget(path, call.ArgAt<bool>(1))
                : throw new DirectoryNotFoundException("Could not find a part of the path '" + path + "'.");
        });
        var decorated = Substitute.For<IFileSystem>();
        decorated.Path.Returns(files.Path);
        decorated.File.Returns(files.File);
        decorated.FileInfo.Returns(files.FileInfo);
        decorated.Directory.Returns(directory);
        return decorated;
    }
}
