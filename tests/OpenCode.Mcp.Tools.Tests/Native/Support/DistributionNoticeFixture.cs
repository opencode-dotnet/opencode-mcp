using System.IO.Abstractions;
using System.Text.Json.Nodes;
using Testably.Abstractions.Testing;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal sealed class DistributionNoticeFixture
{
    public IFileSystem Files { get; }
    public string Root => Files.Path.Combine(Files.Path.GetTempPath(), "packages");
    public JsonObject Assets { get; }
    public string Package => Files.Path.Combine(Root, "microsoft.netcore.app.runtime.win-x64", "10.0.14");

    public DistributionNoticeFixture(IFileSystem? files = null)
    {
        Files = files ?? new MockFileSystem();
        Assets = AssetsFixture.Create();
        Assets["packageFolders"] = new JsonObject { [Root] = new JsonObject() };
        Assets["project"] = new JsonObject
        {
            ["frameworks"] = new JsonObject
            {
                ["net10.0"] = new JsonObject
                {
                    ["frameworkReferences"] = new JsonObject { ["Microsoft.NETCore.App"] = new JsonObject() },
                    ["downloadDependencies"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "Microsoft.NETCore.App.Runtime.win-x64",
                        ["version"] = "[10.0.14, 10.0.14]",
                    }),
                },
            },
        };
    }

    public async Task PrepareAsync(CancellationToken token)
    {
        Files.Directory.CreateDirectory(Package);
        await Files.File.WriteAllTextAsync(Files.Path.Combine(Package, "LICENSE.txt"), "framework license", token);
        await Files.File.WriteAllTextAsync(Files.Path.Combine(Package, "THIRD-PARTY-NOTICES.TXT"), "framework third-party notices", token);
        await Files.File.WriteAllTextAsync(Files.Path.Combine(Package, "microsoft.netcore.app.runtime.win-x64.10.0.14.nupkg.sha512"), "framework-content", token);
        foreach (var path in new[] { "polly.core/8.4.2", "modelcontextprotocol/2.3.1" })
        {
            Files.Directory.CreateDirectory(Files.Path.Combine(Root, path));
        }
    }
}
