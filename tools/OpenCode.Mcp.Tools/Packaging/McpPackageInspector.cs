using System.IO.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
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
        var entries = archive.Entries.Where(static entry => !entry.FullName.EndsWith('/'))
            .Select(static entry => (Entry: entry, Path: Uri.UnescapeDataString(entry.FullName))).ToArray();
        var duplicates = entries.GroupBy(static entry => entry.Path, StringComparer.Ordinal).Where(static group => group.Skip(1).Any())
            .Select(static group => "Duplicate package path: " + group.Key).Order(StringComparer.Ordinal).ToArray();
        NativeDiagnostics.Refuse(duplicates);
        var nuspecEntry = Unique(entries.Where(static entry => entry.Path.EndsWith(".nuspec", StringComparison.Ordinal)), "nuspec");
        var settingsEntry = Unique(entries.Where(static entry => entry.Path[(entry.Path.LastIndexOf('/') + 1)..] == "DotnetToolSettings.xml"), "DotnetToolSettings.xml");
        var manifestEntry = Unique(entries.Where(static entry => entry.Path[(entry.Path.LastIndexOf('/') + 1)..] == "server.json"), "server.json");
        var nuspec = await ReadEntryAsync(nuspecEntry.Entry, nuspecEntry.Path, static (stream, cancellation) => XDocument.LoadAsync(stream, LoadOptions.None, cancellation), token).ConfigureAwait(false);
        var root = nuspec.Elements().Single();
        var ns = root.Name.Namespace;
        var metadata = root.Element(ns + "metadata") ?? throw new InvalidOperationException("Package: missing nuspec metadata.");
        var settings = await ReadEntryAsync(settingsEntry.Entry, settingsEntry.Path, static (stream, cancellation) => XDocument.LoadAsync(stream, LoadOptions.None, cancellation), token).ConfigureAwait(false);
        var command = Unique(settings.Descendants("Command"), "Command");
        using var manifest = await ReadEntryAsync(manifestEntry.Entry, manifestEntry.Path, static (stream, cancellation) => JsonDocument.ParseAsync(stream, cancellationToken: cancellation), token).ConfigureAwait(false);
        var prefix = settingsEntry.Path[..(settingsEntry.Path.LastIndexOf('/') + 1)];
        var payload = new List<PayloadFile>();
        foreach (var entry in entries.Where(entry => entry != settingsEntry && entry.Path.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal))
        {
            payload.Add(await ReadPayloadAsync(entry.Entry, entry.Path[prefix.Length..], token).ConfigureAwait(false));
        }
        return new PackageInspection
        {
            Id = metadata.Element(ns + "id")?.Value ?? "",
            Version = metadata.Element(ns + "version")?.Value ?? "",
            PackageTypes = metadata.Element(ns + "packageTypes")?.Elements(ns + "packageType").Select(static type => type.Attribute("name")?.Value ?? "").ToArray() ?? [],
            SettingsPath = settingsEntry.Path,
            CommandName = command.Attribute("Name")?.Value ?? "",
            Runner = command.Attribute("Runner")?.Value ?? "",
            EntryPoint = command.Attribute("EntryPoint")?.Value ?? "",
            ManifestPath = manifestEntry.Path,
            Manifest = manifest.RootElement.Clone(),
            ManifestSize = manifestEntry.Entry.Length,
            Payload = payload.AsReadOnly(),
            Paths = [.. entries.Select(static entry => entry.Path).Order(StringComparer.Ordinal)],
            RepositoryCommit = metadata.Element(ns + "repository")?.Attribute("commit")?.Value,
        };
    }

    private static T Unique<T>(IEnumerable<T> entries, string name)
    {
        var matches = entries.ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException("Package: expected exactly one " + name + ".");
    }

    private static async Task<T> ReadEntryAsync<T>(ZipArchiveEntry entry, string path, Func<Stream, CancellationToken, Task<T>> read, CancellationToken token)
    {
        var stream = await entry.OpenAsync(token).ConfigureAwait(false);
        await using var ownedStream = stream.ConfigureAwait(false);
        try
        {
            return await read(stream, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is XmlException or JsonException)
        {
            throw new InvalidOperationException("Package: malformed entry " + path + ".", error);
        }
    }

    private static async Task<PayloadFile> ReadPayloadAsync(ZipArchiveEntry entry, string path, CancellationToken token)
    {
        var stream = await entry.OpenAsync(token).ConfigureAwait(false);
        await using var ownedStream = stream.ConfigureAwait(false);
        return new PayloadFile(path, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), entry.Length, null);
    }
}
