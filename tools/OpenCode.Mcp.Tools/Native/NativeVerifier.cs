using System.Globalization;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeVerifier(IFileSystem files, INativeProcessRunner runner)
{
    public async Task<NativeReceipt> VerifyAsync(NativeBuildRequest request, CancellationToken token)
    {
        var host = request.Host;
        var diagnostics = NativePublishPolicy.HostDiagnostics(request.Rid, host.Os, host.OsArchitecture, host.ProcessArchitecture);
        if (diagnostics.Count != 0)
        {
            throw new InvalidOperationException(string.Join('\n', diagnostics));
        }
        var processes = new NativeProcesses(runner, request.Root);
        var store = new NativeEvidenceStore(files);
        files.Directory.CreateDirectory(request.Output);
        var receiptPath = files.Path.Combine(request.Output, "receipt.json");
        files.File.Delete(receiptPath);
        var toolchain = await new NativePrerequisites(files, processes).CheckAsync(request.Rid, host, token).ConfigureAwait(false);
        await files.File.WriteAllTextAsync(files.Path.Combine(request.Output, "toolchain.log"), toolchain.Details, token).ConfigureAwait(false);
        var source = await new NativeSourceReader(files, processes).ReadAsync(request.Root, token).ConfigureAwait(false);
        var sdk = await processes.RequireAsync("dotnet", ["--version"], toolchain.PublishEnvironment, token).ConfigureAwait(false);
        var sdkInfo = await processes.RequireAsync("dotnet", ["--info"], toolchain.PublishEnvironment, token).ConfigureAwait(false);
        await files.File.WriteAllTextAsync(files.Path.Combine(request.Output, "sdk.log"), sdkInfo, token).ConfigureAwait(false);
        NativeDiagnostics.Refuse(NativePublishPolicy.SdkArchitectureDiagnostics(sdkInfo, host.ProcessArchitecture));
        await processes.RequireAsync("dotnet", ["build", "tests/OpenCode.Mcp.Tests/OpenCode.Mcp.Tests.csproj", "-c", "Release"], toolchain.PublishEnvironment, token).ConfigureAwait(false);
        await processes.RequireAsync("dotnet", ["restore", NativeProjectProperties.ProductProject, "-r", request.Rid], toolchain.PublishEnvironment, token).ConfigureAwait(false);
        var properties = await new NativeProjectProperties(files, processes).ReadAsync(request, sdk, toolchain.PublishEnvironment, token).ConfigureAwait(false);
        var publishDirectory = files.Path.Combine(request.Output, "publish");
        string[] publishCommand = ["publish", NativeProjectProperties.ProductProject, "-c", "Release", "-r", request.Rid, "-p:TrimmerSingleWarn=false", "-o", publishDirectory];
        var publish = await processes.RunAsync("dotnet", publishCommand, toolchain.PublishEnvironment, token).ConfigureAwait(false);
        await files.File.WriteAllTextAsync(files.Path.Combine(request.Output, "publish.log"), publish.Output, token).ConfigureAwait(false);
        var warnings = NativePublishPolicy.WarningDiagnostics(publish.Output);
        NativeDiagnostics.Refuse(warnings);
        if (publish.ExitCode != 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Native publish exited {publish.ExitCode}:\n{publish.Output}"));
        }
        var executable = files.Path.Combine(publishDirectory, request.Rid.StartsWith("win-", StringComparison.Ordinal) ? "OpenCode.Mcp.exe" : "OpenCode.Mcp");
        var inspection = await new NativeInspector(files, processes).InspectAsync(request.Rid, executable, toolchain, token).ConfigureAwait(false);
        await files.File.WriteAllTextAsync(files.Path.Combine(request.Output, "inspection.log"), inspection, token).ConfigureAwait(false);
        using var assets = JsonDocument.Parse(await files.File.ReadAllTextAsync(files.Path.Combine(request.Root, "src", "OpenCode.Mcp", "obj", "project.assets.json"), token).ConfigureAwait(false));
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
            AssetsSha256 = await store.DigestAsync(files.Path.Combine(request.Root, "src", "OpenCode.Mcp", "obj", "project.assets.json"), token).ConfigureAwait(false),
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
            BuilderImage = request.Rid.StartsWith("linux-", StringComparison.Ordinal) ? request.Manifest.BuilderImage : null,
            RuntimeImage = request.Rid.StartsWith("linux-", StringComparison.Ordinal) ? request.Manifest.RuntimeImage : null,
        };
        await store.WriteAsync(receiptPath, receipt, token).ConfigureAwait(false);
        return receipt;
    }

}
