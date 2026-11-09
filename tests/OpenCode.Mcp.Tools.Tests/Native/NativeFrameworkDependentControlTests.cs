using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeFrameworkDependentControlTests
{
    [Test]
    [Timeout(30_000)]
    [Arguments("main hostfxr_main", true)]
    [Arguments("DotNetRuntimeDebugHeader", false)]
    [Arguments("tool failure", false)]
    public async Task VerifyAsync_Should_Require_The_Exact_Image_Inspection_Refusal(string exports, bool accepted, CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var respond = scenario.Runner.Response;
        scenario.Runner.Response = async (call, cancellation) =>
        {
            if (call.FileName == scenario.Dumpbin && call.Arguments[0] == "/exports")
            {
                return new NativeProcessResult(exports == "tool failure" ? 1 : 0, exports, "");
            }
            if (call.Arguments[0] == "publish")
            {
                var directory = call.Arguments[call.Arguments.ToList().IndexOf("-o") + 1];
                scenario.Files.Directory.CreateDirectory(directory);
                await scenario.Files.File.WriteAllTextAsync(scenario.Files.Path.Combine(directory, "OpenCode.Mcp.exe"), "apphost", cancellation);
                await scenario.Files.File.WriteAllTextAsync(scenario.Files.Path.Combine(directory, "OpenCode.Mcp.deps.json"), "companion must not decide the verdict", cancellation);
                return new NativeProcessResult(0, "Published framework-dependent control.", "");
            }
            return await respond(call, cancellation);
        };
        var control = new NativeFrameworkDependentControl(scenario.Files, scenario.Runner);
        if (accepted)
        {
            string? refusal = null;
            await Assert.That(async () => refusal = await control.VerifyAsync(request, token)).ThrowsNothing();
            await Assert.That(refusal).IsEqualTo("Missing native export: DotNetRuntimeDebugHeader");
            var publish = scenario.Runner.Calls.Single(static call => call.Arguments[0] == "publish");
            var directory = publish.Arguments[publish.Arguments.ToList().IndexOf("-o") + 1];
            await Assert.That(publish.Arguments).Contains("--self-contained");
            await Assert.That(publish.Arguments).Contains("false");
            await Assert.That(publish.Arguments).Contains("-p:PublishAot=false");
            var inspect = scenario.Runner.Calls[^1];
            await Assert.That(inspect.Arguments).IsEquivalentTo(["/exports", scenario.Files.Path.Combine(directory, "OpenCode.Mcp.exe")], CollectionOrdering.Matching);
            await Assert.That(scenario.Files.Directory.Exists(directory)).IsFalse();
        }
        else
        {
            var error = await Assert.That(async () => await control.VerifyAsync(request, token)).ThrowsExactly<InvalidOperationException>();
            var failure = await Assert.That(error).IsNotNull();
            var publish = scenario.Runner.Calls.Single(static call => call.Arguments[0] == "publish");
            var directory = publish.Arguments[publish.Arguments.ToList().IndexOf("-o") + 1];
            var expected = exports == "DotNetRuntimeDebugHeader" ? "Framework-dependent control unexpectedly passed native inspection."
                : scenario.Dumpbin + " /exports " + scenario.Files.Path.Combine(directory, "OpenCode.Mcp.exe") + " exited 1:\ntool failure\n";
            await Assert.That(failure.Message).IsEqualTo(expected);
        }
    }
}
