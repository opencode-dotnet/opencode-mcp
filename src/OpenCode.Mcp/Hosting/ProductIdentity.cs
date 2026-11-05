using System.Reflection;

namespace OpenCode.Mcp.Hosting;

/// <summary>Provides the product identity advertised to MCP clients.</summary>
public sealed class ProductIdentity
{
    /// <summary>Reads the identity of the product assembly.</summary>
    /// <exception cref="InvalidOperationException">The product has no informational version.</exception>
    public ProductIdentity()
    {
        var version = typeof(ProductIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("The product assembly must declare an informational version.");
        Version = RemoveSourceSuffix(version);
    }

    /// <summary>Gets the stable MCP product name.</summary>
    public string Name { get; } = "opencode-mcp";

    /// <summary>Gets the product version without its optional source suffix.</summary>
    public string Version { get; }

    /// <summary>Preserves the release and prerelease version while discarding optional source metadata.</summary>
    /// <param name="informationalVersion">The assembly's informational version.</param>
    /// <returns>The portion before the first source-metadata separator, or the unchanged version.</returns>
    internal static string RemoveSourceSuffix(string informationalVersion)
    {
        var suffix = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return suffix < 0 ? informationalVersion : informationalVersion[..suffix];
    }
}
