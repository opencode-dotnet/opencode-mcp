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

    private static JsonObject Runtime(string type) => new()
    {
        ["type"] = type,
        ["runtime"] = new JsonObject { ["lib/net10.0/library.dll"] = new JsonObject() },
    };

    private static JsonObject Library(string path, string sha512) => new() { ["path"] = path, ["sha512"] = sha512 };
}
