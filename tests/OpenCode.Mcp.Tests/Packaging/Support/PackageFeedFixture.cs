using System.IO.Abstractions;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace OpenCode.Mcp.Tests.Packaging.Support;

internal sealed class PackageFeedFixture(IFileSystem files, string? input) : IAsyncDisposable
{
    private readonly List<string> _owned = [];
    private const string Instruction = "Build native-package-feed with pack-root after the native pass and set OPENCODE_MCP_TEST_FEED to its directory.";
    public string RepositoryRoot
    {
        get
        {
            var root = files.Path.GetFullPath(AppContext.BaseDirectory);
            while (!files.File.Exists(files.Path.Combine(root, "OpenCode.Mcp.slnx")))
            {
                root = files.Path.GetDirectoryName(files.Path.TrimEndingDirectorySeparator(root)) ?? throw new InvalidOperationException("Cannot locate OpenCode.Mcp.slnx above the test executable.");
            }
            return root;
        }
    }

    public string Resolve()
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("OPENCODE_MCP_TEST_FEED is absent or blank. " + Instruction);
        }
        var path = files.Path.GetFullPath(input, RepositoryRoot);
        if (!files.Directory.Exists(path))
        {
            throw new InvalidOperationException("OPENCODE_MCP_TEST_FEED directory does not exist: " + path + ". " + Instruction);
        }
        return path;
    }

    public Task<string> CopyAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var feed = Resolve();
        var copy = files.Path.Combine(files.Path.GetTempPath(), "opencode-mcp-feed-" + Guid.NewGuid().ToString("N"));
        _owned.Add(copy);
        files.Directory.CreateDirectory(copy);
        foreach (var path in files.Directory.EnumerateFiles(feed, "*", SearchOption.AllDirectories))
        {
            var destination = files.Path.Combine(copy, files.Path.GetRelativePath(feed, path));
            files.Directory.CreateDirectory(files.Path.GetDirectoryName(destination) ?? throw new InvalidOperationException());
            files.File.Copy(path, destination);
        }
        return Task.FromResult(copy);
    }

    public async Task<byte[]> ManifestBytesAsync(string path, CancellationToken token)
    {
        using var bytes = new MemoryStream(await files.File.ReadAllBytesAsync(path, token));
        await using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
        await using var content = await (archive.GetEntry(".mcp/server.json") ?? throw new InvalidOperationException("Missing packed manifest.")).OpenAsync(token);
        using var result = new MemoryStream();
        await content.CopyToAsync(result, token);
        return result.ToArray();
    }

    public async Task MutateReceiptAsync(string directory, string rid, string fault, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(rid);
        ArgumentNullException.ThrowIfNull(fault);
        var path = files.Path.Combine(directory, "evidence", rid, "receipt.json");
        var receipt = JsonNode.Parse(await files.File.ReadAllTextAsync(path, token)) ?? throw new InvalidOperationException();
        receipt[fault == "source" ? "sourceCommit" : "commonInputsSha256"] = "different";
        await files.File.WriteAllTextAsync(path, receipt.ToJsonString(), token);
    }

    public async Task MutatePackageAsync(string path, string fault, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(fault);
        using var bytes = new MemoryStream();
        await bytes.WriteAsync(await files.File.ReadAllBytesAsync(path, token), token);
        await using (var archive = new ZipArchive(bytes, ZipArchiveMode.Update, leaveOpen: true))
        {
            if (fault is "type" or "version")
            {
                var entry = archive.Entries.Single(static entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
                var document = await ReadXmlAsync(entry, token);
                var root = document.Root ?? throw new InvalidOperationException();
                if (fault == "type")
                {
                    document.Descendants(root.Name.Namespace + "packageType").First().SetAttributeValue("name", "wrong");
                }
                else
                {
                    document.Descendants(root.Name.Namespace + "version").Single().SetValue("wrong");
                }
                await ReplaceAsync(archive, entry, entry.FullName, Encoding.UTF8.GetBytes(document.ToString()), token);
            }
            else
            {
                await MutateContentAsync(archive, fault, token);
            }
        }
        await files.File.WriteAllBytesAsync(path, bytes.ToArray(), token);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var directory in _owned)
        {
            files.Directory.Delete(directory, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    private static async Task MutateContentAsync(ZipArchive archive, string fault, CancellationToken token)
    {
        if (fault == "manifest path")
        {
            var entry = archive.GetEntry(".mcp/server.json") ?? throw new InvalidOperationException();
            await ReplaceAsync(archive, entry, "other/server.json", await ReadAsync(entry, token), token);
            return;
        }
        var settings = archive.Entries.Single(static entry => entry.Name == "DotnetToolSettings.xml");
        if (fault == "any")
        {
            var document = await ReadXmlAsync(settings, token);
            document.Descendants("RuntimeIdentifierPackage").Last().AddAfterSelf(new XElement("RuntimeIdentifierPackage",
                new XAttribute("RuntimeIdentifier", "any"), new XAttribute("Id", "OpenCodeDotNet.Mcp.any")));
            await ReplaceAsync(archive, settings, settings.FullName, Encoding.UTF8.GetBytes(document.ToString()), token);
            return;
        }
        var prefix = settings.FullName[..(settings.FullName.LastIndexOf('/') + 1)];
        var payload = archive.GetEntry(prefix + "THIRD-PARTY-NOTICES.md") ?? throw new InvalidOperationException();
        if (fault == "missing payload")
        {
            payload.Delete();
        }
        else
        {
            var content = await ReadAsync(payload, token);
            content[0] ^= 32;
            await ReplaceAsync(archive, payload, payload.FullName, content, token);
        }
    }

    private static async Task<byte[]> ReadAsync(ZipArchiveEntry entry, CancellationToken token)
    {
        await using var content = await entry.OpenAsync(token);
        using var bytes = new MemoryStream();
        await content.CopyToAsync(bytes, token);
        return bytes.ToArray();
    }

    private static async Task<XDocument> ReadXmlAsync(ZipArchiveEntry entry, CancellationToken token)
    {
        await using var content = await entry.OpenAsync(token);
        return await XDocument.LoadAsync(content, LoadOptions.None, token);
    }

    private static async Task ReplaceAsync(ZipArchive archive, ZipArchiveEntry entry, string name, byte[] bytes, CancellationToken token)
    {
        entry.Delete();
        await using var content = await archive.CreateEntry(name).OpenAsync(token);
        await content.WriteAsync(bytes, token);
    }
}
