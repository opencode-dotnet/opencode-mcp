using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenCode.Mcp.Tools.Native;

/// <summary>Checks the publish evidence that must hold before a native claim.</summary>
internal static partial class NativePublishPolicy
{
    /// <summary>
    /// Microsoft.NET.ILLink.Tasks/10.0.14/build/Microsoft.NET.ILLink.targets:83-85 adds this default:
    /// it reports redundant suppressions, never trim- or AOT-unsafe code.
    /// </summary>
    private const string SdkRedundantSuppressionWarning = "IL2121";
    private static readonly string[] SuppressionProperties = ["SuppressTrimAnalysisWarnings", "SuppressAotAnalysisWarnings"];
    /// <summary>Checks that the invoked SDK host has the native process architecture.</summary>
    public static IReadOnlyList<string> SdkArchitectureDiagnostics(string sdkInfo, string expected)
    {
        ArgumentNullException.ThrowIfNull(sdkInfo);
        ArgumentNullException.ThrowIfNull(expected);
        var architecture = sdkInfo.Split('\n').Select(static line => line.Trim()).Where(static line => line.StartsWith("Architecture:", StringComparison.Ordinal))
            .Select(static line => line["Architecture:".Length..].Trim()).LastOrDefault() ?? "missing";
        return string.Equals(architecture, expected, StringComparison.Ordinal) ? [] : [$"SDK architecture must be {expected}; observed {architecture}."];
    }

    /// <summary>Rejects a host or SDK process that does not match the requested native target.</summary>
    public static IReadOnlyList<string> HostDiagnostics(string rid, string os, string osArchitecture, string processArchitecture)
    {
        ArgumentNullException.ThrowIfNull(rid);
        ArgumentNullException.ThrowIfNull(os);
        ArgumentNullException.ThrowIfNull(osArchitecture);
        ArgumentNullException.ThrowIfNull(processArchitecture);
        return string.Equals(rid, os + "-" + osArchitecture, StringComparison.Ordinal)
            && string.Equals(osArchitecture, processArchitecture, StringComparison.Ordinal)
            ? []
            : [$"Native execution needs a compatible {rid} host; observed {os}-{osArchitecture} (process {processArchitecture})."];
    }

    /// <summary>Returns unmet effective Native AOT project properties in assertion order.</summary>
    public static IReadOnlyList<string> PropertyDiagnostics(IReadOnlyDictionary<string, string> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var diagnostics = new List<string>();
        foreach (var name in new[] { "PublishAot", "InvariantGlobalization", "IlcTreatWarningsAsErrors" })
        {
            if (!properties.TryGetValue(name, out var value) || !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(name + " must be true.");
            }
        }
        if (properties.TryGetValue("DebuggerSupport", out var debugger) && string.Equals(debugger, "false", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add("DebuggerSupport must not be false.");
        }
        foreach (var name in SuppressionProperties
            .Where(name => properties.TryGetValue(name, out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(name + " must not be true.");
        }
        foreach (var name in SuppressionProperties.Prepend("NoWarn").Where(name => !properties.ContainsKey(name)))
        {
            diagnostics.Add(name + " is required.");
        }
        if (properties.TryGetValue("NoWarn", out var noWarn))
        {
            diagnostics.AddRange(noWarn.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(static word => word.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
                .Select(static code => code.StartsWith("IL", StringComparison.OrdinalIgnoreCase) ? code[2..] : code)
                .Select(static code => ushort.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0)
                .Where(static number => number is >= 2000 and <= 3999)
                .Select(static number => "IL" + number.ToString(CultureInfo.InvariantCulture))
                .Where(static code => code != SdkRedundantSuppressionWarning).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(static code => "NoWarn must not contain " + code + "."));
        }
        return diagnostics.AsReadOnly();
    }

    /// <summary>Returns the ordered trim and AOT diagnostics in captured compiler output.</summary>
    public static IReadOnlyList<string> WarningDiagnostics(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return [.. output.Split('\n').Select(static line => line.TrimEnd('\r'))
            .Where(static line => WarningCode().IsMatch(line)).Distinct(StringComparer.Ordinal)
            .OrderBy(static line => WarningCode().Match(line).Value, StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"\bIL[23]\d{3}\b", RegexOptions.None, Timeout.Infinite)]
    private static partial Regex WarningCode();
}
