using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeProcessSnapshot(IFileSystem files, INativeProcessRunner runner, string root, TextWriter output)
{
    private const string Query = "$ErrorActionPreference='Stop'; Get-Process | Select-Object Id,ProcessName,CPU,WorkingSet64,@{Name='StartTime';Expression={try {$_.StartTime.ToUniversalTime().ToString('O')} catch {$null}}},CommandLine | ConvertTo-Json -Compress";

    public async Task RecordAsync(string directory, Func<DateTimeOffset> timestamp, CancellationToken token)
    {
        files.Directory.CreateDirectory(directory);
        var json = await new NativeProcesses(runner, root).RequireAsync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", Query],
            new Dictionary<string, string?>(StringComparer.Ordinal), token).ConfigureAwait(false);
        var now = timestamp();
        using var processes = JsonDocument.Parse(json);
        var lines = processes.RootElement.EnumerateArray().OrderByDescending(static process => Cpu(process))
            .Select(process => Format(process, now)).ToArray();
        await files.File.WriteAllLinesAsync(files.Path.Combine(directory, "processes.txt"), lines, token).ConfigureAwait(false);
        foreach (var line in lines.Take(25))
        {
            await output.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
        }
    }

    private static double? Cpu(JsonElement process) => process.GetProperty("CPU").ValueKind == JsonValueKind.Null ? null : process.GetProperty("CPU").GetDouble();

    private static string Format(JsonElement process, DateTimeOffset now)
    {
        var started = process.GetProperty("StartTime");
        var age = started.ValueKind == JsonValueKind.Null ? -1 : Convert.ToInt32((now - started.GetDateTimeOffset()).TotalSeconds);
        return string.Format(CultureInfo.CurrentCulture, "{0}|{1}|cpu={2:N1}s|ws={3}MB|age={4}s|{5}", process.GetProperty("Id").GetInt32(),
            process.GetProperty("ProcessName").GetString(), Cpu(process), Convert.ToInt32(process.GetProperty("WorkingSet64").GetInt64() / 1_048_576d),
            age, process.GetProperty("CommandLine").GetString());
    }
}
