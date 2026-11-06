using System.IO.Abstractions;
using System.Security.Cryptography;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class PayloadInventory(IFileSystem files, IUnixFileModes modes, NativeRid rid)
{
    public async Task<IReadOnlyList<PayloadFile>> CaptureAsync(string directory, CancellationToken token)
    {
        var inventory = new List<PayloadFile>();
        foreach (var path in files.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var bytes = await files.File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            inventory.Add(new PayloadFile(files.Path.GetRelativePath(directory, path).Replace('\\', '/'),
                Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength,
                rid.IsWindows ? null : (int?)modes.Read(path)));
        }
        return [.. inventory.OrderBy(static file => file.Path, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<string>> CompareAsync(string directory, IReadOnlyList<PayloadFile> expected, CancellationToken token) =>
        Compare(await CaptureAsync(directory, token).ConfigureAwait(false), expected);

    public static IReadOnlyList<string> Compare(IReadOnlyList<PayloadFile> observed, IReadOnlyList<PayloadFile> expected)
    {
        var duplicates = expected.GroupBy(static file => file.Path, StringComparer.Ordinal).Where(static group => group.Skip(1).Any())
            .Select(static group => "Duplicate payload path: " + group.Key).Order(StringComparer.Ordinal).ToArray();
        if (duplicates.Length != 0)
        {
            return duplicates;
        }
        var actual = observed.ToDictionary(static item => item.Path, StringComparer.Ordinal);
        var declared = expected.ToDictionary(static item => item.Path, StringComparer.Ordinal);
        var diagnostics = new List<string>();
        foreach (var path in declared.Keys.Union(actual.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(path, out var file))
            {
                diagnostics.Add("Missing payload file: " + path);
            }
            else if (!declared.TryGetValue(path, out var original))
            {
                diagnostics.Add("Unexpected payload file: " + path);
            }
            else if (file != original)
            {
                diagnostics.Add("Changed payload file: " + path);
            }
        }
        return diagnostics.AsReadOnly();
    }
}
