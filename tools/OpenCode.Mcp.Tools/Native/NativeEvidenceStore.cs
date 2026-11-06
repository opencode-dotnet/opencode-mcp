using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeEvidenceStore(IFileSystem files)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, RespectNullableAnnotations = true };

    public async Task WriteAsync<T>(string path, T evidence, CancellationToken token)
    {
        files.Directory.CreateDirectory(files.Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Evidence needs a parent directory."));
        await files.File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, Options) + "\n", token).ConfigureAwait(false);
    }

    public async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        var evidence = JsonSerializer.Deserialize<T>(await files.File.ReadAllTextAsync(path, token).ConfigureAwait(false), Options)
            ?? throw new InvalidOperationException("Missing evidence: " + path);
        if (evidence is NativeReceipt receipt)
        {
            CheckElements(receipt.Payload, "payload");
            CheckElements(receipt.Properties.Values, "properties");
            CheckElements(receipt.CompilerPackages, "compilerPackages");
            CheckElements(receipt.PublishCommand, "publishCommand");
            CheckElements(receipt.Warnings, "warnings");
            CheckElements(receipt.Dependencies, "dependencies");
        }
        else if (evidence is PayloadFile[] payload)
        {
            CheckElements(payload, "payload manifest");
        }
        return evidence;
    }

    private static void CheckElements<T>(IEnumerable<T> elements, string name)
    {
        if (elements.Any(static element => element is null))
        {
            throw new JsonException("Evidence: " + name + " must not contain null elements.");
        }
    }

    public async Task<string> DigestAsync(string path, CancellationToken token) =>
        Convert.ToHexStringLower(SHA256.HashData(await files.File.ReadAllBytesAsync(path, token).ConfigureAwait(false)));
}
