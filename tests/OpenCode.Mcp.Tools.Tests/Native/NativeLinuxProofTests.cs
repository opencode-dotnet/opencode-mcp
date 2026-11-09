using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeLinuxProofTests
{
    [Test]
    [Timeout(30_000)]
    [Arguments("archive", "linux-x64: Archive digest differs from receipt.")]
    [Arguments("asset", "linux-x64: Changed payload file: LICENSE")]
    [Arguments("missing", "linux-x64 archive: Missing payload file: LICENSE")]
    public async Task ProveAsync_Should_Check_The_Archive_Before_Executing_It(string defect, string diagnostic, CancellationToken token)
    {
        var scenario = new LinuxProofScenario();
        var request = await scenario.PrepareAsync(token);
        var store = new NativeEvidenceStore(scenario.Files);
        var receiptPath = scenario.Files.Path.Combine(scenario.Output, "receipt.json");
        var receipt = await store.ReadAsync<NativeReceipt>(receiptPath, token);
        var archive = scenario.Files.Path.Combine(scenario.Output, receipt.Archive ?? throw new InvalidOperationException());
        if (defect == "asset")
        {
            await scenario.Files.File.WriteAllTextAsync(scenario.Files.Path.Combine(scenario.Output, "payload", "LICENSE"), "changed", token);
        }
        else if (defect == "missing")
        {
            await new PayloadArchive(scenario.Files, scenario.Modes, new NativeRid("linux-x64")).CreateAsync(scenario.Files.Path.Combine(scenario.Output, "payload"), archive,
                [.. receipt.Payload.Where(static file => file.Path != "LICENSE")], token);
            await store.WriteAsync(receiptPath, receipt with { ArchiveSha256 = await store.DigestAsync(archive, token) }, token);
        }
        else
        {
            await scenario.Files.File.AppendAllTextAsync(archive, "changed", token);
        }
        await Assert.That(async () => await new NativeLinuxProof(scenario.Files, scenario.Runner, scenario.Modes).ProveAsync(request, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage(diagnostic, StringComparison.Ordinal);
        await Assert.That(scenario.Runner.Calls.Where(static call => call.Arguments[0] == "test")).IsEmpty();
        var incomplete = await store.ReadAsync<NativeReceipt>(receiptPath, token);
        await Assert.That(incomplete.RuntimeIndependence).IsNull();
        await Assert.That(incomplete.GlibcFloor).IsNull();
    }

    [Test]
    [Timeout(30_000)]
    public async Task ProveAsync_Should_Write_Proof_Only_After_All_Checks_And_Cleanup_Succeed(CancellationToken token)
    {
        var scenario = new LinuxProofScenario();
        var request = await scenario.PrepareAsync(token);
        var completed = await new NativeLinuxProof(scenario.Files, scenario.Runner, scenario.Modes).ProveAsync(request, token);
        var proof = await Assert.That(completed.RuntimeIndependence).IsNotNull();
        await Assert.That(proof.Kind).IsEqualTo("linux-clean-container");
        await Assert.That(proof.DotnetAbsent).IsTrue();
        await Assert.That(proof.Tests).IsEqualTo(new NativeTestRun(0, 12, 0, 0));
        await Assert.That(proof.ImageId).IsEqualTo("sha256:" + new string('c', 64));
        await Assert.That(proof.ImageReference).IsEqualTo("registry.test/runtime@sha256:" + new string('b', 64));
        await Assert.That(completed.GlibcFloor).IsEqualTo(new NativeGlibcFloor("2.10", "GNU objdump fixture"));
        var test = scenario.Runner.Calls.Single(static call => call.Arguments[0] == "test");
        var executable = await Assert.That(test.Environment["OPENCODE_MCP_TEST_EXECUTABLE"]).IsNotNull();
        await Assert.That(scenario.Files.File.Exists(executable)).IsFalse();
        await Assert.That(test.Environment["OPENCODE_MCP_TEST_CONTAINER_IMAGE"]).IsEqualTo("registry.test/runtime@sha256:" + new string('b', 64));
        await Assert.That(test.Timeout).IsEqualTo(TimeSpan.FromMinutes(30));
        await Assert.That(NativeReceiptPolicy.Diagnostics(request.Manifest, [completed])).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    public async Task ProveAsync_Should_Refuse_A_Successful_No_Op_Test_Run(CancellationToken token)
    {
        var scenario = new LinuxProofScenario { OmitTestReport = true };
        var request = await scenario.PrepareAsync(token);
        await Assert.That(async () => await new NativeLinuxProof(scenario.Files, scenario.Runner, scenario.Modes).ProveAsync(request, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Native executable tests failed: NativeTestRun { ExitCode = 0, Passed = 0, Failed = 0, Skipped = 0, Succeeded = False }\n\n", StringComparison.Ordinal);
        var receipt = await new NativeEvidenceStore(scenario.Files).ReadAsync<NativeReceipt>(scenario.Files.Path.Combine(scenario.Output, "receipt.json"), token);
        await Assert.That(receipt.RuntimeIndependence).IsNull();
        await Assert.That(receipt.GlibcFloor).IsNull();
    }
}
