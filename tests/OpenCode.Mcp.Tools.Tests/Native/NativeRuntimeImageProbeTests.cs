using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeRuntimeImageProbeTests
{
    [Test]
    [Timeout(30_000)]
    [Arguments("dotnet")]
    [Arguments("libhostfxr.so")]
    [Arguments("libhostpolicy.so")]
    [Arguments("libcoreclr.so")]
    [Arguments("lookup")]
    [Arguments("enumeration")]
    public async Task ProbeAsync_Should_Refuse_Dotnet_Or_Unreadable_Image_Contents(string defect, CancellationToken token)
    {
        var scenario = new LinuxProofScenario();
        var request = await scenario.PrepareAsync(token);
        var diagnostic = "Runtime image contains .NET files:\n/usr/lib/" + defect;
        if (defect == "lookup")
        {
            scenario.Lookup = new NativeProcessResult(0, "/usr/bin/dotnet", "");
            diagnostic = "Runtime image dotnet lookup did not prove absence.";
        }
        else if (defect == "enumeration")
        {
            scenario.Scan = new NativeProcessResult(1, "", "Permission denied");
            diagnostic = "Runtime image filesystem enumeration failed:\nPermission denied";
        }
        else
        {
            scenario.Scan = new NativeProcessResult(0, "/usr/lib/" + defect, "");
        }
        await Assert.That(async () => await new NativeRuntimeImageProbe(scenario.Files, scenario.Runner).ProbeAsync(request, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage(diagnostic, StringComparison.Ordinal);
        var launches = scenario.Runner.Calls.Where(static call => call.Arguments[0] == "run").ToArray();
        foreach (var arguments in launches.Select(static launch => launch.Arguments))
        {
            var name = arguments[arguments.ToList().IndexOf("--name") + 1];
            await Assert.That(scenario.Runner.Calls.Where(call => call.Arguments.Contains(name, StringComparer.Ordinal)).Select(static call => call.Arguments[0]))
                .IsEquivalentTo(["run", "rm", "container"], CollectionOrdering.Matching);
        }
        await Assert.That(launches.Length).IsEqualTo(defect == "lookup" ? 1 : 2);
    }
}
