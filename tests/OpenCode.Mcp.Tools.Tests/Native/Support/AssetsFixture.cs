using System.Text.Json.Nodes;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal static class AssetsFixture
{
    public static JsonObject Create() => new()
    {
        ["packageFolders"] = new JsonObject { ["packages/"] = new JsonObject() },
        ["targets"] = new JsonObject
        {
            ["net10.0/win-x64"] = new JsonObject
            {
                ["Polly.Core/8.4.2"] = Runtime("package"),
                ["Analyzer/1.0.0"] = new JsonObject { ["type"] = "package", ["build"] = new JsonObject() },
                ["ServiceDefaults/fixture"] = Runtime("project"),
                ["ModelContextProtocol/2.3.1"] = Runtime("package"),
            },
        },
        ["libraries"] = new JsonObject
        {
            ["Polly.Core/8.4.2"] = Library("polly.core/8.4.2", "polly-content"),
            ["ModelContextProtocol/2.3.1"] = Library("modelcontextprotocol/2.3.1", "mcp-content"),
        },
    };

    public static JsonObject WithRuntimeAssetKinds()
    {
        var assets = Create();
        var target = assets["targets"]?["net10.0/win-x64"]?.AsObject() ?? throw new InvalidOperationException();
        var libraries = assets["libraries"]?.AsObject() ?? throw new InvalidOperationException();
        foreach (var kind in new[] { "runtimeTargets", "native", "runtime" })
        {
            var name = kind + "/1.0.0";
            target[name] = new JsonObject { ["type"] = "package", [kind] = new JsonObject { ["lib/asset"] = new JsonObject() } };
            libraries[name] = Library(kind + "/1.0.0", kind + "-content");
        }
        target["Placeholder/1.0.0"] = new JsonObject { ["type"] = "package", ["runtime"] = new JsonObject { ["lib/net10.0/_._"] = new JsonObject() } };
        libraries["Placeholder/1.0.0"] = Library("placeholder/1.0.0", "placeholder-content");
        return assets;
    }

    private static JsonObject Runtime(string type) => new()
    {
        ["type"] = type,
        ["runtime"] = new JsonObject { ["lib/net10.0/library.dll"] = new JsonObject() },
    };

    private static JsonObject Library(string path, string sha512) => new() { ["path"] = path, ["sha512"] = sha512 };
}
