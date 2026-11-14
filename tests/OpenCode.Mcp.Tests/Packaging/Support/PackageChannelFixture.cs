using System.IO.Abstractions;
using System.Text.Json;
using System.Xml.Linq;

namespace OpenCode.Mcp.Tests.Packaging.Support;

internal sealed class PackageChannelFixture(IFileSystem files, IReadOnlyDictionary<string, string?> inputs)
{
    public PackageFeedFixture ReferenceFeed { get; } = new(files, inputs.GetValueOrDefault("OPENCODE_MCP_TEST_FEED"));
    public async Task<string> ResolveAsync(CancellationToken token)
    {
        var errors = new List<string>();
        foreach (var name in new[] { "OPENCODE_MCP_TEST_CHANNEL", "OPENCODE_MCP_TEST_CHANNEL_CONFIG", "OPENCODE_MCP_TEST_FEED", "OPENCODE_MCP_TEST_RID" })
        {
            if (string.IsNullOrWhiteSpace(inputs.GetValueOrDefault(name)))
            {
                errors.Add(name + " is absent or blank.");
            }
        }
        if (errors.Count != 0)
        {
            throw new InvalidOperationException(string.Join('\n', errors));
        }
        var channel = inputs["OPENCODE_MCP_TEST_CHANNEL"];
        if (channel is not ("github-packages" or "nuget"))
        {
            errors.Add("OPENCODE_MCP_TEST_CHANNEL must be github-packages or nuget: " + channel);
        }
        var path = files.Path.GetFullPath(inputs["OPENCODE_MCP_TEST_CHANNEL_CONFIG"] ?? throw new InvalidOperationException(), ReferenceFeed.RepositoryRoot);
        if (!files.File.Exists(path))
        {
            errors.Add("OPENCODE_MCP_TEST_CHANNEL_CONFIG file does not exist.");
        }
        await using var manifestStream = files.File.OpenRead(files.Path.Combine(ReferenceFeed.RepositoryRoot, "packaging", "native-manifest.json"));
        using var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: token);
        var rid = inputs["OPENCODE_MCP_TEST_RID"];
        if (!manifest.RootElement.GetProperty("runtimes").EnumerateArray().Any(runtime => runtime.GetProperty("rid").GetString() == rid))
        {
            errors.Add("OPENCODE_MCP_TEST_RID is not in the native manifest: " + rid);
        }
        if (errors.Count != 0)
        {
            throw new InvalidOperationException(string.Join('\n', errors));
        }
        await RequireConfigurationAsync(path, channel == "nuget" ? "nuget-org" : "github-packages", token);
        ReferenceFeed.Resolve();
        return path;
    }

    private async Task RequireConfigurationAsync(string path, string basename, CancellationToken token)
    {
        await using var selectedStream = files.File.OpenRead(path);
        var selected = await XDocument.LoadAsync(selectedStream, LoadOptions.None, token);
        await using var expectedStream = files.File.OpenRead(files.Path.Combine(ReferenceFeed.RepositoryRoot, "packaging", basename + ".nuget.config"));
        var expected = await XDocument.LoadAsync(expectedStream, LoadOptions.None, token);
        selected.Root?.Elements("packageSourceCredentials").Remove();
        if (!XNode.DeepEquals(selected, expected))
        {
            throw new InvalidOperationException("Channel configuration must match packaging/" + basename + ".nuget.config (sources and mapping).");
        }
    }
}
