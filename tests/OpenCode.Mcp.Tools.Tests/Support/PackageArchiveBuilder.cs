using System.IO.Compression;
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
    public NativeBuildScenario Native { get; set; } = new();
    public MockFileSystem Files => Native.Files;
    public required XDocument Nuspec { get; set; }
    public required XDocument Settings { get; set; }
    public required JsonObject Manifest { get; init; }
    public required PackageIdentity Identity { get; set; }
    public required NativeReceipt Receipt { get; set; }
    public int PackageCount { get; set; } = 1;
    public NativeProcessResult PackResult { get; set; } = new(0, "Packed.", "");
    public Func<CancellationToken, Task>? AfterPack { get; set; }
    public string SettingsPath { get; set; } = "tools/any/win-x64/DotnetToolSettings.xml";
    public string? NuspecPath { get; set; }
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
    public IList<PackageArchiveBuilder> Children { get; } = [];
    public string Feed => Files.Path.Combine(Native.Root, "artifacts", "feed");
    public string ReceiptsDirectory => Files.Path.Combine(Native.Root, "artifacts", "receipts");
    public string PackageFileName => Metadata.Element(Metadata.Name.Namespace + "id")?.Value + "." + Identity.Version + ".nupkg";
    public XElement Metadata => Nuspec.Root?.Element(Nuspec.Root.Name.Namespace + "metadata") ?? throw new InvalidOperationException();
    public XElement Command => Settings.Descendants("Command").Single();

    public IReadOnlyList<PayloadFile> Inventory() => [.. Payload.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
        .Select(static pair => new PayloadFile(pair.Key, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value))), Encoding.UTF8.GetByteCount(pair.Value), null))];

    public static async Task<PackageArchiveBuilder> CreateAsync(CancellationToken token)
    {
        var nuspec = XDocument.Parse(await FixtureAsync("OpenCodeDotNet.Mcp.win-x64.nuspec", token));
        nuspec.Descendants((nuspec.Root ?? throw new InvalidOperationException()).Name.Namespace + "version").Single().SetValue(PackagingData.BuildVersion);
        var manifest = JsonNode.Parse(await FixtureAsync("server.json", token))?.AsObject() ?? throw new InvalidOperationException();
        return new PackageArchiveBuilder
        {
            Nuspec = nuspec,
            Settings = XDocument.Parse(await FixtureAsync("DotnetToolSettings.xml", token)),
            Manifest = manifest,
            Identity = new PackageIdentity("OpenCodeDotNet.Mcp", PackagingData.BuildVersion, "opencode-mcp", ["win-x64"]),
            Receipt = ReceiptFixture.Create(new("win-x64", "windows-2025")) with
            {
                SourceCommit = nuspec.Descendants((nuspec.Root ?? throw new InvalidOperationException()).Name.Namespace + "repository").Single().Attribute("commit")?.Value ?? throw new InvalidOperationException(),
            },
        };
    }

    public static async Task<PackageArchiveBuilder> CreateRootAsync(CancellationToken token)
    {
        var builder = await CreateAsync(token);
        builder.Nuspec = XDocument.Parse(await FixtureAsync("OpenCodeDotNet.Mcp.nuspec", token));
        builder.Metadata.Element(builder.Metadata.Name.Namespace + "version")?.SetValue(PackagingData.BuildVersion);
        builder.Settings = XDocument.Parse(await FixtureAsync("RootDotnetToolSettings.xml", token));
        builder.SettingsPath = "tools/any/any/DotnetToolSettings.xml";
        builder.Payload.Clear();
        var manifest = await new NativeManifestStore(builder.Files).ReadAsync(await new NativeManifestFixture().WriteAsync(builder.Files, token), token);
        builder.Identity = builder.Identity with { RuntimeIdentifiers = [.. manifest.Runtimes.Select(static runtime => runtime.Rid)] };
        builder.Receipt = builder.Receipt with { SourceCommit = builder.Metadata.Element(builder.Metadata.Name.Namespace + "repository")?.Attribute("commit")?.Value ?? throw new InvalidOperationException() };
        return builder;
    }

    public async Task WriteAsync(CancellationToken token, string? destination = null)
    {
        destination ??= Path;
        Files.Directory.CreateDirectory(Files.Path.GetDirectoryName(destination) ?? throw new InvalidOperationException());
        await using var stream = Files.File.Create(destination);
        await using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var id = Nuspec.Descendants().FirstOrDefault(static element => element.Name.LocalName == "id")?.Value ?? (Identity.Id + "." + Receipt.Rid);
        await AddAsync(archive, NuspecPath ?? (id + ".nuspec"), Nuspec.ToString(), token);
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
        Native.EffectiveProperties["Version"] = PackagingData.BuildVersion;
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

    public async Task<NativeBuildRequest> PrepareCompleteAsync(CancellationToken token)
    {
        var request = await Native.PrepareAsync(token);
        var source = await new NativeSourceReader(Files, new NativeProcesses(Native.Runner, request.Root)).ReadAsync(request.Root, token);
        Metadata.Element(Metadata.Name.Namespace + "repository")?.SetAttributeValue("commit", source.Commit);
        Receipt = Receipt with { SourceCommit = source.Commit };
        foreach (var runtime in request.Manifest.Runtimes.Reverse())
        {
            var child = await CreateAsync(token);
            child.Native = Native;
            child.Identity = Identity;
            child.Metadata.Element(child.Metadata.Name.Namespace + "id")?.SetValue(Identity.Id + "." + runtime.Rid);
            child.Metadata.Element(child.Metadata.Name.Namespace + "repository")?.SetAttributeValue("commit", source.Commit);
            child.SettingsPath = "tools/any/" + runtime.Rid + "/DotnetToolSettings.xml";
            var rid = new NativeRid(runtime.Rid);
            child.Command.SetAttributeValue("EntryPoint", rid.CommandName);
            child.Payload.Remove("opencode-mcp.exe");
            child.Payload.Add(rid.CommandName, "native");
            child.Receipt = ReceiptFixture.Create(runtime) with
            {
                Version = Identity.Version,
                SourceCommit = source.Commit,
                SdkVersion = "10.0.403",
                CommonInputsSha256 = source.InputsSha256,
                Payload = [.. child.Inventory().Select(file => file with { UnixMode = rid.IsWindows ? null : 493 })],
                ExecutableSha256 = child.Inventory().Single(file => file.Path == rid.CommandName).Sha256,
                Archive = rid.IsWindows ? "payload.zip" : "payload.tar.gz",
            };
            await child.WriteNativeEvidenceAsync(token);
            Children.Add(child);
        }
        var respond = Native.Runner.Response;
        Native.Runner.Response = async (call, cancellation) =>
        {
            if (call.Arguments[0] == "msbuild")
            {
                return new(0, JsonSerializer.Serialize(new { Properties = new { PackageId = Identity.Id, Identity.Version, ToolCommandName = Identity.CommandName, ToolPackageRuntimeIdentifiers = string.Join(';', Identity.RuntimeIdentifiers) } }), "");
            }
            if (call.Arguments[0] != "pack")
            {
                return await respond(call, cancellation);
            }
            for (var index = 0; index < PackageCount; index++)
            {
                await WriteAsync(cancellation, Files.Path.Combine(Feed, index == 0 ? PackageFileName : "extra.nupkg"));
            }
            if (AfterPack is { } afterPack)
            {
                await afterPack(cancellation);
            }
            return PackResult;
        };
        await new NativeEvidenceStore(Files).WriteAsync(Files.Path.Combine(request.Root, "packaging", "native-manifest.json"), request.Manifest, token);
        return request with { Output = ReceiptsDirectory, Host = request.Host with { WorkingDirectory = request.Root } };
    }

    public async Task WriteFeedAsync(CancellationToken token)
    {
        await WriteAsync(token, Files.Path.Combine(Feed, PackageFileName));
        foreach (var child in Children)
        {
            await child.WriteAsync(token, Files.Path.Combine(Feed, child.PackageFileName));
            var evidence = Files.Path.Combine(Feed, "evidence", child.Receipt.Rid);
            Files.Directory.CreateDirectory(evidence);
            foreach (var name in new[] { "receipt.json", "payload-manifest.json" })
            {
                Files.File.Copy(Files.Path.Combine(ReceiptsDirectory, child.Receipt.Rid, name), Files.Path.Combine(evidence, name));
            }
        }
    }

    private async Task WriteNativeEvidenceAsync(CancellationToken token)
    {
        var directory = Files.Path.Combine(ReceiptsDirectory, Receipt.Rid);
        var payload = Files.Path.Combine(directory, "payload");
        Files.Directory.CreateDirectory(payload);
        foreach (var pair in Payload)
        {
            await Files.File.WriteAllTextAsync(Files.Path.Combine(payload, pair.Key), pair.Value, token);
        }
        var store = new NativeEvidenceStore(Files);
        var archive = Files.Path.Combine(directory, Receipt.Archive ?? throw new InvalidOperationException());
        await new PayloadArchive(Files, Native.Modes, new NativeRid(Receipt.Rid)).CreateAsync(payload, archive, Receipt.Payload, token);
        Receipt = Receipt with { ArchiveSha256 = await store.DigestAsync(archive, token) };
        await store.WriteAsync(Files.Path.Combine(directory, "receipt.json"), Receipt, token);
        await store.WriteAsync(Files.Path.Combine(directory, "payload-manifest.json"), Receipt.Payload, token);
        await WriteAsync(token, Files.Path.Combine(directory, "packages", PackageFileName));
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

    public static async Task<string> FixtureAsync(string name, CancellationToken token)
    {
        await using var stream = typeof(PackageArchiveBuilder).Assembly.GetManifestResourceStream("Fixtures." + name) ?? throw new InvalidOperationException(name);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(token);
    }
}
