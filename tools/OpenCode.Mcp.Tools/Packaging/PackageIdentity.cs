namespace OpenCode.Mcp.Tools.Packaging;

internal sealed record PackageIdentity(string Id, string Version, string CommandName, IReadOnlyList<string> RuntimeIdentifiers);
