using System.Security.Cryptography;
using System.Text;
using OpenCode.Mcp.Tools.Native;
using Testably.Abstractions.Testing;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal sealed class PayloadFixture
{
    public MockFileSystem Files { get; } = new();
    public FakeUnixFileModes Modes { get; } = new();
    public NativeRid Rid { get; } = new("win-x64");
    public string Root => Files.Path.Combine(Files.Path.GetTempPath(), "payload");
    public IReadOnlyList<PayloadFile> Inventory { get; }

    public PayloadFixture()
    {
        Files.Directory.CreateDirectory(Root);
        Inventory = [Entry("LICENSE", "license"), Entry("z-asset", "asset")];
    }

    public async Task PrepareAsync(CancellationToken token)
    {
        await ChangeAsync("z-asset", "asset", token);
        await ChangeAsync("LICENSE", "license", token);
    }

    public async Task ChangeAsync(string name, string contents, CancellationToken token) =>
        await Files.File.WriteAllTextAsync(Files.Path.Combine(Root, name), contents, token);

    public void Remove(string name) => Files.File.Delete(Files.Path.Combine(Root, name));

    private static PayloadFile Entry(string name, string contents) => new(name,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contents))), Encoding.UTF8.GetByteCount(contents),
        null);
}
