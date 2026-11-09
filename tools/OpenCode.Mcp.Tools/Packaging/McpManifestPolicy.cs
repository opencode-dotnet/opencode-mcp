using System.Text.Json;

namespace OpenCode.Mcp.Tools.Packaging;

internal static class McpManifestPolicy
{
    public static IReadOnlyList<string> Diagnostics(JsonElement manifest, long size, PackageIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var diagnostics = new List<string>();
        CheckText(manifest, "name", "io.github.opencode-dotnet/opencode-mcp", diagnostics);
        if (Text(Member(manifest, "description")) is not { Length: >= 1 and <= 100 })
        {
            diagnostics.Add("Manifest description must have 1 to 100 characters.");
        }
        CheckText(manifest, "version", identity.Version, diagnostics);
        var packages = Member(manifest, "packages");
        if (packages.ValueKind != JsonValueKind.Array || packages.GetArrayLength() != 1)
        {
            diagnostics.Add("Manifest must contain exactly one package.");
        }
        else
        {
            var package = packages[0];
            if (Text(Member(package, "version")) != identity.Version)
            {
                diagnostics.Add("Manifest package version must be " + identity.Version + ".");
            }
            CheckText(package, "registryType", "nuget", diagnostics);
            CheckText(package, "identifier", identity.Id, diagnostics);
            CheckText(package, "registryBaseUrl", "https://api.nuget.org/v3/index.json", diagnostics);
            CheckText(package, "runtimeHint", "dnx", diagnostics);
            CheckArguments(package, "runtimeArguments", "-v", "minimal", diagnostics);
            if (Text(Member(Member(package, "transport"), "type")) != "stdio")
            {
                diagnostics.Add("Manifest transport.type must be stdio.");
            }
            CheckArguments(package, "packageArguments", "--transport", "stdio", diagnostics);
        }
        if (size >= 20000)
        {
            diagnostics.Add("Manifest UTF-8 size must be under 20000 bytes.");
        }
        return diagnostics.AsReadOnly();
    }

    private static JsonElement Member(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) ? member : default;

    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void CheckText(JsonElement value, string name, string expected, List<string> diagnostics)
    {
        if (Text(Member(value, name)) != expected)
        {
            diagnostics.Add($"Manifest {name} must be {expected}.");
        }
    }

    private static void CheckArguments(JsonElement package, string field, string name, string value, List<string> diagnostics)
    {
        var arguments = Member(package, field);
        if (arguments.ValueKind != JsonValueKind.Array || arguments.GetArrayLength() != 1
            || Text(Member(arguments[0], "type")) != "named" || Text(Member(arguments[0], "name")) != name || Text(Member(arguments[0], "value")) != value)
        {
            diagnostics.Add($"Manifest {field} must be exactly named {name}={value}.");
        }
    }
}
