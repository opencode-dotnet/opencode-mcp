using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeSourceReader(IFileSystem files, NativeProcesses processes)
{
    public async Task<NativeSourceEvidence> ReadAsync(string root, CancellationToken token)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var commit = await processes.RequireAsync("git", ["rev-parse", "HEAD"], environment, token).ConfigureAwait(false);
        var status = await processes.RequireAsync("git", ["status", "--porcelain=v1", "--untracked-files=all"], environment, token).ConfigureAwait(false);
        var paths = await processes.RequireAsync("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], environment, token).ConfigureAwait(false);
        var inventory = new StringBuilder();
        var store = new NativeEvidenceStore(files);
        foreach (var path in paths.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var fullPath = files.Path.Combine(root, path);
            inventory.Append(path).Append('\0').Append(files.File.Exists(fullPath)
                ? await store.DigestAsync(fullPath, token).ConfigureAwait(false) : "deleted").Append('\n');
        }
        return new NativeSourceEvidence(commit, status, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(inventory.ToString()))));
    }
}
