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
    [Arguments("MSVC")]
    [Arguments("SDK")]
    public async Task CheckAsync_Should_Ignore_Non_Version_Folders_And_Select_The_Highest_Usable_Version(string kind, CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        AddPrerequisiteVersion(scenario, kind, "not-a-version");
        AddPrerequisiteVersion(scenario, "MSVC", "14.100");
        AddPrerequisiteVersion(scenario, "SDK", "10.0.30000.0");
        AddPrerequisiteVersion(scenario, "MSVC", "14.200", withFile: false);
        AddPrerequisiteVersion(scenario, "SDK", "10.0.40000.0", withFile: false);
        NativeToolchain? toolchain = null;
        await Assert.That(async () => toolchain = await new NativePrerequisites(scenario.Files, new NativeProcesses(scenario.Runner, request.Root))
            .CheckAsync(request.Rid, request.Host, token)).ThrowsNothing();
        var selected = await Assert.That(toolchain).IsNotNull();
        await Assert.That(selected.Inspector).IsEqualTo(scenario.Files.Path.Combine(scenario.VisualStudio, "VC", "Tools", "MSVC", "14.100", "bin", "Hostx64", "x64", "dumpbin.exe"));
        await Assert.That(selected.Details).Contains("Available Windows SDK: " + scenario.Files.Path.Combine(scenario.WindowsSdk, "10.0.30000.0"));
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("MSVC", "14.50", "Missing prerequisite: dumpbin.exe for win-x64")]
    [Arguments("SDK", "10.0.26100.0", "Missing prerequisite: Windows SDK libraries for win-x64.")]
    public async Task CheckAsync_Should_Refuse_When_Only_Non_Version_Folders_Hold_The_Required_File(string kind, string version, string diagnostic, CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        AddPrerequisiteVersion(scenario, kind, "not-a-version");
        var root = kind == "MSVC" ? scenario.Files.Path.Combine(scenario.VisualStudio, "VC", "Tools", "MSVC") : scenario.WindowsSdk;
        scenario.Files.Directory.Delete(scenario.Files.Path.Combine(root, version), recursive: true);
        AddPrerequisiteVersion(scenario, kind, version, withFile: false);
        await Assert.That(async () => await new NativePrerequisites(scenario.Files, new NativeProcesses(scenario.Runner, request.Root))
            .CheckAsync(request.Rid, request.Host, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage(diagnostic, StringComparison.Ordinal);
    }

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

    private static void AddPrerequisiteVersion(NativeBuildScenario scenario, string kind, string version, bool withFile = true)
    {
        var directory = kind == "MSVC"
            ? scenario.Files.Path.Combine(scenario.VisualStudio, "VC", "Tools", "MSVC", version, "bin", "Hostx64", "x64")
            : scenario.Files.Path.Combine(scenario.WindowsSdk, version, "um", "x64");
        scenario.Files.Directory.CreateDirectory(directory);
        if (withFile)
        {
            var file = kind == "MSVC" ? "dumpbin.exe" : "kernel32.lib";
            scenario.Files.Initialize().WithFile(scenario.Files.Path.Combine(directory, file));
            if (kind == "MSVC")
            {
                scenario.Files.Initialize().WithFile(scenario.Files.Path.Combine(directory, "link.exe"));
            }
        }
    }
}
