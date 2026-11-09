using System.Text.Json;
using OpenCode.Mcp.Tools.Native;

namespace OpenCode.Mcp.Tools.Packaging;

internal sealed class PackageIdentityReader(NativeProcesses processes)
{
    public async Task<PackageIdentity> ReadAsync(CancellationToken token)
    {
        var output = await processes.RequireAsync("dotnet", ["msbuild", NativeProjectProperties.ProductProject, "-p:Configuration=Release", "-nodeReuse:false", "-p:UseSharedCompilation=false",
            "-getProperty:PackageId,ToolCommandName,Version,ToolPackageRuntimeIdentifiers"], new Dictionary<string, string?>(StringComparer.Ordinal), token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(output);
        var properties = document.RootElement.GetProperty("Properties");
        return new PackageIdentity(properties.GetProperty("PackageId").GetString() ?? "", properties.GetProperty("Version").GetString() ?? "",
            properties.GetProperty("ToolCommandName").GetString() ?? "", (properties.GetProperty("ToolPackageRuntimeIdentifiers").GetString() ?? "").Split(';'));
    }
}
