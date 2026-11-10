using System.Text.Json;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using OpenCode.Mcp.Tools.Packaging;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Packaging;

public sealed class PackageIdentityReaderTests
{
    [Test]
    public async Task ReadAsync_Should_Read_The_Evaluated_Owner_Once_Including_Rid_Order(CancellationToken token)
    {
        var runner = new FakeNativeProcessRunner
        {
            Response = static (_, _) => Task.FromResult(new NativeProcessResult(0, JsonSerializer.Serialize(new
            {
                Properties = new { PackageId = "Evaluated.Owner", Version = "9.8.7-preview.6", ToolCommandName = "evaluated-command", ToolPackageRuntimeIdentifiers = "osx-arm64;win-x64;linux-x64" },
            }), "")),
        };
        var result = await new PackageIdentityReader(new NativeProcesses(runner, "repository")).ReadAsync(token);
        await Assert.That(result.Id).IsEqualTo("Evaluated.Owner");
        await Assert.That(result.Version).IsEqualTo("9.8.7-preview.6");
        await Assert.That(result.CommandName).IsEqualTo("evaluated-command");
        await Assert.That(result.RuntimeIdentifiers).IsEquivalentTo(["osx-arm64", "win-x64", "linux-x64"], CollectionOrdering.Matching);
        await Assert.That(runner.Calls.Select(static call => call.FileName)).IsEquivalentTo(["dotnet"], CollectionOrdering.Matching);
        await Assert.That(runner.Calls[0].Arguments).IsEquivalentTo(["msbuild", "src/OpenCode.Mcp/OpenCode.Mcp.csproj", "-p:Configuration=Release", "-nodeReuse:false", "-p:UseSharedCompilation=false",
            "-getProperty:PackageId,ToolCommandName,Version,ToolPackageRuntimeIdentifiers"], CollectionOrdering.Matching);
        await Assert.That(runner.Calls[0].WorkingDirectory).IsEqualTo("repository");
        await Assert.That(runner.Calls[0].Environment).IsEmpty();
        await Assert.That(runner.Calls[0].Timeout).IsEqualTo(TimeSpan.FromMinutes(35));
    }
}
