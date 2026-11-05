using OpenCode.Mcp.Hosting;

namespace OpenCode.Mcp.Tests.Hosting;

/// <summary>Checks version metadata stripping without changing release or prerelease text.</summary>
public sealed class ProductIdentityTests
{
    /// <summary>Only the first source-metadata separator and its suffix are removed.</summary>
    [Test]
    [Arguments("1.0.0", "1.0.0")]
    [Arguments("1.0.0+abc", "1.0.0")]
    [Arguments("1.0.0-pre.1+abc+def", "1.0.0-pre.1")]
    [Arguments("1.0.0+", "1.0.0")]
    public async Task RemoveSourceSuffix_Should_Preserve_Release_And_Prerelease_When_Metadata_Is_Optional(
        string informationalVersion, string expected)
    {
        await Assert.That(ProductIdentity.RemoveSourceSuffix(informationalVersion))
            .IsEqualTo(expected, StringComparison.Ordinal);
    }
}
