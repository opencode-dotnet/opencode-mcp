using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativePrerequisitesTests
{
    [Test]
    [Timeout(30_000)]
    public async Task CheckAsync_Should_Require_Arm64_Components_And_Select_Arm64_Host_Tools(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var directory = scenario.Files.Path.Combine(scenario.VisualStudio, "VC", "Tools", "MSVC", "14.50", "bin", "Hostarm64", "arm64");
        scenario.Files.Directory.CreateDirectory(directory);
        var dumpbin = scenario.Files.Path.Combine(directory, "dumpbin.exe");
        scenario.Files.Initialize().WithFile(dumpbin).WithFile(scenario.Files.Path.Combine(directory, "link.exe"));
        var sdk = scenario.Files.Path.Combine(scenario.WindowsSdk, "10.0.26100.0", "um", "arm64");
        scenario.Files.Directory.CreateDirectory(sdk);
        scenario.Files.Initialize().WithFile(scenario.Files.Path.Combine(sdk, "kernel32.lib"));
        var selected = await new NativePrerequisites(scenario.Files, new NativeProcesses(scenario.Runner, request.Root)).CheckAsync("win-arm64", request.Host, token);
        await Assert.That(selected.Inspector).IsEqualTo(dumpbin);
        await Assert.That(scenario.Runner.Calls[0].Arguments).IsEquivalentTo(["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.ARM64", "-property", "installationPath"], CollectionOrdering.Matching);
        await Assert.That(scenario.Runner.Calls.Select(static call => call.FileName)).IsEquivalentTo([scenario.Files.Path.Combine(scenario.Installer, "vswhere.exe"), dumpbin, scenario.Files.Path.Combine(directory, "link.exe")], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("linux-x64", "-D --defined-only")]
    [Arguments("linux-arm64", "-D --defined-only")]
    [Arguments("osx-x64", "-gU")]
    [Arguments("osx-arm64", "-gU")]
    public async Task CheckAsync_Should_Select_The_Target_Export_Table(string rid, string arguments, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var toolchain = await new NativePrerequisites(scenario.Files, new NativeProcesses(scenario.Runner, request.Root))
            .CheckAsync(rid, request.Host, token);
        await Assert.That(toolchain.Inspector).IsEqualTo("nm");
        await Assert.That(toolchain.InspectorArguments).IsEquivalentTo(arguments.Split(' '), CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task CheckAsync_Should_Probe_Installed_Msvc_Tools_Without_Requiring_Help_To_Succeed(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var respond = scenario.Runner.Response;
        scenario.Runner.Response = (process, cancellation) => process.Arguments.SequenceEqual(["/?"], StringComparer.Ordinal)
            ? Task.FromResult(new NativeProcessResult(1100, "MSVC usage", "")) : respond(process, cancellation);
        NativeToolchain? toolchain = null;
        await Assert.That(async () => toolchain = await new NativePrerequisites(scenario.Files, new NativeProcesses(scenario.Runner, request.Root))
            .CheckAsync(request.Rid, request.Host, token)).ThrowsNothing();
        var selected = await Assert.That(toolchain).IsNotNull();
        await Assert.That(selected.Inspector).IsEqualTo(scenario.Dumpbin);
        await Assert.That(selected.InspectorArguments).IsEquivalentTo(["/exports"], CollectionOrdering.Matching);
        await Assert.That(selected.Details).Contains("Linker fixture version 14.50");
    }

    [Test]
    [Timeout(30_000)]
    public async Task CheckAsync_Should_Record_The_Selected_Linker_And_Available_Windows_Sdk(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var toolchain = await new NativePrerequisites(scenario.Files, new NativeProcesses(scenario.Runner, request.Root)).CheckAsync(request.Rid, request.Host, token);
        await Assert.That(toolchain.Details).Contains("Linker fixture version 14.50");
        await Assert.That(toolchain.Details).Contains(scenario.Files.Path.Combine(scenario.WindowsSdk, "10.0.26100.0"));
    }

    [Test]
    [Timeout(30_000)]
    public async Task CheckAsync_Should_Refuse_A_Missing_Windows_Sdk(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        scenario.Files.Directory.Delete(scenario.WindowsSdk, recursive: true);
        await Assert.That(async () => await new NativePrerequisites(scenario.Files, new NativeProcesses(scenario.Runner, request.Root)).CheckAsync(request.Rid, request.Host, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Missing prerequisite: Windows SDK libraries for win-x64.", StringComparison.Ordinal);
    }
}
