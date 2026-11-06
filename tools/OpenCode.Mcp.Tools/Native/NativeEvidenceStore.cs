using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeEvidenceStore(IFileSystem files)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task WriteAsync<T>(string path, T evidence, CancellationToken token)
    {
        files.Directory.CreateDirectory(files.Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Evidence needs a parent directory."));
        await files.File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, Options) + "\n", token).ConfigureAwait(false);
    }

    public async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        var evidence = JsonSerializer.Deserialize<T>(await files.File.ReadAllTextAsync(path, token).ConfigureAwait(false), Options)
            ?? throw new InvalidOperationException("Missing evidence: " + path);
        var missing = Options.GetTypeInfo(typeof(T)).Properties.FirstOrDefault(property => property.IsRequired && property.Get?.Invoke(evidence) is null);
        if (missing is not null)
        {
            throw new InvalidOperationException("Evidence: required field " + missing.Name + " must not be null.");
        }
        return evidence;
    }

    public async Task<string> DigestAsync(string path, CancellationToken token) =>
        Convert.ToHexStringLower(SHA256.HashData(await files.File.ReadAllBytesAsync(path, token).ConfigureAwait(false)));
}
