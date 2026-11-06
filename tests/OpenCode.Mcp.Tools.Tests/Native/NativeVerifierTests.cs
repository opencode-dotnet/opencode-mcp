using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using Testably.Abstractions.Testing;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeVerifierTests
{
    [Test]
    [Timeout(30_000)]
    public async Task VerifyAsync_Should_Refuse_An_Unowned_Nonempty_Output_Without_Deleting_Files(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var payload = scenario.Files.Path.Combine(request.Output, "payload");
        var publish = scenario.Files.Path.Combine(request.Output, "publish");
        scenario.Files.Directory.CreateDirectory(payload);
        scenario.Files.Directory.CreateDirectory(publish);
        await scenario.Files.File.WriteAllTextAsync(scenario.Files.Path.Combine(payload, "keep"), "payload", token);
        await scenario.Files.File.WriteAllTextAsync(scenario.Files.Path.Combine(publish, "keep"), "publish", token);
        await Assert.That(async () => await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Output directory is not owned by the native tool: " + request.Output, StringComparison.Ordinal);
        await Assert.That(await scenario.Files.File.ReadAllTextAsync(scenario.Files.Path.Combine(payload, "keep"), token)).IsEqualTo("payload");
        await Assert.That(await scenario.Files.File.ReadAllTextAsync(scenario.Files.Path.Combine(publish, "keep"), token)).IsEqualTo("publish");
        await Assert.That(scenario.Runner.Calls).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("1701;1702;IL2121", null)]
    [Arguments("IL2121;IL2026", "NoWarn must not contain IL2026.")]
    [Arguments("IL3050", "NoWarn must not contain IL3050.")]
    public async Task VerifyAsync_Should_Allow_Only_The_Sdk_Redundant_Suppression_Default(string noWarn, string? diagnostic, CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        scenario.EffectiveProperties["NoWarn"] = noWarn;
        var request = await scenario.PrepareAsync(token);
        var verifier = new NativeVerifier(scenario.Files, scenario.Runner);
        if (diagnostic is not null)
        {
            await Assert.That(async () => await verifier.VerifyAsync(request, token))
                .ThrowsExactly<InvalidOperationException>().WithMessage(diagnostic, StringComparison.Ordinal);
        }
        else
        {
            await Assert.That(async () => await verifier.VerifyAsync(request, token)).ThrowsNothing();
            var receipt = await new NativeEvidenceStore(scenario.Files)
                .ReadAsync<NativeReceipt>(scenario.Files.Path.Combine(request.Output, "receipt.json"), token);
            await Assert.That(receipt.Properties["NoWarn"]).IsEqualTo("1701;1702;IL2121");
            await Assert.That(receipt.PublishedTests).IsEqualTo(new NativeTestRun(0, 12, 0, 0));
        }
    }

    [Test]
    [Timeout(30_000)]
    public async Task VerifyAsync_Should_Disable_Child_Build_Servers(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token);
        var builds = scenario.Runner.Calls.Where(static call => call.FileName == "dotnet" && call.Arguments[0] is "build" or "restore" or "msbuild" or "publish").ToArray();
        await Assert.That(builds.Select(static call => call.Arguments[0])).IsEquivalentTo(["build", "restore", "msbuild", "publish"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        foreach (var arguments in builds.Select(static build => build.Arguments))
        {
            await Assert.That(arguments).Contains("-nodeReuse:false");
            await Assert.That(arguments).Contains("-p:UseSharedCompilation=false");
        }
    }

    [Test]
    [Timeout(30_000)]
    public async Task VerifyAsync_Should_Remove_Owned_Stale_Publish_And_Payload_Files(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var publish = scenario.Files.Path.Combine(request.Output, "publish");
        var payload = scenario.Files.Path.Combine(request.Output, "payload");
        await new NativeEvidenceStore(scenario.Files).WriteAsync(scenario.Files.Path.Combine(request.Output, "receipt.json"),
            ReceiptFixture.Create(request.Manifest.Runtimes[0]), token);
        scenario.Files.Directory.CreateDirectory(publish);
        scenario.Files.Directory.CreateDirectory(payload);
        scenario.Files.Initialize().WithFile(scenario.Files.Path.Combine(publish, "stale.xml"))
            .WithFile(scenario.Files.Path.Combine(payload, "stale.json"));
        await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token);
        await Assert.That(scenario.Files.Directory.EnumerateFiles(publish).Select(path => scenario.Files.Path.GetFileName(path) ?? ""))
            .IsEquivalentTo(["OpenCode.Mcp.exe"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(scenario.Files.Directory.Exists(payload)).IsFalse();
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("architecture", "SDK architecture must be x64; observed arm64.")]
    [Arguments("version", "Evaluated SDK must match executed SDK: expected 10.0.403; observed 10.0.999.")]
    public async Task VerifyAsync_Should_Refuse_A_Different_Sdk_Identity_Before_Publishing(string defect, string diagnostic, CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        if (defect == "architecture")
        {
            scenario.SdkArchitecture = "arm64";
        }
        else
        {
            scenario.EffectiveProperties["NETCoreSdkVersion"] = "10.0.999";
        }
        var request = await scenario.PrepareAsync(token);
        await Assert.That(async () => await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage(diagnostic, StringComparison.Ordinal);
        await Assert.That(scenario.Runner.Calls.Where(static call => call.Arguments[0] == "publish")).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    public async Task VerifyAsync_Should_Refuse_A_Mismatched_Host_Before_Starting_Any_Process(CancellationToken token)
    {
        var files = new MockFileSystem();
        var runner = new FakeNativeProcessRunner();
        var host = new NativeHost("linux", "x64", "x64", "fixture", new Dictionary<string, string?>(StringComparer.Ordinal));
        var manifest = await new NativeManifestStore(files).ReadAsync(await new NativeManifestFixture().WriteAsync(files, token), token);
        var request = new NativeBuildRequest(files.Path.GetTempPath(), files.Path.GetTempPath(), "win-x64", host, manifest);
        await Assert.That(async () => await new NativeVerifier(files, runner).VerifyAsync(request, token))
            .ThrowsExactly<InvalidOperationException>()
            .WithMessage("Native execution needs a compatible win-x64 host; observed linux-x64 (process x64).", StringComparison.Ordinal);
        await Assert.That(runner.Calls).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    public async Task VerifyAsync_Should_Record_The_Published_Image_And_Exact_Native_Test_Run(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var receipt = await Assert.That(await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token)).IsNotNull();
        await Assert.That(scenario.Files.File.Exists(scenario.Files.Path.Combine(request.Output, "receipt.json"))).IsTrue();
        await Assert.That(receipt.Rid).IsEqualTo("win-x64");
        await Assert.That(receipt.SourceCommit).IsEqualTo("fixture-commit");
        await Assert.That(receipt.SdkVersion).IsEqualTo("10.0.403");
        await Assert.That(receipt.Executable).IsEqualTo(scenario.Files.Path.Combine(request.Output, "publish", "OpenCode.Mcp.exe"));
        await Assert.That(receipt.Properties["IlcTreatWarningsAsErrors"]).IsEqualTo("true");
        await Assert.That(receipt.PublishedTests).IsEqualTo(new NativeTestRun(0, 12, 0, 0));
        var publish = scenario.Runner.Calls.Single(static call => call.Arguments[0] == "publish");
        await Assert.That(publish.Environment["PATH"]).IsEqualTo(scenario.Installer + scenario.Files.Path.PathSeparator + "original-path");
        var test = scenario.Runner.Calls.Single(static call => call.Arguments[0] == "test");
        await Assert.That(test.Environment["OPENCODE_MCP_TEST_EXECUTABLE"]).IsEqualTo(receipt.Executable);
        await Assert.That(test.Environment["OPENCODE_MCP_TEST_EXPECT_NATIVE"]).IsEqualTo("true");
        await Assert.That(request.Host.Environment["PATH"]).IsEqualTo("original-path");
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task VerifyAsync_Should_Preserve_Publish_Warnings_And_Refuse_A_Receipt_Even_With_Exit_Zero(int exitCode, CancellationToken token)
    {
        var scenario = new NativeBuildScenario { PublishOutput = "IL3050: dynamic code\nIL2026: trimmed code", PublishExitCode = exitCode };
        var request = await scenario.PrepareAsync(token);
        await Assert.That(async () => await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("IL2026: trimmed code\nIL3050: dynamic code", StringComparison.Ordinal);
        await Assert.That(await scenario.Files.File.ReadAllTextAsync(scenario.Files.Path.Combine(request.Output, "publish.log"), token))
            .Contains(scenario.PublishOutput);
        await Assert.That(scenario.Files.File.Exists(scenario.Files.Path.Combine(request.Output, "receipt.json"))).IsFalse();
        await Assert.That(scenario.Runner.Calls.Where(static call => call.Arguments[0] == "test")).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    public async Task VerifyAsync_Should_Refuse_A_Missing_Windows_Component_Before_Publishing(CancellationToken token)
    {
        var scenario = new NativeBuildScenario { ComponentFound = false };
        var request = await scenario.PrepareAsync(token);
        await Assert.That(async () => await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Missing prerequisite: Microsoft.VisualStudio.Component.VC.Tools.x86.x64", StringComparison.Ordinal);
        await Assert.That(scenario.Runner.Calls.Select(static call => call.Arguments[0])).IsEquivalentTo(["-latest"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task VerifyAsync_Should_Refuse_A_Managed_Control_When_The_Inspector_Reports_No_Aot_Export(CancellationToken token)
    {
        var scenario = new NativeBuildScenario { Exports = "main hostfxr_main" };
        var request = await scenario.PrepareAsync(token);
        await Assert.That(async () => await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Missing native export: DotNetRuntimeDebugHeader", StringComparison.Ordinal);
        await Assert.That(scenario.Files.File.Exists(scenario.Files.Path.Combine(request.Output, "receipt.json"))).IsFalse();
    }

    [Test]
    [Timeout(30_000)]
    public async Task VerifyAsync_Should_Refuse_Disabled_Effective_Properties_Before_Publishing(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        scenario.EffectiveProperties.Remove("IlcTreatWarningsAsErrors");
        scenario.EffectiveProperties["DebuggerSupport"] = "false";
        var request = await scenario.PrepareAsync(token);
        await Assert.That(async () => await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("IlcTreatWarningsAsErrors must be true.\nDebuggerSupport must not be false.", StringComparison.Ordinal);
        await Assert.That(scenario.Runner.Calls.Where(static call => call.Arguments[0] == "publish")).IsEmpty();
    }
}
