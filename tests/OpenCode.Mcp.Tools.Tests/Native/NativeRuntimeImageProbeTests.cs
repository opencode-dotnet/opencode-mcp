using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeRuntimeImageProbeTests
{
    [Test]
    [Timeout(30_000)]
    [Arguments(127, "", "", true)]
    [Arguments(1, "", "", true)]
    [Arguments(0, "", "", false)]
    [Arguments(2, "", "", false)]
    [Arguments(127, " ", "", false)]
    [Arguments(1, "", "\n", false)]
    public async Task ProbeAsync_Should_Accept_Only_An_Empty_Not_Found_Lookup(int exitCode, string stdout, string stderr, bool absent, CancellationToken token)
    {
        var scenario = new LinuxProofScenario { Lookup = new NativeProcessResult(exitCode, stdout, stderr) };
        var request = await scenario.PrepareAsync(token);
        var probe = new NativeRuntimeImageProbe(scenario.Files, scenario.Runner);
        if (absent)
        {
            NativeRuntimeIndependence? proof = null;
            await Assert.That(async () => proof = await probe.ProbeAsync(request, token)).ThrowsNothing();
            var measured = await Assert.That(proof).IsNotNull();
            await Assert.That(measured.DotnetAbsent).IsTrue();
            await Assert.That(measured.ImageReference).IsEqualTo(request.Manifest.RuntimeImage);
        }
        else
        {
            await Assert.That(async () => await probe.ProbeAsync(request, token)).ThrowsExactly<InvalidOperationException>()
                .WithMessage("Runtime image dotnet lookup did not prove absence.", StringComparison.Ordinal);
        }
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("all-proven")]
    [Arguments("second-unproven")]
    public async Task ProbeAsync_Should_Keep_The_Root_Until_All_Containers_Are_Absent(string outcome, CancellationToken token)
    {
        var scenario = new LinuxProofScenario();
        var request = await scenario.PrepareAsync(token);
        var respond = scenario.Runner.Response;
        var launches = 0;
        string? root = null;
        scenario.Runner.Response = (call, cancellation) =>
        {
            if (call.FileName == "docker")
            {
                root = call.WorkingDirectory;
                if (call.Arguments[0] == "run")
                {
                    launches++;
                }
                if (outcome == "second-unproven" && launches == 2 && call.Arguments[0] == "container" && call.Arguments[1] == "inspect")
                {
                    return Task.FromResult(new NativeProcessResult(1, "", "Cannot connect to the Docker daemon"));
                }
            }
            return respond(call, cancellation);
        };
        var probe = new NativeRuntimeImageProbe(scenario.Files, scenario.Runner);
        if (outcome == "second-unproven")
        {
            var error = await Assert.That(async () => await probe.ProbeAsync(request, token)).ThrowsExactly<AggregateException>();
            var failure = await Assert.That(error).IsNotNull();
            var nested = await Assert.That(failure.InnerExceptions[0]).IsTypeOf<AggregateException>();
            var cleanup = await Assert.That(nested).IsNotNull();
            await Assert.That(cleanup.InnerExceptions.Select(static item => item.Message))
                .IsEquivalentTo(["Cannot connect to the Docker daemon"], CollectionOrdering.Matching);
            await Assert.That(failure.InnerExceptions.Skip(1).Select(static item => item.Message))
                .IsEquivalentTo(["Container absence is unresolved; retained root: " + root], CollectionOrdering.Matching);
        }
        else
        {
            var proof = await probe.ProbeAsync(request, token);
            await Assert.That(proof.DotnetAbsent).IsTrue();
        }
        var ownedRoot = await Assert.That(root).IsNotNull();
        await Assert.That(scenario.Files.Directory.Exists(ownedRoot)).IsEqualTo(outcome == "second-unproven");
    }

    [Test]
    [Timeout(30_000)]
    public async Task ProbeAsync_Should_Refuse_Failed_Enumeration_Even_With_Empty_Output(CancellationToken token)
    {
        var scenario = new LinuxProofScenario { Scan = new NativeProcessResult(1, "", "") };
        var request = await scenario.PrepareAsync(token);
        await Assert.That(async () => await new NativeRuntimeImageProbe(scenario.Files, scenario.Runner).ProbeAsync(request, token))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Runtime image filesystem enumeration failed:\n", StringComparison.Ordinal);
    }

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
