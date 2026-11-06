using System.IO.Abstractions;
using System.Text;
using System.Text.Json;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeDistributionNotices(IFileSystem files)
{
    public async Task<string> ComposeAsync(JsonElement assets, string rid, CancellationToken token)
    {
        var folders = assets.GetProperty("packageFolders").EnumerateObject().Select(static property => property.Name).ToArray();
        var notices = new StringBuilder();
        foreach (var package in DependencyInventory.Read(assets, rid))
        {
            var directory = FindPackage(folders, package.PackageDirectory);
            await AppendMaterialsAsync(notices, package, directory, token).ConfigureAwait(false);
        }
        var frameworks = Frameworks(assets, rid);
        if (frameworks.Length == 0)
        {
            throw new InvalidOperationException("Missing runtime distributions: " + rid);
        }
        foreach (var framework in frameworks)
        {
            var directory = FindPackage(folders, framework.PackageDirectory);
            if (!files.Directory.EnumerateFiles(directory).Any(path => files.Path.GetFileName(path).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Missing required distribution license: " + framework.Id + "/" + framework.Version);
            }
            var identity = await files.File.ReadAllTextAsync(files.Path.Combine(directory, framework.Id.ToLowerInvariant() + "." + framework.Version + ".nupkg.sha512"), token).ConfigureAwait(false);
            await AppendMaterialsAsync(notices, framework with { ContentIdentity = identity.Trim() }, directory, token).ConfigureAwait(false);
        }
        return notices.ToString();
    }

    private string FindPackage(IReadOnlyList<string> folders, string relative) => folders.Select(folder => files.Path.Combine(folder, relative))
        .FirstOrDefault(files.Directory.Exists) ?? throw new InvalidOperationException("Missing resolved package directory: " + relative);

    private async Task AppendMaterialsAsync(StringBuilder notices, DependencyNotice package, string directory, CancellationToken token)
    {
        var materials = files.Directory.EnumerateFiles(directory).Where(path => IsNotice(files.Path.GetFileName(path))).Order(StringComparer.Ordinal).ToArray();
        if (materials.Length == 0)
        {
            return;
        }
        notices.Append("\n## ").Append(package.Id).Append('/').Append(package.Version).Append("\n\nPackage content identity: ").Append(package.ContentIdentity).Append('\n');
        foreach (var path in materials)
        {
            notices.Append("\n### ").Append(files.Path.GetFileName(path)).Append("\n\n")
                .Append(await files.File.ReadAllTextAsync(path, token).ConfigureAwait(false)).Append('\n');
        }
    }

    private static bool IsNotice(string name) => name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("THIRD-PARTY-NOTICES", StringComparison.OrdinalIgnoreCase);

    private static DependencyNotice[] Frameworks(JsonElement assets, string rid)
    {
        if (!assets.TryGetProperty("project", out var project))
        {
            return [];
        }
        var framework = project.GetProperty("frameworks").EnumerateObject().Single().Value;
        var references = framework.GetProperty("frameworkReferences").EnumerateObject().Select(static property => property.Name).ToArray();
        return [.. framework.GetProperty("downloadDependencies").EnumerateArray()
            .Where(package => references.Any(reference => (package.GetProperty("name").GetString() ?? "").StartsWith(reference + ".Runtime.", StringComparison.Ordinal))
                && (package.GetProperty("name").GetString() ?? "").EndsWith("." + rid, StringComparison.Ordinal))
            .Select(static package => Framework(package)).OrderBy(static package => package.Id, StringComparer.Ordinal)];
    }

    private static DependencyNotice Framework(JsonElement package)
    {
        var id = package.GetProperty("name").GetString() ?? throw new InvalidOperationException("Missing distribution name.");
        var versions = (package.GetProperty("version").GetString() ?? "").Trim('[', ']').Split(',', StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (versions.Length != 1 || versions[0].Length == 0)
        {
            throw new InvalidOperationException("Distribution needs one exact restored version: " + id);
        }
        return new DependencyNotice(id, versions[0], "", id.ToLowerInvariant() + "/" + versions[0]);
    }
}
