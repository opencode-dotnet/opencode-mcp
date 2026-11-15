using System.Globalization;
using System.Text.Json;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using Testably.Abstractions;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

[NotInParallel("processes")]
public sealed class NativeProcessSnapshotTests
{
    private const string SnapshotQuery = "$ErrorActionPreference='Stop'; $commandLines=@{}; if ($IsWindows) {try {Get-CimInstance Win32_Process -Property ProcessId,CommandLine -OperationTimeoutSec 30 | ForEach-Object {$commandLines[[int]$_.ProcessId]=$_.CommandLine}} catch {$commandLines=@{}}}; Get-Process | Select-Object Id,ProcessName,CPU,WorkingSet64,@{Name='StartTime';Expression={try {$_.StartTime.ToUniversalTime().ToString('O')} catch {$null}}},@{Name='CommandLine';Expression={if ($IsWindows) {$commandLines[$_.Id]} else {$_.CommandLine}}} | ConvertTo-Json -Compress";

    [Test]
    [Timeout(30_000)]
    public async Task RecordAsync_Should_Send_One_Two_Minute_PowerShell_Query(CancellationToken token)
    {
        var files = new MockFileSystem();
        var now = DateTimeOffset.UtcNow;
        var runner = new FakeNativeProcessRunner
        {
            Response = async (request, _) =>
            {
                await Assert.That(request.Timeout).IsEqualTo(TimeSpan.FromMinutes(2));
                await Assert.That(request.FileName).IsEqualTo("pwsh");
                await Assert.That(request.Arguments).IsEquivalentTo(["-NoProfile", "-NonInteractive", "-Command", SnapshotQuery], CollectionOrdering.Matching);
                return new NativeProcessResult(0, Query(now), "");
            },
        };
        await new NativeProcessSnapshot(files, runner, files.Path.GetTempPath(), TextWriter.Null)
            .RecordAsync(files.Path.Combine(files.Path.GetTempPath(), "snapshot"), () => now, token);
        await Assert.That(runner.Calls).Count().IsEqualTo(1);
    }

    [Test]
    [Timeout(30_000)]
    public async Task RecordAsync_Should_Timestamp_After_The_Process_Query(CancellationToken token)
    {
        var files = new MockFileSystem();
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var runner = new FakeNativeProcessRunner
        {
            Response = (_, _) =>
            {
                var started = now.AddSeconds(2);
                now = now.AddSeconds(5);
                var json = JsonSerializer.Serialize(new[] { new { Id = 7, ProcessName = "inside-query", CPU = 1, WorkingSet64 = 1_048_576L, StartTime = started, CommandLine = "query-child" } });
                return Task.FromResult(new NativeProcessResult(0, json, ""));
            },
        };
        var root = files.Path.Combine(files.Path.GetTempPath(), "snapshot");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        await new NativeProcessSnapshot(files, runner, files.Path.GetTempPath(), output).RecordAsync(root, () => now, token);
        await Assert.That(await files.File.ReadAllLinesAsync(files.Path.Combine(root, "processes.txt"), token))
            .IsEquivalentTo(["7|inside-query|cpu=" + 1d.ToString("N1", CultureInfo.CurrentCulture) + "s|ws=1MB|age=3s|query-child"], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task RecordAsync_Should_Preserve_The_Ci_Evidence_Fields_And_Cpu_Order(CancellationToken token)
    {
        var files = new MockFileSystem();
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var runner = new FakeNativeProcessRunner { Response = (_, _) => Task.FromResult(new NativeProcessResult(0, Query(now), "")) };
        var root = files.Path.Combine(files.Path.GetTempPath(), "snapshot");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            await new NativeProcessSnapshot(files, runner, files.Path.GetTempPath(), output).RecordAsync(root, () => now, token);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
        var snapshot = files.Path.Combine(root, "processes.txt");
        await Assert.That(files.File.Exists(snapshot)).IsTrue();
        var lines = await files.File.ReadAllLinesAsync(snapshot, token);
        int[] order = [25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0];
        await Assert.That(lines[0]).IsEqualTo("25|process25|cpu=25.0s|ws=25MB|age=25s|command25");
        await Assert.That(lines[^1]).IsEqualTo("0|process0|cpu=0.0s|ws=0MB|age=0s|command0");
        await Assert.That(lines.Select(static line => int.Parse(line.Split('|')[0], CultureInfo.InvariantCulture))).IsEquivalentTo(order, CollectionOrdering.Matching);
        var logged = output.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(logged.Select(static line => int.Parse(line.Split('|')[0], CultureInfo.InvariantCulture))).IsEquivalentTo(order.Take(25), CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task RecordAsync_Should_Format_Null_CommandLine_As_Empty_In_Cpu_Order(CancellationToken token)
    {
        var files = new MockFileSystem();
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var rows = new[]
        {
            new { Id = 1, ProcessName = "lower", CPU = 1, WorkingSet64 = 1_048_576L, StartTime = now, CommandLine = (string?)"command" },
            new { Id = 2, ProcessName = "higher", CPU = 2, WorkingSet64 = 2_097_152L, StartTime = now, CommandLine = (string?)null },
        };
        var runner = new FakeNativeProcessRunner { Response = (_, _) => Task.FromResult(new NativeProcessResult(0, JsonSerializer.Serialize(rows), "")) };
        var root = files.Path.Combine(files.Path.GetTempPath(), "snapshot");
        await new NativeProcessSnapshot(files, runner, files.Path.GetTempPath(), TextWriter.Null).RecordAsync(root, () => now, token);
        await Assert.That(await files.File.ReadAllLinesAsync(files.Path.Combine(root, "processes.txt"), token)).IsEquivalentTo(
            ["2|higher|cpu=" + 2d.ToString("N1", CultureInfo.CurrentCulture) + "s|ws=2MB|age=0s|", "1|lower|cpu=" + 1d.ToString("N1", CultureInfo.CurrentCulture) + "s|ws=1MB|age=0s|command"], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(150_000)]
    public async Task RecordAsync_Should_Query_The_Real_Test_Host(CancellationToken token)
    {
        var files = new MockFileSystem();
        var runner = new FakeNativeProcessRunner
        {
            Response = async (request, cancellation) =>
            {
                var result = await new SystemNativeProcessRunner().RunAsync(request, cancellation);
                await Assert.That(result.ExitCode).IsEqualTo(0);
                using var json = JsonDocument.Parse(result.StandardOutput);
                await Assert.That(json.RootElement.ValueKind).IsEqualTo(JsonValueKind.Array);
                var host = json.RootElement.EnumerateArray().Single(row => row.GetProperty("Id").GetInt32() == Environment.ProcessId);
                await Assert.That(host.GetProperty("ProcessName").GetString()).IsNotNullOrEmpty();
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                {
                    await Assert.That(host.GetProperty("CommandLine").GetString()).IsNotNullOrEmpty();
                }
                return result;
            },
        };
        await new NativeProcessSnapshot(files, runner, new RealFileSystem().Path.GetTempPath(), TextWriter.Null)
            .RecordAsync(files.Path.Combine(files.Path.GetTempPath(), "snapshot"), () => DateTimeOffset.UtcNow, token);
    }

    private static string Query(DateTimeOffset now) => JsonSerializer.Serialize(Enumerable.Range(0, 26).Select(index => new
    {
        Id = index,
        ProcessName = "process" + index.ToString(CultureInfo.InvariantCulture),
        CPU = index,
        WorkingSet64 = index * 1_048_576L,
        StartTime = now.AddSeconds(-index),
        CommandLine = "command" + index.ToString(CultureInfo.InvariantCulture),
    }));
}
