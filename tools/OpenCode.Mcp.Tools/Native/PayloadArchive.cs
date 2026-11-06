using System.Formats.Tar;
using System.IO.Abstractions;
using System.IO.Compression;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class PayloadArchive(IFileSystem files, IUnixFileModes modes, NativeRid rid)
{
    public async Task CreateAsync(string directory, string path, IReadOnlyList<PayloadFile> inventory, CancellationToken token)
    {
        using var archiveBytes = new MemoryStream();
        if (path.EndsWith(".zip", StringComparison.Ordinal))
        {
            await CreateZipAsync(directory, inventory, archiveBytes, token).ConfigureAwait(false);
        }
        else
        {
            await CreateTarAsync(directory, inventory, archiveBytes, token).ConfigureAwait(false);
        }
        await files.File.WriteAllBytesAsync(path, archiveBytes.ToArray(), token).ConfigureAwait(false);
    }

    public async Task ExtractAsync(string path, string directory, CancellationToken token)
    {
        files.Directory.CreateDirectory(directory);
        using var archiveBytes = new MemoryStream(await files.File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
        if (path.EndsWith(".zip", StringComparison.Ordinal))
        {
            var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Read);
            await using (archive.ConfigureAwait(false))
            {
                foreach (var entry in archive.Entries)
                {
                    var content = await entry.OpenAsync(token).ConfigureAwait(false);
                    await using (content.ConfigureAwait(false))
                    {
                        await ExtractFileAsync(directory, entry.FullName, content, entry.ExternalAttributes >> 16, token).ConfigureAwait(false);
                    }
                }
            }
        }
        else
        {
            var compressed = new GZipStream(archiveBytes, CompressionMode.Decompress);
            await using (compressed.ConfigureAwait(false))
            {
                var reader = new TarReader(compressed);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.GetNextEntryAsync(cancellationToken: token).ConfigureAwait(false) is { } entry)
                    {
                        if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null)
                        {
                            throw new InvalidOperationException("Unexpected archive entry: " + entry.Name);
                        }
                        await ExtractFileAsync(directory, entry.Name, entry.DataStream, (int)entry.Mode, token).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private async Task CreateZipAsync(string directory, IReadOnlyList<PayloadFile> inventory, Stream destination, CancellationToken token)
    {
        var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        await using (archive.ConfigureAwait(false))
        {
            foreach (var file in inventory)
            {
                var entry = archive.CreateEntry(file.Path);
                entry.ExternalAttributes = (file.UnixMode ?? 0) << 16;
                var content = await entry.OpenAsync(token).ConfigureAwait(false);
                await using (content.ConfigureAwait(false))
                {
                    await content.WriteAsync(await files.File.ReadAllBytesAsync(files.Path.Combine(directory, file.Path), token).ConfigureAwait(false), token).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task CreateTarAsync(string directory, IReadOnlyList<PayloadFile> inventory, Stream destination, CancellationToken token)
    {
        var compressed = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        await using (compressed.ConfigureAwait(false))
        {
            var writer = new TarWriter(compressed, leaveOpen: true);
            await using (writer.ConfigureAwait(false))
            {
                foreach (var file in inventory)
                {
                    using var content = new MemoryStream(await files.File.ReadAllBytesAsync(files.Path.Combine(directory, file.Path), token).ConfigureAwait(false));
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, file.Path) { DataStream = content };
                    if (file.UnixMode is { } mode)
                    {
                        entry.Mode = (UnixFileMode)mode;
                    }
                    await writer.WriteEntryAsync(entry, token).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ExtractFileAsync(string directory, string name, Stream content, int mode, CancellationToken token)
    {
        var destination = files.Path.GetFullPath(files.Path.Combine(directory, name));
        var root = files.Path.GetFullPath(directory).TrimEnd(files.Path.DirectorySeparatorChar) + files.Path.DirectorySeparatorChar;
        if (!destination.StartsWith(root, StringComparison.Ordinal) || files.File.Exists(destination))
        {
            throw new InvalidOperationException("Unsafe or duplicate archive path: " + name);
        }
        files.Directory.CreateDirectory(files.Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Archive entry needs a parent."));
        using var bytes = new MemoryStream();
        await content.CopyToAsync(bytes, token).ConfigureAwait(false);
        await files.File.WriteAllBytesAsync(destination, bytes.ToArray(), token).ConfigureAwait(false);
        if (!rid.IsWindows && mode != 0)
        {
            modes.Write(destination, (UnixFileMode)mode);
        }
    }
}
