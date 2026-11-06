using System.Globalization;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeVerifier(IFileSystem files, INativeProcessRunner runner)
{
    private static readonly string[] OwnedDirectories = ["publish", "payload"];
    public async Task<NativeReceipt> VerifyAsync(NativeBuildRequest request, CancellationToken token)
    {
        var host = request.Host;
        var rid = new NativeRid(request.Rid);
        var diagnostics = NativePublishPolicy.HostDiagnostics(request.Rid, host.Os, host.OsArchitecture, host.ProcessArchitecture);
        NativeDiagnostics.Refuse(diagnostics);
        var processes = new NativeProcesses(runner, request.Root);
        var store = new NativeEvidenceStore(files);
        await PrepareOutputAsync(request.Output, token).ConfigureAwait(false);
        var receiptPath = files.Path.Combine(request.Output, "receipt.json");
        var toolchain = await new NativePrerequisites(files, processes).CheckAsync(request.Rid, host, token).ConfigureAwait(false);
        await files.File.WriteAllTextAsync(files.Path.Combine(request.Output, "toolchain.log"), toolchain.Details, token).ConfigureAwait(false);
        var source = await new NativeSourceReader(files, processes).ReadAsync(request.Root, token).ConfigureAwait(false);
        var sdk = await processes.RequireAsync("dotnet", ["--version"], toolchain.PublishEnvironment, token).ConfigureAwait(false);
        var sdkInfo = await processes.RequireAsync("dotnet", ["--info"], toolchain.PublishEnvironment, token).ConfigureAwait(false);
        await files.File.WriteAllTextAsync(files.Path.Combine(request.Output, "sdk.log"), sdkInfo, token).ConfigureAwait(false);
        NativeDiagnostics.Refuse(NativePublishPolicy.SdkArchitectureDiagnostics(sdkInfo, host.ProcessArchitecture));
        await processes.RequireAsync("dotnet", ["build", "tests/OpenCode.Mcp.Tests/OpenCode.Mcp.Tests.csproj", "-c", "Release", "-nodeReuse:false", "-p:UseSharedCompilation=false"], toolchain.PublishEnvironment, token).ConfigureAwait(false);
        await processes.RequireAsync("dotnet", ["restore", NativeProjectProperties.ProductProject, "-r", request.Rid, "-nodeReuse:false", "-p:UseSharedCompilation=false"], toolchain.PublishEnvironment, token).ConfigureAwait(false);
        var properties = await new NativeProjectProperties(files, processes).ReadAsync(request, sdk, toolchain.PublishEnvironment, token).ConfigureAwait(false);
        var publishDirectory = files.Path.Combine(request.Output, "publish");
        string[] publishCommand = ["publish", NativeProjectProperties.ProductProject, "-c", "Release", "-r", request.Rid, "-p:TrimmerSingleWarn=false", "-o", publishDirectory, "-nodeReuse:false", "-p:UseSharedCompilation=false"];
        var publish = await processes.RunAsync("dotnet", publishCommand, toolchain.PublishEnvironment, token).ConfigureAwait(false);
        await files.File.WriteAllTextAsync(files.Path.Combine(request.Output, "publish.log"), publish.Output, token).ConfigureAwait(false);
        var warnings = NativePublishPolicy.WarningDiagnostics(publish.Output);
        NativeDiagnostics.Refuse(warnings);
        if (publish.ExitCode != 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Native publish exited {publish.ExitCode}:\n{publish.Output}"));
        }
        var executable = files.Path.Combine(publishDirectory, rid.PublishedName);
        var inspection = await new NativeInspector(files, processes).InspectAsync(request.Rid, publishDirectory, toolchain, token).ConfigureAwait(false);
        await files.File.WriteAllTextAsync(files.Path.Combine(request.Output, "inspection.log"), inspection, token).ConfigureAwait(false);
        var assetsPath = files.Path.Combine(request.Root, rid.AssetsPath);
        using var assets = JsonDocument.Parse(await files.File.ReadAllTextAsync(assetsPath, token).ConfigureAwait(false));
        var dependencies = DependencyInventory.Read(assets.RootElement, request.Rid);
        var tests = await new NativeExecutableTests(files, processes).RunAsync(executable, files.Path.Combine(request.Root, "artifacts", "test-results", "native", request.Rid, "published"), token).ConfigureAwait(false);
        var receipt = new NativeReceipt
        {
            Rid = request.Rid,
            Version = properties["Version"],
            SourceCommit = source.Commit,
            SourceStatus = source.Status,
            SdkVersion = sdk,
            CommonInputsSha256 = source.InputsSha256,
            DependenciesSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', dependencies.Select(static package => package.Id + "/" + package.Version + ":" + package.ContentIdentity))))),
            RequestedRunner = request.Manifest.Runtimes.Single(runtime => runtime.Rid == request.Rid).Runner,
            ActualRunner = host.Environment.GetValueOrDefault("RUNNER_NAME") ?? "local development host",
            Host = host.Description,
            Os = host.Os,
            OsArchitecture = host.OsArchitecture,
            Toolchain = toolchain.Details,
            AssetsSha256 = await store.DigestAsync(assetsPath, token).ConfigureAwait(false),
            CompilerPackages = [.. assets.RootElement.GetProperty("libraries").EnumerateObject().Select(static property => property.Name)
                .Where(static name => name.Contains("ILCompiler/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)],
            RunnerImage = host.Environment.GetValueOrDefault("ImageVersion"),
            ProcessArchitecture = host.ProcessArchitecture,
            Properties = properties,
            PublishDirectory = publishDirectory,
            Executable = executable,
            ExecutableSha256 = await store.DigestAsync(executable, token).ConfigureAwait(false),
            PublishCommand = publishCommand,
            PublishExitCode = publish.ExitCode,
            Warnings = warnings,
            Inspection = inspection,
            PublishedTests = tests,
            Dependencies = dependencies,
            BuilderImage = rid.IsLinux ? request.Manifest.BuilderImage : null,
            RuntimeImage = rid.IsLinux ? request.Manifest.RuntimeImage : null,
        };
        await store.WriteAsync(receiptPath, receipt, token).ConfigureAwait(false);
        return receipt;
    }

    private async Task PrepareOutputAsync(string output, CancellationToken token)
    {
        files.Directory.CreateDirectory(output);
        var receiptPath = files.Path.Combine(output, "receipt.json");
        if (files.File.Exists(receiptPath))
        {
            await new NativeEvidenceStore(files).ReadAsync<NativeReceipt>(receiptPath, token).ConfigureAwait(false);
        }
        else if (files.Directory.EnumerateFileSystemEntries(output).Any())
        {
            throw new InvalidOperationException("Output directory is not owned by the native tool: " + output);
        }
        files.File.Delete(receiptPath);
        foreach (var directory in OwnedDirectories.Select(name => files.Path.Combine(output, name)).Where(files.Directory.Exists))
        {
            files.Directory.Delete(directory, recursive: true);
        }
    }
}
