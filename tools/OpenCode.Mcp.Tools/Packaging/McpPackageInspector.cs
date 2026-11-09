using System.IO.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using OpenCode.Mcp.Tools.Native;

namespace OpenCode.Mcp.Tools.Packaging;

internal sealed class McpPackageInspector(IFileSystem files)
{
    public async Task<PackageInspection> InspectAsync(string packagePath, CancellationToken token)
    {
        try
        {
            return await InspectArchiveAsync(packagePath, token).ConfigureAwait(false);
        }
        catch (InvalidDataException error)
        {
            throw new InvalidOperationException("Package: invalid archive " + packagePath + ".", error);
        }
    }

    private async Task<PackageInspection> InspectArchiveAsync(string packagePath, CancellationToken token)
    {
        var stream = files.File.OpenRead(packagePath);
        await using var ownedStream = stream.ConfigureAwait(false);
        var archive = await ZipArchive.CreateAsync(stream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: null, token).ConfigureAwait(false);
        await using var ownedArchive = archive.ConfigureAwait(false);
        var entries = archive.Entries.Where(static entry => !entry.FullName.EndsWith('/')).ToArray();
        var duplicates = entries.GroupBy(static entry => entry.FullName, StringComparer.Ordinal).Where(static group => group.Skip(1).Any())
            .Select(static group => "Duplicate package path: " + group.Key).Order(StringComparer.Ordinal).ToArray();
        NativeDiagnostics.Refuse(duplicates);
        var nuspecEntry = Unique(entries.Where(static entry => entry.Name.EndsWith(".nuspec", StringComparison.Ordinal)), "nuspec");
        var settingsEntry = Unique(entries.Where(static entry => entry.Name == "DotnetToolSettings.xml"), "DotnetToolSettings.xml");
        var manifestEntry = Unique(entries.Where(static entry => entry.Name == "server.json"), "server.json");
        var nuspecStream = await nuspecEntry.OpenAsync(token).ConfigureAwait(false);
        await using var ownedNuspec = nuspecStream.ConfigureAwait(false);
        var nuspec = await XDocument.LoadAsync(nuspecStream, LoadOptions.None, token).ConfigureAwait(false);
        var ns = nuspec.Root?.Name.Namespace ?? throw new InvalidOperationException("Package: missing nuspec root.");
        var metadata = nuspec.Root.Element(ns + "metadata") ?? throw new InvalidOperationException("Package: missing nuspec metadata.");
        var settingsStream = await settingsEntry.OpenAsync(token).ConfigureAwait(false);
        await using var ownedSettings = settingsStream.ConfigureAwait(false);
        var settings = await XDocument.LoadAsync(settingsStream, LoadOptions.None, token).ConfigureAwait(false);
        var command = settings.Descendants("Command").Single();
        var manifestStream = await manifestEntry.OpenAsync(token).ConfigureAwait(false);
        await using var ownedManifest = manifestStream.ConfigureAwait(false);
        using var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: token).ConfigureAwait(false);
        var prefix = settingsEntry.FullName[..(settingsEntry.FullName.LastIndexOf('/') + 1)];
        var payload = new List<PayloadFile>();
        foreach (var entry in entries.Where(entry => entry != settingsEntry && entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(static entry => entry.FullName, StringComparer.Ordinal))
        {
            payload.Add(await ReadPayloadAsync(entry, prefix, token).ConfigureAwait(false));
        }
        return new PackageInspection
        {
            Id = metadata.Element(ns + "id")?.Value ?? "",
            Version = metadata.Element(ns + "version")?.Value ?? "",
            PackageTypes = metadata.Element(ns + "packageTypes")?.Elements(ns + "packageType").Select(static type => type.Attribute("name")?.Value ?? "").ToArray() ?? [],
            SettingsPath = settingsEntry.FullName,
            CommandName = command.Attribute("Name")?.Value ?? "",
            Runner = command.Attribute("Runner")?.Value ?? "",
            EntryPoint = command.Attribute("EntryPoint")?.Value ?? "",
            ManifestPath = manifestEntry.FullName,
            Manifest = manifest.RootElement.Clone(),
            ManifestSize = manifestEntry.Length,
            Payload = payload.AsReadOnly(),
            Paths = [.. entries.Select(static entry => entry.FullName).Order(StringComparer.Ordinal)],
            RepositoryCommit = metadata.Element(ns + "repository")?.Attribute("commit")?.Value,
        };
    }

    private static ZipArchiveEntry Unique(IEnumerable<ZipArchiveEntry> entries, string name)
    {
        var matches = entries.ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException("Package: expected exactly one " + name + ".");
    }

    private static async Task<PayloadFile> ReadPayloadAsync(ZipArchiveEntry entry, string prefix, CancellationToken token)
    {
        var stream = await entry.OpenAsync(token).ConfigureAwait(false);
        await using var ownedStream = stream.ConfigureAwait(false);
        return new PayloadFile(entry.FullName[prefix.Length..], Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), entry.Length, null);
    }
}
