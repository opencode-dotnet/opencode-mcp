using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeProcessSnapshot(IFileSystem files, INativeProcessRunner runner, string root, TextWriter output)
{
    private const string Query = "$ErrorActionPreference='Stop'; Get-Process | Select-Object Id,ProcessName,CPU,WorkingSet64,@{Name='StartTime';Expression={try {$_.StartTime.ToUniversalTime().ToString('O')} catch {$null}}},@{Name='CommandLine';Expression={if (-not $IsWindows) {$_.CommandLine}}} | ConvertTo-Json -Compress";
    private const string CommandLineQuery = "$ErrorActionPreference='Stop'; Get-CimInstance Win32_Process -Property ProcessId,CommandLine -OperationTimeoutSec 30 | Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress";

    public async Task RecordAsync(string directory, Func<DateTimeOffset> timestamp, CancellationToken token)
    {
        files.Directory.CreateDirectory(directory);
        var commandLines = new Dictionary<int, string?>();
        var unavailable = OperatingSystem.IsWindows() ? await QueryCommandLinesAsync(commandLines, token).ConfigureAwait(false) : null;
        var json = await new NativeProcesses(runner, root).RequireAsync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", Query],
            new Dictionary<string, string?>(StringComparer.Ordinal), token, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        var now = timestamp();
        using var processes = JsonDocument.Parse(json);
        var lines = processes.RootElement.EnumerateArray().OrderByDescending(static process => Cpu(process))
            .Select(process => Format(process, now, commandLines)).ToArray();
        await files.File.WriteAllLinesAsync(files.Path.Combine(directory, "processes.txt"), unavailable is null ? lines : [unavailable, .. lines], token).ConfigureAwait(false);
        if (unavailable is not null)
        {
            await output.WriteLineAsync(unavailable.AsMemory(), token).ConfigureAwait(false);
        }
        foreach (var line in lines.Take(25))
        {
            await output.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
        }
    }

    private async Task<string?> QueryCommandLinesAsync(Dictionary<int, string?> commandLines, CancellationToken token)
    {
        NativeProcessResult result;
        try
        {
            result = await new NativeProcesses(runner, root).RunAsync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", CommandLineQuery],
                new Dictionary<string, string?>(StringComparer.Ordinal), token, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return "command lines unavailable: CIM query timed out after 45 s";
        }
        if (result.ExitCode != 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"command lines unavailable: CIM query exited {result.ExitCode}");
        }
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            IEnumerable<JsonElement> rows = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray() : [json.RootElement];
            foreach (var row in rows)
            {
                commandLines.Add(row.GetProperty("ProcessId").GetInt32(), row.GetProperty("CommandLine").GetString());
            }
            return null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            commandLines.Clear();
            return "command lines unavailable: CIM query returned unreadable output";
        }
    }

    private static double? Cpu(JsonElement process) => process.GetProperty("CPU").ValueKind == JsonValueKind.Null ? null : process.GetProperty("CPU").GetDouble();

    private static string Format(JsonElement process, DateTimeOffset now, IReadOnlyDictionary<int, string?> commandLines)
    {
        var started = process.GetProperty("StartTime");
        var age = started.ValueKind == JsonValueKind.Null ? -1 : Convert.ToInt32((now - started.GetDateTimeOffset()).TotalSeconds);
        return string.Format(CultureInfo.CurrentCulture, "{0}|{1}|cpu={2:N1}s|ws={3}MB|age={4}s|{5}", process.GetProperty("Id").GetInt32(),
            process.GetProperty("ProcessName").GetString(), Cpu(process), Convert.ToInt32(process.GetProperty("WorkingSet64").GetInt64() / 1_048_576d),
            age, OperatingSystem.IsWindows() ? commandLines.GetValueOrDefault(process.GetProperty("Id").GetInt32()) : process.GetProperty("CommandLine").GetString());
    }
}
