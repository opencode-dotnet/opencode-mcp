using System.IO.Abstractions;
using System.Reflection.PortableExecutable;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeInspector(IFileSystem files, NativeProcesses processes)
{
    private static readonly string[] RuntimeLibraries = ["hostfxr", "hostpolicy", "coreclr"];
    public async Task<string> InspectAsync(string rid, string path, NativeToolchain toolchain, CancellationToken token)
    {
        var directory = files.Directory.Exists(path) ? path : files.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Inspection needs an executable or directory.");
        var executable = files.Directory.Exists(path)
            ? files.Directory.EnumerateFiles(path).Single(file => files.Path.GetFileName(file) is "OpenCode.Mcp.exe" or "OpenCode.Mcp" or "opencode-mcp.exe" or "opencode-mcp")
            : path;
        var forbidden = new List<string>();
        foreach (var file in files.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var isForbidden = await IsForbiddenAsync(file, token).ConfigureAwait(false);
            if (isForbidden)
            {
                forbidden.Add(files.Path.GetRelativePath(directory, file).Replace('\\', '/'));
            }
        }
        var exports = await processes.RequireAsync(toolchain.Inspector, [.. toolchain.InspectorArguments, executable], toolchain.PublishEnvironment, token).ConfigureAwait(false);
        var diagnostics = NativeInspectionPolicy.Diagnostics(forbidden, rid, exports);
        if (diagnostics.Count != 0)
        {
            throw new InvalidOperationException(string.Join('\n', diagnostics));
        }
        return exports;
    }

    private async Task<bool> IsForbiddenAsync(string path, CancellationToken token)
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
        using var image = new PEReader(imageBytes);
        return image.HasMetadata;
    }
}
