using System.Globalization;
using System.Text.RegularExpressions;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Packaging;

internal static partial class McpPackagePolicy
{
    private static readonly string[] RootFiles = ["README.md", "LICENSE", "THIRD-PARTY-NOTICES.md"];

    public static IReadOnlyList<string> PackDiagnostics(NativeProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExitCode != 0)
        {
            return [string.Create(CultureInfo.InvariantCulture, $"Child pack exited {result.ExitCode}:\n{result.Output}")];
        }
        return [.. result.Output.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => Warning().IsMatch(line))
            .Select(static line => "Child pack warning: " + line)];
    }

    [GeneratedRegex(@"\bwarning(?:\s+[a-z]+\d+)?\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout.Infinite)]
    private static partial Regex Warning();

    public static IReadOnlyList<string> ChildDiagnostics(PackageInspection package, PackageIdentity identity, NativeReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(receipt);
        var id = identity.Id + "." + receipt.Rid;
        var diagnostics = new List<string>();
        if (package.Id != id)
        {
            diagnostics.Add("Package ID must be " + id + ".");
        }
        if (package.Version != identity.Version)
        {
            diagnostics.Add("Package version must be " + identity.Version + ".");
        }
        if (!package.PackageTypes.SequenceEqual(["DotnetToolRidPackage"], StringComparer.Ordinal))
        {
            diagnostics.Add("Package types must be exactly DotnetToolRidPackage.");
        }
        if (package.CommandName != identity.CommandName || identity.CommandName != "opencode-mcp")
        {
            diagnostics.Add("Package command must be opencode-mcp.");
        }
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
        if (package.ManifestPath != ".mcp/server.json")
        {
            diagnostics.Add("Package manifest must be .mcp/server.json.");
        }
        diagnostics.AddRange(McpManifestPolicy.Diagnostics(package.Manifest, package.ManifestSize, identity));
        foreach (var name in RootFiles.Where(name => !package.Paths.Contains(name, StringComparer.Ordinal)))
        {
            diagnostics.Add("Missing package root file: " + name);
        }
        if (package.RepositoryCommit != receipt.SourceCommit)
        {
            diagnostics.Add("Package repository commit differs from native receipt.");
        }
        return diagnostics.AsReadOnly();
    }
}
