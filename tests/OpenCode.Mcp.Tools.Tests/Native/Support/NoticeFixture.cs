using OpenCode.Mcp.Tools.Native;
using Testably.Abstractions.Testing;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal sealed class NoticeFixture
{
    public MockFileSystem Files { get; } = new();
    public string Root => Files.Path.GetTempPath();
    public static IReadOnlyList<DependencyNotice> Packages =>
        [new DependencyNotice("Polly.Core", "8.4.2", "polly-content", "polly.core/8.4.2")];
    public const string PollyNotice = "Copyright (c) 2024, App vNext\nRedistribution conditions and disclaimer from the owned fixture.";

    public async Task PrepareAsync(CancellationToken token)
    {
        Files.Directory.CreateDirectory(Root);
        await Files.File.WriteAllTextAsync(Files.Path.Combine(Root, "THIRD-PARTY-NOTICES.md"), "Repository derivation notices.\n", token);
    }

    public async Task AddPollyAsync(CancellationToken token) => await Files.File.AppendAllTextAsync(Files.Path.Combine(Root, "THIRD-PARTY-NOTICES.md"),
        "<!-- native-package: Polly.Core/8.4.2 -->\n" + PollyNotice + "\n", token);
}
