using System.Text.RegularExpressions;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal static partial class NativeGlibcPolicy
{
    public static NativeGlibcFloor Measure(NativeProcessResult tool, NativeProcessResult symbols)
    {
        if (tool.ExitCode != 0 || symbols.ExitCode != 0 || string.IsNullOrWhiteSpace(tool.StandardOutput))
        {
            throw new InvalidOperationException("glibc measurement requires successful objdump output.");
        }
        var versions = new List<Version>();
        foreach (var match in GlibcReference().Matches(symbols.StandardOutput).Cast<Match>())
        {
            var value = match.Groups["version"].Value;
            if (char.IsAsciiLetter(value[0]) && value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                continue;
            }
            if (!NumericVersion().IsMatch(value) || !Version.TryParse(value, out var version))
            {
                throw new InvalidOperationException("Malformed GLIBC requirement: " + value);
            }
            versions.Add(version);
        }
        var maximum = versions.OrderDescending().FirstOrDefault()
            ?? throw new InvalidOperationException("objdump output contains no numeric GLIBC requirement.");
        return new NativeGlibcFloor(maximum.ToString(), tool.StandardOutput.TrimEnd('\r', '\n'));
    }

    [GeneratedRegex(@"\bGLIBC_(?<version>[^\s()]+)", RegexOptions.ExplicitCapture, Timeout.Infinite)]
    private static partial Regex GlibcReference();

    [GeneratedRegex(@"\A[0-9]+(?:\.[0-9]+)+\z", RegexOptions.None, Timeout.Infinite)]
    private static partial Regex NumericVersion();
}
