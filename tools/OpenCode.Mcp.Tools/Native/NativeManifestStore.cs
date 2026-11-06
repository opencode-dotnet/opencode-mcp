using System.IO.Abstractions;
using System.Text.Json;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeManifestStore(IFileSystem files)
{
    public async Task<NativeManifest> ReadAsync(string path, CancellationToken token)
    {
        try
        {
            using var document = JsonDocument.Parse(await files.File.ReadAllTextAsync(path, token).ConfigureAwait(false));
            NativeManifestPolicy.Validate(document.RootElement);
            return document.RootElement.Deserialize<NativeManifest>(JsonSerializerOptions.Web)
                ?? throw new InvalidOperationException("Manifest: document is null.");
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("Manifest: malformed JSON or field type.", error);
        }
    }
}
