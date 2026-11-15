using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeInstallerAssets(IFileSystem files)
{
    public async Task<IReadOnlyList<NativeInstallerAsset>> ReadAsync(string directory, string version, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(version);
        var diagnostics = new List<string>();
        var assets = new List<NativeInstallerAsset>();
        foreach (var name in new[] { "install.sh", "install.ps1" })
        {
            var path = files.Path.Combine(directory, name);
            if (!files.File.Exists(path))
            {
                diagnostics.Add("Missing installer script: " + name + ".");
                continue;
            }
            var bytes = await files.File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            var variable = name == "install.sh" ? "OPENCODE_MCP_RELEASE_VERSION" : "$OpenCodeMcpReleaseVersion";
            var expected = variable + (name == "install.sh" ? "='" : " = '") + version + "'";
            var lines = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Where(line => IsAssignment(line, variable)).ToArray();
            if (lines.Length != 1 || !string.Equals(lines[0], expected, StringComparison.Ordinal))
            {
                diagnostics.Add("Installer default version must be " + version + ": " + name + ".");
            }
            assets.Add(new(name, [.. bytes], Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }
        NativeDiagnostics.Refuse(diagnostics);
        return assets.AsReadOnly();
    }

    private static bool IsAssignment(string line, string variable) =>
        line.StartsWith(variable, StringComparison.Ordinal) && line.AsSpan(variable.Length).TrimStart() is ['=', ..];
}
