using System.IO.Abstractions;
using System.Text.Json;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeMatrix(IFileSystem files)
{
    public Task WriteAsync(NativeManifest manifest, string path, CancellationToken token)
    {
        var linux = manifest.Runtimes.Where(static runtime => new NativeRid(runtime.Rid).IsLinux)
            .Select(runtime => new { rid = runtime.Rid, runner = runtime.Runner, builderImage = manifest.BuilderImage, runtimeImage = manifest.RuntimeImage }).ToArray();
        var native = manifest.Runtimes.Where(static runtime => !new NativeRid(runtime.Rid).IsLinux)
            .Select(static runtime => new { rid = runtime.Rid, runner = runtime.Runner }).ToArray();
        return files.File.AppendAllTextAsync(path, "linux-matrix=" + JsonSerializer.Serialize(new { include = linux })
            + "\nnative-matrix=" + JsonSerializer.Serialize(new { include = native }) + "\n", token);
    }
}
