using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenCode.Mcp.Tools.Native;

internal static partial class NativeManifestPolicy
{
    public static void Validate(JsonElement document)
    {
        CheckFields(document, ["schemaVersion", "builderImage", "runtimeImage", "runtimes"]);
        if (!document.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var value) || value != 1)
        {
            throw new InvalidOperationException("Manifest: schemaVersion must be 1.");
        }
        foreach (var name in new[] { "builderImage", "runtimeImage" })
        {
            if (!document.TryGetProperty(name, out var image) || image.ValueKind != JsonValueKind.String || !DigestImage().IsMatch(image.GetString() ?? ""))
            {
                throw new InvalidOperationException($"Manifest: {name} must be digest-qualified (sha256).");
            }
        }
        if (!document.TryGetProperty("runtimes", out var runtimes) || runtimes.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Manifest: runtimes must be an array.");
        }
        var rids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var runtime in runtimes.EnumerateArray())
        {
            CheckFields(runtime, ["rid", "runner"]);
            var rid = Text(runtime, "rid");
            if (!RidShape().IsMatch(rid))
            {
                throw new InvalidOperationException("Manifest: unknown RID shape " + rid + ".");
            }
            if (!rids.Add(rid))
            {
                throw new InvalidOperationException("Manifest: duplicate RID " + rid + ".");
            }
            if (string.IsNullOrWhiteSpace(Text(runtime, "runner")))
            {
                throw new InvalidOperationException("Manifest: runner is required for " + rid + ".");
            }
        }
    }

    private static void CheckFields(JsonElement element, IReadOnlyList<string> known)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Manifest: expected an object.");
        }
        var unknown = element.EnumerateObject().Select(static property => property.Name).FirstOrDefault(name => !known.Contains(name, StringComparer.Ordinal));
        if (unknown is not null)
        {
            throw new InvalidOperationException($"Manifest: unknown field {unknown}.");
        }
    }

    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? "" : "";

    [GeneratedRegex(@"^[^\s@]+@sha256:[a-fA-F0-9]{64}$", RegexOptions.None, Timeout.Infinite)]
    private static partial Regex DigestImage();

    [GeneratedRegex(@"^(?:win|linux|osx)-(?:x64|arm64)$", RegexOptions.None, Timeout.Infinite)]
    private static partial Regex RidShape();
}
