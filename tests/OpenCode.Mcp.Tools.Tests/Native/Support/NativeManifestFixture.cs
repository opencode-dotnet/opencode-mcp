using System.IO.Abstractions;
using System.Text.Json.Nodes;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal sealed class NativeManifestFixture
{
    public JsonObject Document { get; } = new()
    {
        ["schemaVersion"] = 1,
        ["builderImage"] = "registry.test/builder@sha256:" + new string('a', 64),
        ["runtimeImage"] = "registry.test/runtime@sha256:" + new string('b', 64),
        ["runtimes"] = new JsonArray(
            Runtime("win-x64", "windows-2025"), Runtime("win-arm64", "windows-11-arm"),
            Runtime("linux-x64", "ubuntu-24.04"), Runtime("linux-arm64", "ubuntu-24.04-arm"),
            Runtime("osx-x64", "macos-15-intel"), Runtime("osx-arm64", "macos-26")),
    };

    public async Task<string> WriteAsync(IFileSystem files, CancellationToken token)
    {
        var path = files.Path.Combine(files.Path.GetTempPath(), "native-manifest.json");
        files.Directory.CreateDirectory(files.Path.GetTempPath());
        await files.File.WriteAllTextAsync(path, Document.ToJsonString(), token);
        return path;
    }

    private static JsonObject Runtime(string rid, string runner) => new() { ["rid"] = rid, ["runner"] = runner };
}
