using System.Globalization;
using System.Text.RegularExpressions;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Packaging;

internal static partial class McpPackagePolicy
{
    private static readonly string[] RootFiles = ["README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "icon.png"];

    public static IReadOnlyList<string> PackageNames(PackageIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return [identity.Id + "." + identity.Version + ".nupkg", .. identity.RuntimeIdentifiers.Select(rid => identity.Id + "." + rid + "." + identity.Version + ".nupkg")];
    }

    public static IReadOnlyList<string> PackDiagnostics(NativeProcessResult result, string kind = "Child")
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExitCode != 0)
        {
            return [string.Create(CultureInfo.InvariantCulture, $"{kind} pack exited {result.ExitCode}:\n{result.Output}")];
        }
        return [.. result.Output.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => Warning().IsMatch(line)).Distinct(StringComparer.Ordinal)
            .Select(line => kind + " pack warning: " + line)];
    }

    [GeneratedRegex(@"\bwarning(?:\s+[a-z]+\d+)?\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout.Infinite)]
    private static partial Regex Warning();

    public static IReadOnlyList<string> RootDiagnostics(PackageInspection package, PackageIdentity identity, string sourceCommit)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(sourceCommit);
        var diagnostics = new List<string>();
        CheckIdentity(package, identity, identity.Id, diagnostics);
        if (!package.PackageTypes.SequenceEqual(["DotnetTool", "McpServer"], StringComparer.Ordinal))
        {
            diagnostics.Add("Root package types must be exactly DotnetTool, McpServer.");
        }
        CheckCommand(package, identity, diagnostics);
        if (package.Runner is not null)
        {
            diagnostics.Add("Root package must have no runner.");
        }
        if (package.EntryPoint is not null)
        {
            diagnostics.Add("Root package must have no entry point.");
        }
        if (!package.RuntimeIdentifierPackages.SequenceEqual(identity.RuntimeIdentifiers.Select(rid => new PackageRidMapping(rid, identity.Id + "." + rid))))
        {
            diagnostics.Add("Root package RID mappings must match the native manifest in order; observed: "
                + string.Join(", ", package.RuntimeIdentifierPackages.Select(static mapping => mapping.RuntimeIdentifier + "=" + mapping.Id)));
        }
        diagnostics.AddRange(package.Payload.Select(static file => "Root package must carry no implementation: " + file.Path));
        CheckChannel(package, identity, sourceCommit, diagnostics);
        return diagnostics.AsReadOnly();
    }

    public static IReadOnlyList<string> ChildDiagnostics(PackageInspection package, PackageIdentity identity, NativeReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(receipt);
        var id = identity.Id + "." + receipt.Rid;
        var diagnostics = new List<string>();
        CheckIdentity(package, identity, id, diagnostics);
        if (!package.PackageTypes.SequenceEqual(["DotnetToolRidPackage"], StringComparer.Ordinal))
        {
            diagnostics.Add("Package types must be exactly DotnetToolRidPackage.");
        }
        CheckCommand(package, identity, diagnostics);
        if (package.Runner != "executable")
        {
            diagnostics.Add("Package runner must be executable.");
        }
        var entryPoint = new NativeRid(receipt.Rid).CommandName;
        if (package.EntryPoint != entryPoint)
        {
            diagnostics.Add("Package entry point must be " + entryPoint + ".");
        }
        diagnostics.AddRange(PayloadInventory.Compare([.. package.Payload.Select(static file => file with { UnixMode = null })],
            [.. receipt.Payload.Select(static file => file with { UnixMode = null })]));
        CheckChannel(package, identity, receipt.SourceCommit, diagnostics);
        return diagnostics.AsReadOnly();
    }

    private static void CheckIdentity(PackageInspection package, PackageIdentity identity, string id, List<string> diagnostics)
    {
        if (package.Id != id)
        {
            diagnostics.Add("Package ID must be " + id + ".");
        }
        if (package.Version != identity.Version)
        {
            diagnostics.Add("Package version must be " + identity.Version + ".");
        }
    }

    private static void CheckCommand(PackageInspection package, PackageIdentity identity, List<string> diagnostics)
    {
        if (package.CommandName != identity.CommandName || identity.CommandName != "opencode-mcp")
        {
            diagnostics.Add("Package command must be opencode-mcp.");
        }
    }

    private static void CheckChannel(PackageInspection package, PackageIdentity identity, string sourceCommit, List<string> diagnostics)
    {
        if (package.Authors != "Deniz İrgin")
        {
            diagnostics.Add("Package authors must be Deniz İrgin.");
        }
        if (package.ProjectUrl != "https://github.com/opencode-dotnet/opencode-mcp")
        {
            diagnostics.Add("Package project URL must be https://github.com/opencode-dotnet/opencode-mcp.");
        }
        if (package.ManifestPath != ".mcp/server.json")
        {
            diagnostics.Add("Package manifest must be .mcp/server.json.");
        }
        diagnostics.AddRange(McpManifestPolicy.Diagnostics(package.Manifest, package.ManifestSize, identity));
        foreach (var name in RootFiles.Where(name => !package.Paths.Contains(name, StringComparer.Ordinal)))
        {
            diagnostics.Add("Missing package root file: " + name);
        }
        if (package.RepositoryCommit != sourceCommit)
        {
            diagnostics.Add("Package repository commit differs from native receipt.");
        }
        diagnostics.AddRange(UnexpectedEntries(package));
    }

    private static IEnumerable<string> UnexpectedEntries(PackageInspection package)
    {
        var prefix = package.SettingsPath[..(package.SettingsPath.LastIndexOf('/') + 1)];
        var coreProperties = package.Paths.Where(static path => path.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal)
            && path.EndsWith(".psmdcp", StringComparison.Ordinal)).ToArray();
        return package.Paths.Where(path => !path.StartsWith(prefix, StringComparison.Ordinal)
            && !RootFiles.Contains(path, StringComparer.Ordinal)
            && path is not (".mcp/server.json" or "[Content_Types].xml" or "_rels/.rels")
            && path != package.Id + ".nuspec"
            && !(coreProperties.Length == 1 && path == coreProperties[0]))
            .Order(StringComparer.Ordinal).Select(static path => "Unexpected package entry: " + path);
    }
}
