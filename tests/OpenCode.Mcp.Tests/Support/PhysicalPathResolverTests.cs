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

        var resolved = new PhysicalPathResolver(files).Resolve(files.Path.Combine(root, "home", ".dotnet", "tools"));

        await Assert.That(resolved).IsEqualTo(files.Path.Combine(root, "home", ".dotnet", "tools"));
    }

    [Test]
    public async Task Resolve_Should_Follow_An_Existing_Link_Before_Missing_Components()
    {
        var files = new MockFileSystem();
        var root = files.Path.Combine(files.Path.GetTempPath(), "owned");
        files.Directory.CreateDirectory(files.Path.Combine(root, "real"));
        files.Directory.CreateSymbolicLink(files.Path.Combine(root, "link"), files.Path.Combine(root, "real"));

        var resolved = new PhysicalPathResolver(files).Resolve(files.Path.Combine(root, "link", "home", "tools"));

        await Assert.That(resolved).IsEqualTo(files.Path.Combine(root, "real", "home", "tools"));
    }
}
