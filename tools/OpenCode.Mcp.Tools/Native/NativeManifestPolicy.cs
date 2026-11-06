using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenCode.Mcp.Tools.Native;

internal static partial class NativeManifestPolicy
{
    private static readonly string[] ApprovedMappings =
    [
        "win-x64/windows-2025", "win-arm64/windows-11-arm", "linux-x64/ubuntu-24.04",
        "linux-arm64/ubuntu-24.04-arm", "osx-x64/macos-15-intel", "osx-arm64/macos-26",
    ];

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
        var mappings = new List<string>();
        foreach (var runtime in runtimes.EnumerateArray())
        {
            CheckFields(runtime, ["rid", "runner"]);
            mappings.Add(Text(runtime, "rid") + "/" + Text(runtime, "runner"));
        }
        if (!mappings.SequenceEqual(ApprovedMappings, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Manifest: runtimes must have the approved ordered RID/runner mapping.");
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
}
