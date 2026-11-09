using System.Globalization;
using System.Text.Json;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeProcessSnapshotTests
{
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
