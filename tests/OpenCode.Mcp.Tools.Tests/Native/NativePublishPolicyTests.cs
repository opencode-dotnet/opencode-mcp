using OpenCode.Mcp.Tools.Native;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativePublishPolicyTests
{
    [Test]
    [Arguments("linux", "x64", "x64", "Native execution needs a compatible win-x64 host; observed linux-x64 (process x64).")]
    [Arguments("win", "arm64", "arm64", "Native execution needs a compatible win-x64 host; observed win-arm64 (process arm64).")]
    [Arguments("win", "x64", "arm64", "Native execution needs a compatible win-x64 host; observed win-x64 (process arm64).")]
    public async Task HostDiagnostics_Should_Refuse_A_Mismatched_Host_Or_Process(string os, string osArchitecture, string processArchitecture, string diagnostic)
    {
        await Assert.That(NativePublishPolicy.HostDiagnostics("win-x64", os, osArchitecture, processArchitecture))
            .IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("IlcTreatWarningsAsErrors", null, "IlcTreatWarningsAsErrors must be true.")]
    [Arguments("DebuggerSupport", "false", "DebuggerSupport must not be false.")]
    [Arguments("PublishAot", "false", "PublishAot must be true.")]
    [Arguments("InvariantGlobalization", "false", "InvariantGlobalization must be true.")]
    [Arguments("SuppressTrimAnalysisWarnings", "TRUE", "SuppressTrimAnalysisWarnings must not be true.")]
    [Arguments("SuppressAotAnalysisWarnings", "true", "SuppressAotAnalysisWarnings must not be true.")]
    [Arguments("NoWarn", "CS1591;IL3050;IL2026;IL3050", "NoWarn must not contain IL2026.\nNoWarn must not contain IL3050.")]
    public async Task PropertyDiagnostics_Should_Refuse_Missing_Or_Disabled_Effective_Properties(string property, string? value, string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DebuggerSupport"] = "true",
            ["IlcTreatWarningsAsErrors"] = "true",
            ["InvariantGlobalization"] = "true",
            ["PublishAot"] = "true",
        };
        if (value is null)
        {
            properties.Remove(property);
        }
        else
        {
            properties[property] = value;
        }
        await Assert.That(NativePublishPolicy.PropertyDiagnostics(properties)).IsEquivalentTo(diagnostic.Split('\n'), CollectionOrdering.Matching);
    }

    [Test]
    public async Task WarningDiagnostics_Should_Reject_Trim_And_Aot_Warnings_Even_When_Publish_Succeeds()
    {
        var output = string.Join('\n', "publish succeeded", "IL3050: dynamic code", "IL2026: trimmed code", "IL3050: dynamic code");
        await Assert.That(NativePublishPolicy.WarningDiagnostics(output))
            .IsEquivalentTo(["IL2026: trimmed code", "IL3050: dynamic code"], CollectionOrdering.Matching);
    }
}
