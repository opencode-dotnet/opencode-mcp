using System.IO.Abstractions;
using System.Reflection.PortableExecutable;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeInspector(IFileSystem files, NativeProcesses processes)
{
    private static readonly string[] RuntimeLibraries = ["hostfxr", "hostpolicy", "coreclr"];
    public async Task<string> InspectAsync(string rid, string path, NativeToolchain toolchain, CancellationToken token)
    {
        var isDirectory = files.Directory.Exists(path);
        var directory = isDirectory ? path : files.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Inspection needs an executable or directory.");
        var target = new NativeRid(rid);
        var executable = isDirectory
            ? files.Directory.EnumerateFiles(path).Single(file => files.Path.GetFileName(file) == target.PublishedName || files.Path.GetFileName(file) == target.CommandName)
            : path;
        var forbidden = new List<string>();
        foreach (var file in isDirectory ? files.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories) : [path])
        {
            var isForbidden = await IsForbiddenAsync(file, token).ConfigureAwait(false);
            if (isForbidden)
            {
                forbidden.Add(files.Path.GetRelativePath(directory, file).Replace('\\', '/'));
            }
        }
        var exports = await processes.RequireAsync(toolchain.Inspector, [.. toolchain.InspectorArguments, executable], toolchain.PublishEnvironment, token).ConfigureAwait(false);
        var diagnostics = NativeInspectionPolicy.Diagnostics(forbidden, rid, exports);
        NativeDiagnostics.Refuse(diagnostics);
        return exports;
    }

    internal async Task<bool> IsForbiddenAsync(string path, CancellationToken token)
    {
        var name = files.Path.GetFileName(path);
        if (name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
            || RuntimeLibraries.Any(library => name.Contains(library, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        using var imageBytes = new MemoryStream(await files.File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
        try
        {
            using var image = new PEReader(imageBytes);
            return image.HasMetadata;
        }
        catch (BadImageFormatException error)
        {
            throw new InvalidOperationException("Forbidden or unreadable image: " + path, error);
        }
    }
}
