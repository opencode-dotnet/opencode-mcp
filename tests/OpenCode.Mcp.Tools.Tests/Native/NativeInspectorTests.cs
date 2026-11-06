using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeInspectorTests
{
    [Test]
    [Timeout(30_000)]
    public async Task InspectAsync_Should_Name_An_Unreadable_Image_During_A_Directory_Scan(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var directory = scenario.Files.Path.Combine(request.Output, "control");
        scenario.Files.Directory.CreateDirectory(directory);
        var unreadable = scenario.Files.Path.Combine(directory, "broken.dll");
        scenario.Files.Initialize().WithFile(scenario.Files.Path.Combine(directory, "OpenCode.Mcp.exe"));
        await scenario.Files.File.WriteAllBytesAsync(unreadable, [1, 2, 3], token);
        var inspector = new NativeInspector(scenario.Files, new NativeProcesses(scenario.Runner, request.Root));
        var toolchain = new NativeToolchain(scenario.Dumpbin, ["/exports"], request.Host.Environment, "fixture");
        await Assert.That(async () => await inspector.InspectAsync("win-x64", directory, toolchain, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Forbidden or unreadable image: " + unreadable, StringComparison.Ordinal);
    }

    [Test]
    [Timeout(30_000)]
    public async Task InspectAsync_Should_Inspect_Only_The_Selected_Image_Not_Its_Companions(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var directory = scenario.Files.Path.Combine(request.Output, "control");
        scenario.Files.Directory.CreateDirectory(directory);
        var executable = scenario.Files.Path.Combine(directory, "OpenCode.Mcp.exe");
        scenario.Files.Initialize().WithFile(executable).WithFile(scenario.Files.Path.Combine(directory, "control.deps.json"));
        var inspector = new NativeInspector(scenario.Files, new NativeProcesses(scenario.Runner, request.Root));
        var toolchain = new NativeToolchain(scenario.Dumpbin, ["/exports"], request.Host.Environment, "fixture");
        string? exports = null;
        await Assert.That(async () => exports = await inspector.InspectAsync("win-x64", executable, toolchain, token)).ThrowsNothing();
        await Assert.That(exports).IsEqualTo(scenario.Exports);
        await Assert.That(async () => await inspector.InspectAsync("win-x64", directory, toolchain, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Forbidden runtime file: control.deps.json", StringComparison.Ordinal);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("product.runtimeconfig.json", true)]
    [Arguments("product.DEPS.JSON", true)]
    [Arguments("libhostfxr.so", true)]
    [Arguments("hostpolicy.dll", true)]
    [Arguments("libcoreclr.dylib", true)]
    [Arguments("LICENSE", false)]
    public async Task IsForbiddenAsync_Should_Classify_Runtime_File_Names(string name, bool forbidden, CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var path = scenario.Files.Path.Combine(scenario.Root, name);
        var inspector = new NativeInspector(scenario.Files, new NativeProcesses(scenario.Runner, scenario.Root));
        await Assert.That(await inspector.IsForbiddenAsync(path, token)).IsEqualTo(forbidden);
    }

    [Test]
    [Timeout(30_000)]
    public async Task IsForbiddenAsync_Should_Reject_A_Managed_Pe_With_Metadata(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        scenario.Files.Directory.CreateDirectory(scenario.Root);
        var path = scenario.Files.Path.Combine(scenario.Root, "managed.dll");
        await scenario.Files.File.WriteAllBytesAsync(path, await new RealFileSystem().File.ReadAllBytesAsync(typeof(NativeInspector).Assembly.Location, token), token);
        await Assert.That(await new NativeInspector(scenario.Files, new NativeProcesses(scenario.Runner, scenario.Root)).IsForbiddenAsync(path, token)).IsTrue();
    }
}
