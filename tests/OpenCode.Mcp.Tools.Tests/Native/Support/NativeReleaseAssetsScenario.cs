using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Support;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal sealed class NativeReleaseAssetsScenario(PackageArchiveBuilder builder, NativeBuildRequest build)
{
    public PackageArchiveBuilder Builder { get; } = builder;
    public NativeBuildRequest Build { get; } = build;
    public IFileSystem Files => Builder.Files;
    public NativeManifest Manifest { get; set; } = build.Manifest;
    public string Receipts { get; set; } = build.Output;
    public string Evidence => Build.Output;
    public string Output => Files.Path.Combine(Build.Root, "artifacts", "release-assets");
    public string Version => Builder.Identity.Version;
    public NativeReleaseAssetRequest Request => new(Manifest, Receipts, Evidence, Output, Version);
    public FakeNativeProcessRunner Runner { get; } = new();
    public IDictionary<string, byte[]> Archives { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    public static async Task<NativeReleaseAssetsScenario> CreateAsync(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var build = await builder.PrepareCompleteAsync(token);
        var scenario = new NativeReleaseAssetsScenario(builder, build);
        foreach (var child in builder.Children)
        {
            var receipt = child.Receipt;
            var directory = builder.EvidenceDirectory(receipt.Rid);
            var old = builder.Files.Path.Combine(directory, receipt.Archive ?? throw new InvalidOperationException());
            var name = "opencode-mcp-" + receipt.Rid + new NativeRid(receipt.Rid).ArchiveExtension;
            var path = builder.Files.Path.Combine(directory, name);
            builder.Files.File.Move(old, path);
            await scenario.WriteReceiptAsync(receipt with { Archive = name, SourceStatus = " M development", PublishDirectory = "original-machine/published" }, token);
            scenario.Archives.Add(receipt.Rid, await builder.Files.File.ReadAllBytesAsync(path, token));
        }
        return scenario;
    }

    public NativeReceipt Receipt(string rid) => Builder.Children.Single(child => child.Receipt.Rid == rid).Receipt;
    public string Directory(string rid) => Builder.EvidenceDirectory(rid);
    public string Archive(string rid) => Files.Path.Combine(Directory(rid), Receipt(rid).Archive ?? throw new InvalidOperationException());

    public async Task WriteReceiptAsync(NativeReceipt receipt, CancellationToken token)
    {
        Builder.Children.Single(child => child.Receipt.Rid == receipt.Rid).Receipt = receipt;
        await new NativeEvidenceStore(Files).WriteAsync(Files.Path.Combine(Directory(receipt.Rid), "receipt.json"), receipt, token);
    }

    public async Task DetachReceiptsAsync(CancellationToken token)
    {
        Receipts = Files.Path.Combine(Build.Root, "detached");
        var directories = new[] { "z", "a", "y", "b", "x", "c" };
        for (var index = 0; index < Manifest.Runtimes.Count; index++)
        {
            var receipt = Receipt(Manifest.Runtimes[index].Rid);
            await new NativeEvidenceStore(Files).WriteAsync(Files.Path.Combine(Receipts, directories[index], "receipt.json"), receipt, token);
        }
    }

    public string Digest(string rid) => Convert.ToHexStringLower(SHA256.HashData(Archives[rid]));

    public IFileSystem DecorateWrites(string fault)
    {
        var file = Substitute.For<IFile>();
        file.Exists(Arg.Any<string>()).Returns(call => Files.File.Exists(call.Arg<string>()));
        file.ReadAllTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Files.File.ReadAllTextAsync(call.Arg<string>(), call.Arg<CancellationToken>()));
        file.ReadAllBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Files.File.ReadAllBytesAsync(call.Arg<string>(), call.Arg<CancellationToken>()));
        file.WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var path = call.Arg<string>();
            var bytes = call.Arg<byte[]>();
            if (path.EndsWith("-linux-x64.tar.gz", StringComparison.Ordinal))
            {
                if (fault == "archive")
                {
                    throw new IOException("fixture archive write failure");
                }
                if (fault == "changed")
                {
                    bytes = [1, 2, 3];
                }
            }
            await Files.File.WriteAllBytesAsync(path, bytes, call.Arg<CancellationToken>());
        });
        file.WriteAllTextAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Encoding>(), Arg.Any<CancellationToken>()).Returns(call =>
            fault == "checksum" ? Task.FromException(new IOException("fixture checksum write failure"))
                : Files.File.WriteAllTextAsync(call.ArgAt<string>(0), call.ArgAt<string>(1), call.Arg<Encoding>(), call.Arg<CancellationToken>()));
        var decorated = Substitute.For<IFileSystem>();
        decorated.File.Returns(file);
        decorated.Directory.Returns(Files.Directory);
        decorated.Path.Returns(Files.Path);
        decorated.FileStream.Returns(Files.FileStream);
        return decorated;
    }
}
