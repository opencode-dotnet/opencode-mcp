using System.IO.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativePayloadNormalizer(IFileSystem files)
{
    public async Task<string> NormalizeAsync(string output, NativeReceipt receipt, CancellationToken token)
    {
        var payload = files.Path.Combine(output, "payload");
        if (files.Directory.Exists(payload))
        {
            files.Directory.Delete(payload, recursive: true);
        }
        files.Directory.CreateDirectory(payload);
        var executableName = receipt.Rid.StartsWith("win-", StringComparison.Ordinal) ? "opencode-mcp.exe" : "opencode-mcp";
        foreach (var source in files.Directory.EnumerateFiles(receipt.PublishDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = files.Path.GetRelativePath(receipt.PublishDirectory, source);
            if (IsSymbol(relative))
            {
                continue;
            }
            var target = files.Path.Combine(payload, string.Equals(source, receipt.Executable, StringComparison.Ordinal) ? executableName : relative);
            files.Directory.CreateDirectory(files.Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Payload needs a parent."));
            await files.File.WriteAllBytesAsync(target, await files.File.ReadAllBytesAsync(source, token).ConfigureAwait(false), token).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                files.File.SetUnixFileMode(target, files.File.GetUnixFileMode(source));
            }
        }
        return payload;
    }

    private static bool IsSymbol(string path) => path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase) || path.Replace('\\', '/').Contains(".dSYM/", StringComparison.Ordinal);
}
