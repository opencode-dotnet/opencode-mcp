using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using OpenCode.Mcp.Tools.Packaging;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using Testably.Abstractions.Testing;

namespace OpenCode.Mcp.Tools.Tests.Support;

internal sealed class PackageArchiveBuilder
{
    public const string PackWarning = "src/product.csproj(3,5): warning NU5100: fixture [src/product.csproj]";
    public const string PackWarningDiagnostic = "Child pack warning: " + PackWarning;
    public const string PackageCountDiagnostic = "Expected exactly one child package named OpenCodeDotNet.Mcp.win-x64.{version}.nupkg.";
    public const string PackageIdDiagnostic = "Package ID must be OpenCodeDotNet.Mcp.win-x64.";
    public const string PackageVersionDiagnostic = "Package version must be {version}.";
    public const string ManifestNameDiagnostic = "Manifest name must be io.github.opencode-dotnet/opencode-mcp.";
    public const string MissingManifestDiagnostic = "Package: expected exactly one server.json.";

    public static string BuildVersion { get; } = (typeof(PackageIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?? throw new InvalidOperationException("Missing tools informational version.")).InformationalVersion.Split('+')[0];

    public NativeBuildScenario Native { get; } = new();
    public MockFileSystem Files => Native.Files;
    public required XDocument Nuspec { get; init; }
    public required XDocument Settings { get; init; }
    public required JsonObject Manifest { get; init; }
    public required PackageIdentity Identity { get; set; }
    public required NativeReceipt Receipt { get; set; }
    public int PackageCount { get; set; } = 1;
    public NativeProcessResult PackResult { get; set; } = new(0, "Packed.", "");
    public Func<CancellationToken, Task>? AfterPack { get; set; }
    public string SettingsPath { get; set; } = "tools/any/win-x64/DotnetToolSettings.xml";
    public string ManifestPath { get; set; } = ".mcp/server.json";
    public string Path => Files.Path.Combine(Files.Path.GetTempPath(), "child.nupkg");
    public IDictionary<string, string> Payload { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["payload.json"] = "metadata",
        ["opencode-mcp.exe"] = "native",
        ["THIRD-PARTY-NOTICES.md"] = "notices",
        ["LICENSE"] = "license",
    };
    public ISet<string> Omitted { get; } = new HashSet<string>(StringComparer.Ordinal);
    public IDictionary<string, string> AdditionalFiles { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public string? Duplicate { get; set; }
    public string? MalformedEntry { get; set; }
    public int? ManifestSize { get; set; }
    public XElement Metadata => Nuspec.Root?.Element(Nuspec.Root.Name.Namespace + "metadata") ?? throw new InvalidOperationException();
    public XElement Command => Settings.Descendants("Command").Single();

    public IReadOnlyList<PayloadFile> Inventory() => [.. Payload.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
        .Select(static pair => new PayloadFile(pair.Key, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value))), Encoding.UTF8.GetByteCount(pair.Value), null))];

    public static async Task<PackageArchiveBuilder> CreateAsync(CancellationToken token)
    {
        var nuspec = XDocument.Parse(await FixtureAsync("OpenCodeDotNet.Mcp.win-x64.nuspec", token));
        nuspec.Descendants((nuspec.Root ?? throw new InvalidOperationException()).Name.Namespace + "version").Single().SetValue(BuildVersion);
        var manifest = JsonNode.Parse(await FixtureAsync("server.json", token))?.AsObject() ?? throw new InvalidOperationException();
        return new PackageArchiveBuilder
        {
            Nuspec = nuspec,
            Settings = XDocument.Parse(await FixtureAsync("DotnetToolSettings.xml", token)),
            Manifest = manifest,
            Identity = new PackageIdentity("OpenCodeDotNet.Mcp", BuildVersion, "opencode-mcp", ["win-x64"]),
            Receipt = ReceiptFixture.Create(new("win-x64", "windows-2025")) with
            {
                SourceCommit = nuspec.Descendants((nuspec.Root ?? throw new InvalidOperationException()).Name.Namespace + "repository").Single().Attribute("commit")?.Value ?? throw new InvalidOperationException(),
            },
        };
    }

    public async Task WriteAsync(CancellationToken token, string? destination = null)
    {
        destination ??= Path;
        Files.Directory.CreateDirectory(Files.Path.GetDirectoryName(destination) ?? throw new InvalidOperationException());
        await using var stream = Files.File.Create(destination);
        await using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        await AddAsync(archive, "OpenCodeDotNet.Mcp.win-x64.nuspec", Nuspec.ToString(), token);
        await AddAsync(archive, SettingsPath, Settings.ToString(), token);
        var manifest = Manifest.ToJsonString();
        await AddAsync(archive, ManifestPath, ManifestSize is { } size ? manifest.PadRight(size) : manifest, token);
        foreach (var name in new[] { "README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "[Content_Types].xml", "_rels/.rels", "package/services/metadata/core-properties/fixture.psmdcp" })
        {
            await AddAsync(archive, name, name, token);
        }
        foreach (var pair in Payload)
        {
            await AddAsync(archive, SettingsPath[..(SettingsPath.LastIndexOf('/') + 1)] + pair.Key, pair.Value, token);
        }
        foreach (var pair in AdditionalFiles)
        {
            await AddAsync(archive, pair.Key, pair.Value, token);
        }
    }

    public async Task<NativeBuildRequest> PrepareStagedAsync(CancellationToken token)
    {
        var request = await Native.PrepareAsync(token);
        Native.EffectiveProperties["Version"] = BuildVersion;
        var published = await new NativeVerifier(Files, Native.Runner).VerifyAsync(request, token);
        await Native.AddStagingInputsAsync(token);
        Receipt = await new NativeStager(Files, Native.Runner, Native.Modes).StageAsync(request, published.PublishDirectory, token);
        Identity = Identity with { Version = Receipt.Version, RuntimeIdentifiers = [.. request.Manifest.Runtimes.Select(static runtime => runtime.Rid)] };
        Metadata.Element(Metadata.Name.Namespace + "version")?.SetValue(Identity.Version);
        Metadata.Element(Metadata.Name.Namespace + "repository")?.SetAttributeValue("commit", Receipt.SourceCommit);
        Payload.Clear();
        foreach (var file in Receipt.Payload)
        {
            Payload.Add(file.Path, await Files.File.ReadAllTextAsync(Files.Path.Combine(request.Output, "payload", file.Path), token));
        }
        var respond = Native.Runner.Response;
        Native.Runner.Response = async (call, cancellation) =>
        {
            if (call.Arguments[0] == "msbuild" && call.Arguments.Any(static value => value.StartsWith("-getProperty:PackageId", StringComparison.Ordinal)))
            {
                return new(0, JsonSerializer.Serialize(new { Properties = new { PackageId = Identity.Id, Identity.Version, ToolCommandName = Identity.CommandName, ToolPackageRuntimeIdentifiers = string.Join(';', Identity.RuntimeIdentifiers) } }), "");
            }
            if (call.Arguments[0] != "pack")
            {
                return await respond(call, cancellation);
            }
            var output = Files.Path.Combine(request.Output, "packages");
            for (var index = 0; index < PackageCount; index++)
            {
                await WriteAsync(cancellation, Files.Path.Combine(output, index == 0 ? Identity.Id + "." + request.Rid + "." + Identity.Version + ".nupkg" : "second.nupkg"));
            }
            if (AfterPack is { } afterPack)
            {
                await afterPack(cancellation);
            }
            return PackResult;
        };
        return request;
    }

    public async Task BreakStagingAsync(NativeBuildRequest request, string fault, CancellationToken token)
    {
        var store = new NativeEvidenceStore(Files);
        var manifest = Files.Path.Combine(request.Output, "payload-manifest.json");
        var payload = Files.Path.Combine(request.Output, "payload");
        var archive = Files.Path.Combine(request.Output, Receipt.Archive ?? throw new InvalidOperationException());
        switch (fault)
        {
            case "manifest": Files.File.Delete(manifest); break;
            case "manifest content": await store.WriteAsync(manifest, Array.Empty<PayloadFile>(), token); break;
            case "payload": Files.Directory.Delete(payload, recursive: true); break;
            case "payload content": await Files.File.WriteAllTextAsync(Files.Path.Combine(payload, "required.dat"), "REQUIRED ASSET", token); break;
            case "archive": Files.File.Delete(archive); break;
            case "archive digest": await Files.File.WriteAllTextAsync(archive, "changed", token); break;
            case "archive content":
                await new PayloadArchive(Files, Native.Modes, new NativeRid(request.Rid)).CreateAsync(payload, archive, [.. Receipt.Payload.Where(static file => file.Path != "required.dat")], token);
                Receipt = Receipt with { ArchiveSha256 = await store.DigestAsync(archive, token) };
                await store.WriteAsync(Files.Path.Combine(request.Output, "receipt.json"), Receipt, token);
                break;
        }
    }

    private async Task AddAsync(ZipArchive archive, string path, string content, CancellationToken token)
    {
        if (Omitted.Contains(path))
        {
            return;
        }
        await WriteEntryAsync(archive, path, path == MalformedEntry ? "malformed" : content, token);
        if (Duplicate == path)
        {
            await WriteEntryAsync(archive, path, content, token);
        }
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string path, string content, CancellationToken token)
    {
        var entryName = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        await using var writer = new StreamWriter(await archive.CreateEntry(entryName).OpenAsync(token), new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), token);
    }

    private static async Task<string> FixtureAsync(string name, CancellationToken token)
    {
        await using var stream = typeof(PackageArchiveBuilder).Assembly.GetManifestResourceStream("Fixtures." + name) ?? throw new InvalidOperationException(name);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(token);
    }
}
