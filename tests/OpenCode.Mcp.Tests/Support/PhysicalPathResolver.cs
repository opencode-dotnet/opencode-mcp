using System.Diagnostics;
using System.IO.Abstractions;

namespace OpenCode.Mcp.Tests.Support;

internal sealed class PhysicalPathResolver(IFileSystem files)
{
    public string Resolve(string path)
    {
        var root = files.Path.GetPathRoot(path);
        Debug.Assert(root is not null);
        var resolved = root;
        foreach (var component in path[root.Length..].Split([files.Path.DirectorySeparatorChar, files.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = files.Path.Combine(resolved, component);
            if (!files.File.Exists(resolved) && !files.Directory.Exists(resolved) && files.FileInfo.New(resolved).LinkTarget is null)
            {
                continue; // A missing component is no link, so the rest of the path stays as written.
            }
            var target = files.Directory.ResolveLinkTarget(resolved, returnFinalTarget: true);
            if (target is not null)
            {
                resolved = Resolve(target.FullName);
            }
        }
        return resolved;
    }
}
