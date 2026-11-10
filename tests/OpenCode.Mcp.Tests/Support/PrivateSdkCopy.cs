using System.Diagnostics;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Testably.Abstractions;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Owns the lazily prepared SDK installation shared read-only by package acquisition tests.</summary>
public sealed class PrivateSdkCopy : IAsyncDisposable
{
    private readonly IFileSystem _files;
    private readonly Lock _gate = new();
    private Task? _copy;
    private readonly string? _installation;

    /// <summary>Creates a session owner without copying or requiring package inputs.</summary>
    public PrivateSdkCopy() : this(new RealFileSystem()) { }

    internal PrivateSdkCopy(IFileSystem files, string? installation = null)
    {
        _files = files;
        _installation = installation;
    }

    internal string Root { get; private set; } = "";
    internal string SdkVersion { get; private set; } = "";
    internal string RuntimeVersion { get; private set; } = "";
    internal long Size { get; private set; }
    internal TimeSpan CopyDuration { get; private set; }
    internal string HostPath => _files.Path.Combine(Root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

    internal async Task PrepareAsync(string repositoryRoot, CancellationToken token)
    {
        var global = JsonNode.Parse(await _files.File.ReadAllTextAsync(_files.Path.Combine(repositoryRoot, "global.json"), token));
        var version = global?["sdk"]?["version"]?.GetValue<string>() ?? throw new InvalidOperationException("global.json has no SDK version.");
        var installation = _installation ?? _files.Path.GetFullPath(_files.Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        await PrepareAsync(installation, version, token);
    }

    internal Task PrepareAsync(string installation, string sdkVersion, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(sdkVersion);
        lock (_gate)
        {
            return _copy ??= CopyAsync(installation, sdkVersion, token);
        }
    }

    private async Task CopyAsync(string installation, string sdkVersion, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        SdkVersion = sdkVersion;
        var configuration = JsonNode.Parse(await _files.File.ReadAllTextAsync(_files.Path.Combine(installation, "sdk", sdkVersion, "dotnet.runtimeconfig.json"), token));
        var framework = configuration?["runtimeOptions"]?["framework"] ?? throw new InvalidOperationException("The selected SDK has no required framework.");
        if (framework["name"]?.GetValue<string>() != "Microsoft.NETCore.App")
        {
            throw new InvalidOperationException("The selected SDK must require only Microsoft.NETCore.App.");
        }
        RuntimeVersion = framework["version"]?.GetValue<string>() ?? throw new InvalidOperationException("The selected SDK has no required Core runtime version.");
        Root = _files.Path.Combine(_files.Path.GetTempPath(), "opencode-mcp-sdk-" + Guid.NewGuid().ToString("N"));
        _files.Directory.CreateDirectory(Root);
        CopyFile(installation, _files.Path.GetFileName(HostPath));
        foreach (var relative in new[] { _files.Path.Combine("sdk", sdkVersion), _files.Path.Combine("shared", "Microsoft.NETCore.App", RuntimeVersion),
            _files.Path.Combine("host", "fxr", RuntimeVersion) })
        {
            var source = _files.Path.Combine(installation, relative);
            foreach (var file in _files.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                CopyFile(installation, _files.Path.GetRelativePath(installation, file));
            }
        }
        CopyDuration = clock.Elapsed;
    }

    private void CopyFile(string installation, string relative)
    {
        var source = _files.Path.Combine(installation, relative);
        var destination = _files.Path.Combine(Root, relative);
        _files.Directory.CreateDirectory(_files.Path.GetDirectoryName(destination) ?? throw new InvalidOperationException());
        _files.File.Copy(source, destination);
        if (!OperatingSystem.IsWindows())
        {
            _files.File.SetUnixFileMode(destination, _files.File.GetUnixFileMode(source));
        }
        Size += _files.FileInfo.New(destination).Length;
    }

    /// <summary>Removes only this session's real SDK copy.</summary>
    public ValueTask DisposeAsync()
    {
        if (Root.Length != 0 && _files.Directory.Exists(Root))
        {
            _files.Directory.Delete(Root, recursive: true);
        }
        return ValueTask.CompletedTask;
    }
}
