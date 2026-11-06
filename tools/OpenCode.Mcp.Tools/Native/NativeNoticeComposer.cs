using System.IO.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeNoticeComposer(IFileSystem files)
{
    public async Task<string> ComposeAsync(string root, IReadOnlyList<DependencyNotice> packages, CancellationToken token)
    {
        var notices = await files.File.ReadAllTextAsync(files.Path.Combine(root, "THIRD-PARTY-NOTICES.md"), token).ConfigureAwait(false);
        var missing = packages.Where(package => !notices.Contains($"<!-- native-package: {package.Id}/{package.Version} -->", StringComparison.Ordinal))
            .OrderBy(static package => package.Id, StringComparer.Ordinal).Select(static package => "Missing required notice: " + package.Id + "/" + package.Version).ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidOperationException(string.Join('\n', missing));
        }
        return notices;
    }
}
