using System.Text.Json;

namespace OpenCode.Mcp.Tools.Native;

internal static class DependencyInventory
{
    private static readonly string[] RuntimeAssetKinds = ["runtime", "native", "runtimeTargets"];
    public static IReadOnlyList<DependencyNotice> Read(JsonElement assets, string rid)
    {
        var target = assets.GetProperty("targets").EnumerateObject().Single(property => property.Name.EndsWith("/" + rid, StringComparison.Ordinal)).Value;
        var libraries = assets.GetProperty("libraries");
        var packages = new List<DependencyNotice>();
        foreach (var name in target.EnumerateObject().Where(static property => IsRuntimePackage(property.Value)).Select(static property => property.Name))
        {
            var separator = name.LastIndexOf('/');
            var library = libraries.GetProperty(name);
            packages.Add(new DependencyNotice(name[..separator], name[(separator + 1)..],
                library.GetProperty("sha512").GetString() ?? throw new InvalidOperationException("Missing package content identity: " + name),
                library.GetProperty("path").GetString() ?? throw new InvalidOperationException("Missing package path: " + name)));
        }
        return [.. packages.OrderBy(static package => package.Id, StringComparer.Ordinal)];
    }

    private static bool IsRuntimePackage(JsonElement package) => package.GetProperty("type").GetString() == "package"
        && RuntimeAssetKinds.Any(name => package.TryGetProperty(name, out var assets)
            && assets.EnumerateObject().Any(static asset => !asset.Name.EndsWith("/_._", StringComparison.Ordinal)));
}
