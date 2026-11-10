using System.ComponentModel;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using NSubstitute;
using OpenCode.Mcp.Tools.Native.Abstractions;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Exceptions;

namespace OpenCode.Mcp.Tests.Support;

public sealed class OwnedToolAcquisitionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PrepareAsync_Should_Refuse_An_Unexpected_Private_Host_Inventory(bool runtimeInventory, CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var sdk = new PrivateSdkCopy(files);
        await using var owner = new OwnedToolAcquisition(files, runner, sdk);
        var output = "10.0.405";
        var command = runtimeInventory ? "--list-runtimes" : "--version";
        runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == command), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (runtimeInventory)
            {
                output = "Microsoft.NETCore.App 10.0.14 [" + files.Path.Combine(sdk.Root, "shared", "Microsoft.NETCore.App") + "]\n"
                    + "Microsoft.AspNetCore.App 10.0.14 [" + files.Path.Combine(sdk.Root, "shared", "Microsoft.AspNetCore.App") + "]";
            }
            return Task.FromResult(new NativeProcessResult(0, output, ""));
        });
        var failure = await Assert.That(() => owner.PrepareAsync(repository, "feed", "10.0.403", token)).ThrowsExactly<InvalidOperationException>();
        var error = await Assert.That(failure).IsNotNull();
        await Assert.That(error.Message).IsEqualTo((runtimeInventory ? "The private SDK must list only its required Core runtime: "
            : "The private SDK selected for global.json 10.0.403 must be 10.0.403: ") + output + "\n");
        string[] expected = runtimeInventory ? ["--version", "--list-runtimes"] : ["--version"];
        await Assert.That(runner.ReceivedCalls().Select(call => call.GetArguments()[0]).OfType<NativeProcessRequest>().Select(request => request.Arguments[0]))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("version", "opencodedotnet.mcp.win-x64")]
    [Arguments("opencodedotnet.mcp.win-x64", "version")]
    public async Task AssertMissingImplementationAsync_Should_Require_Each_Product_Identifier(string output, string missing)
    {
        var result = new NativeProcessResult(1, "", output);
        var failure = await Assert.That(() => OwnedToolAcquisition.AssertMissingImplementationAsync(result, "win-x64", "version", []))
            .ThrowsExactly<AssertionException>();
        var error = await Assert.That(failure).IsNotNull();
        await Assert.That(error.Message).Contains("to contain \"" + missing + "\"");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Acquisition_Should_Refuse_Ambient_Configuration_Immediately_Before_Launch(bool start, CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        var launch = owner.CreateDnxLaunch("version") with { FileName = files.Path.Combine(owner.OwnedRoot, "missing.exe") };
        string[] paths = [files.Path.Combine(files.Path.GetTempPath(), "NuGet.Config"), files.Path.Combine(launch.WorkingDirectory, "NuGet.Config")];
        foreach (var path in paths.Reverse())
        {
            await files.File.WriteAllTextAsync(path, "foreign configuration, never read", token);
        }
        var failure = await Assert.That(async () =>
        {
            if (start)
            {
                await owner.StartAsync(launch, token);
            }
            else
            {
                await owner.InstallAsync("version", token);
            }
        }).ThrowsExactly<InvalidOperationException>();
        var error = await Assert.That(failure).IsNotNull();
        await Assert.That(error.Message).IsEqualTo("Ambient NuGet configuration cannot be owned:\n" + string.Join('\n', paths));
        await Assert.That(runner.ReceivedCalls().Select(call => call.GetArguments()[0]).OfType<NativeProcessRequest>().Select(request => request.Arguments[0]))
            .IsEquivalentTo(["--version", "--list-runtimes"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task PrepareAsync_Should_Refuse_Fixed_Machine_Configuration_Only_On_MacOS(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        var machine = files.Path.GetFullPath("/Library/Application Support/NuGet/Config");
        files.Directory.CreateDirectory(machine);
        string[] paths = [files.Path.Combine(machine, "a.config"), files.Path.Combine(machine, "z.config")];
        foreach (var path in paths.Reverse())
        {
            await files.File.WriteAllTextAsync(path, "foreign configuration, never read", token);
        }
        if (OperatingSystem.IsMacOS())
        {
            await Assert.That(() => owner.PrepareAsync(repository, "feed", "10.0.403", token)).ThrowsExactly<InvalidOperationException>()
                .WithMessage("Ambient NuGet configuration cannot be owned:\n" + string.Join('\n', paths), StringComparison.Ordinal);
            await Assert.That(runner.ReceivedCalls()).IsEmpty();
        }
        else
        {
            await owner.PrepareAsync(repository, "feed", "10.0.403", token);
            await Assert.That(runner.ReceivedCalls().Select(call => call.GetArguments()[0]).OfType<NativeProcessRequest>().Select(request => request.Arguments[0]))
                .IsEquivalentTo(["--version", "--list-runtimes"], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task InstallAsync_Should_Retain_Installed_Command_Evidence_Before_Releasing_A_Failed_Installation(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        var command = files.Path.Combine(owner.ToolPath, "opencode-mcp.cmd");
        runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == "tool"), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await files.File.WriteAllTextAsync(command, "installed command", token);
            return new NativeProcessResult(1, "", "opencodedotnet.mcp.win-x64 version");
        });
        await Assert.That(async () => { await owner.InstallAsync("version", token); }).ThrowsExactly<InvalidOperationException>()
            .WithMessage("Tool-path installation failed: opencodedotnet.mcp.win-x64 version", StringComparison.Ordinal);
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsFalse();
        var result = await Assert.That(owner.InstallResult).IsNotNull();
        await Assert.That(owner.InstalledCommands).IsEquivalentTo([command], CollectionOrdering.Matching);
        await Assert.That(() => OwnedToolAcquisition.AssertMissingImplementationAsync(result, "win-x64", "version", owner.InstalledCommands)).ThrowsExactly<AssertionException>();
    }

    [Test]
    public async Task ObserveInstalledCommands_Should_Refuse_A_Missing_Root(CancellationToken token)
    {
        var files = new MockFileSystem();
        await using var owner = new OwnedToolAcquisition(files, ProbeRunner(files));
        await owner.DisposeAsync();
        await Assert.That(owner.ObserveInstalledCommands)
            .ThrowsExactly<DirectoryNotFoundException>();
        token.ThrowIfCancellationRequested();
    }

    [Test]
    public async Task StartAsync_Should_Release_The_Root_When_The_Process_Owner_Refuses_The_Launch(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        await using var owner = new OwnedToolAcquisition(files, ProbeRunner(files));
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        var sibling = owner.OwnedRoot + "-sibling";
        files.Directory.CreateDirectory(sibling);
        var sentinel = files.Path.Combine(sibling, "sentinel");
        await files.File.WriteAllTextAsync(sentinel, "unrelated", token);
        var launch = owner.CreateDnxLaunch("version") with { WorkingDirectory = sibling };
        await Assert.That(() => owner.StartAsync(launch, token)).ThrowsExactly<ArgumentException>()
            .WithMessage("The launch requires an opencode-mcp- temp child root containing its working directory. (Parameter 'launch')", StringComparison.Ordinal);
        await owner.DisposeAsync();
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsFalse();
        await Assert.That(await files.File.ReadAllTextAsync(sentinel, token)).IsEqualTo("unrelated");
    }

    [Test]
    public async Task PrepareAsync_Should_Copy_And_Seed_The_Selected_Sdk_Patch(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token, "10.0.404");
        await using var owner = new OwnedToolAcquisition(files, ProbeRunner(files, "10.0.404"));
        await owner.PrepareAsync(repository, "feed", "10.0.404", token);
        await Assert.That(owner.SdkVersion).IsEqualTo("10.0.404");
        await Assert.That(files.File.Exists(files.Path.Combine(owner.SdkDirectory, ".version"))).IsTrue();
        await Assert.That(files.Path.GetFileName(owner.SdkDirectory)).IsEqualTo("10.0.404");
        await Assert.That(files.Directory.EnumerateFiles(files.Path.Combine(owner.OwnedRoot, "home"), "*", SearchOption.AllDirectories))
            .IsEquivalentTo([files.Path.Combine(owner.OwnedRoot, "home", ".dotnet", "10.0.404.dotnetFirstUseSentinel")], CollectionOrdering.Matching);
        await Assert.That(await files.File.ReadAllBytesAsync(files.Path.Combine(owner.OwnedRoot, "work", "global.json"), token))
            .IsEquivalentTo(await files.File.ReadAllBytesAsync(files.Path.Combine(repository, "global.json"), token), CollectionOrdering.Matching);
    }

    [Test]
    public async Task PrepareAsync_Should_Name_The_Selected_Sdk_And_Installation_When_Missing(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token, "10.0.404");
        var installation = files.Path.GetFullPath(files.Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        await using var owner = new OwnedToolAcquisition(files, ProbeRunner(files));
        await Assert.That(() => owner.PrepareAsync(repository, "feed", "10.0.403", token)).ThrowsExactly<InvalidOperationException>()
            .WithMessage("The selected SDK 10.0.403 is not present in the installation: " + installation, StringComparison.Ordinal);
    }

    [Test]
    public async Task CreateDnxLaunch_Should_Remove_Inherited_Host_Variables_From_The_Exact_Environment(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        string[] names = ["PATH", "SystemRoot", "WINDIR", "ComSpec", "PATHEXT", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64",
            "DOTNET_HOST_PATH", "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE", "UNRELATED"];
        var inherited = names.ToDictionary(static name => name, static name => (string?)name, StringComparer.Ordinal);
        var environment = owner.Process.CreateEnvironment(owner.CreateDnxLaunch("version"), inherited);
        string[] expected = ["APPDATA", "ComSpec", "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "DOTNET_CLI_HOME", "DOTNET_CLI_TELEMETRY_OPTOUT",
            "DOTNET_CLI_UI_LANGUAGE", "DOTNET_GENERATE_ASPNET_CERTIFICATE", "DOTNET_NOLOGO", "DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK",
            "HOME", "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE", "LOCALAPPDATA", "NO_PROXY",
            .. OperatingSystem.IsLinux() ? new[] { "NUGET_COMMON_APPLICATION_DATA" } : [],
            "NUGET_HTTP_CACHE_PATH", "NUGET_PACKAGES", "NUGET_PLUGINS_CACHE_PATH", "NUGET_PLUGIN_PATHS", "NUGET_SCRATCH", "OPENCODE_CONFIG_DIR", "PATH", "PATHEXT",
            .. OperatingSystem.IsWindows() ? new[] { "ProgramFiles", "ProgramFiles(x86)" } : [],
            "SystemRoot", "TEMP", "TMP", "TMPDIR", "USERPROFILE", "WINDIR", "XDG_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_RUNTIME_DIR", "XDG_STATE_HOME"];
        await Assert.That(environment.Keys.Order(StringComparer.OrdinalIgnoreCase)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        foreach (var request in runner.ReceivedCalls().Select(call => call.GetArguments()[0]).OfType<NativeProcessRequest>())
        {
            await Assert.That(request.Environment.Keys).DoesNotContain("DOTNET_ROOT");
            await Assert.That(request.Environment.Keys).DoesNotContain("DOTNET_ROOT_X64");
            await Assert.That(request.Environment.Keys).DoesNotContain("DOTNET_ROOT_ARM64");
            await Assert.That(request.Environment.Keys).DoesNotContain("DOTNET_HOST_PATH");
        }
        await Assert.That(inherited.Keys).IsEquivalentTo(names, CollectionOrdering.Matching);
        await Assert.That(inherited.Values).IsEquivalentTo(names.Cast<string?>(), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("unused")]
    [Arguments("success")]
    [Arguments("failed-start")]
    public async Task DisposeAsync_Should_Preserve_An_Outside_Sentinel(string path, CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        var sibling = owner.OwnedRoot + "-sibling";
        files.Directory.CreateDirectory(sibling);
        var sentinel = files.Path.Combine(sibling, "sentinel");
        await files.File.WriteAllTextAsync(sentinel, "unrelated", token);
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsTrue();
        if (path != "unused")
        {
            await owner.PrepareAsync(repository, "feed", "10.0.403", token);
            if (path == "failed-start")
            {
                var launch = owner.CreateDnxLaunch("version") with { FileName = files.Path.Combine(owner.OwnedRoot, "missing.exe") };
                await Assert.That(() => owner.StartAsync(launch, token)).ThrowsExactly<Win32Exception>();
            }
            else
            {
                runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == "tool"), Arg.Any<CancellationToken>()).Returns(async _ =>
                {
                    await SeedInstalledCommandAsync(files, owner.ToolPath, token);
                    return new NativeProcessResult(0, "installed", "");
                });
                await owner.InstallAsync("version", token);
            }
        }
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsFalse();
        await Assert.That(await files.File.ReadAllTextAsync(sentinel, token)).IsEqualTo("unrelated");
    }

    [Test]
    public async Task AssertMissingImplementationAsync_Should_Reject_Zero_Exit(CancellationToken token)
    {
        var files = new MockFileSystem();
        await using var owner = new OwnedToolAcquisition(files, ProbeRunner(files));
        await Assert.That(() => OwnedToolAcquisition.AssertMissingImplementationAsync(new NativeProcessResult(0, "", "opencodedotnet.mcp.win-x64 version"), "win-x64", "version", []))
            .ThrowsExactly<AssertionException>();
        token.ThrowIfCancellationRequested();
    }

    [Test]
    public async Task ReadSdkRidAsync_Should_Read_The_Private_Dotnet_Process_Rid(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == "--info"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NativeProcessResult(0, "Runtime Environment:\n RID: linux-arm64\n Base Path: private-sdk", "")));
        await Assert.That(await owner.ReadSdkRidAsync(token)).IsEqualTo("linux-arm64");
    }

    [Test]
    public async Task StartAsync_Should_Report_A_Startup_Cleanup_Failure_Only_Once(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var decorated = DecorateDirectory(files, out var directory);
        await using var owner = new OwnedToolAcquisition(decorated, ProbeRunner(files));
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        directory.When(value => value.Delete(owner.OwnedRoot, recursive: true)).Do(_ => throw new IOException("root deletion refused"));
        var launch = owner.CreateDnxLaunch("version") with { FileName = files.Path.Combine(owner.OwnedRoot, "missing.exe") };
        var failure = await Assert.That(() => owner.StartAsync(launch, token)).ThrowsExactly<AggregateException>();
        var combined = await Assert.That(failure).IsNotNull();
        var errors = combined.Flatten().InnerExceptions;
        await Assert.That(errors.Select(static error => error.GetType())).IsEquivalentTo([typeof(Win32Exception), typeof(IOException)], CollectionOrdering.Matching);
        await Assert.That(errors[0].Message).Contains(launch.FileName);
        await Assert.That(errors[1].Message).IsEqualTo("root deletion refused");
        await Assert.That(combined.Message).Contains(owner.OwnedRoot);
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsTrue();
        await owner.DisposeAsync();
    }

    [Test]
    public async Task InstallAsync_Should_Preserve_Original_Then_Cleanup_Failure(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var decorated = DecorateDirectory(files, out var directory);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(decorated, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        directory.When(value => value.Delete(Arg.Any<string>(), recursive: true)).Do(call =>
        {
            var path = call.ArgAt<string>(0);
            if (path == owner.OwnedRoot)
            {
                throw new IOException("root deletion refused");
            }
            files.Directory.Delete(path, recursive: true);
        });
        runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == "tool"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NativeProcessResult(1, "", "installation refused")));
        var failure = await Assert.That(async () => { await owner.InstallAsync("version", token); }).ThrowsExactly<AggregateException>();
        var combined = await Assert.That(failure).IsNotNull();
        await Assert.That(combined.Message).StartsWith("Acquisition failed; retained root: " + owner.OwnedRoot);
        await Assert.That(combined.InnerExceptions.Select(static error => error.Message))
            .IsEquivalentTo(["Tool-path installation failed: installation refused", "root deletion refused"], CollectionOrdering.Matching);
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsTrue();
        await owner.DisposeAsync();
    }

    private static IFileSystem DecorateDirectory(MockFileSystem files, out IDirectory directory)
    {
        directory = Substitute.For<IDirectory>();
        directory.CreateDirectory(Arg.Any<string>()).Returns(call => files.Directory.CreateDirectory(call.Arg<string>()));
        directory.Exists(Arg.Any<string>()).Returns(call => files.Directory.Exists(call.Arg<string>()));
        directory.ResolveLinkTarget(Arg.Any<string>(), returnFinalTarget: true)
            .Returns(call => files.Directory.ResolveLinkTarget(call.Arg<string>(), returnFinalTarget: true));
        directory.EnumerateFiles(Arg.Any<string>()).Returns(call => files.Directory.EnumerateFiles(call.Arg<string>()));
        directory.EnumerateFiles(Arg.Any<string>(), Arg.Any<string>(), SearchOption.AllDirectories)
            .Returns(call => files.Directory.EnumerateFiles(call.ArgAt<string>(0), call.ArgAt<string>(1), SearchOption.AllDirectories));
        directory.EnumerateFileSystemEntries(Arg.Any<string>()).Returns(call => files.Directory.EnumerateFileSystemEntries(call.Arg<string>()));
        var decorated = Substitute.For<IFileSystem>();
        decorated.Directory.Returns(directory);
        decorated.File.Returns(files.File);
        decorated.Path.Returns(files.Path);
        decorated.FileInfo.Returns(files.FileInfo);
        return decorated;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InstallAsync_Should_Release_The_Root_And_Preserve_Full_Diagnostics_When_Acquisition_Fails(bool cancel, CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        var sibling = owner.OwnedRoot + "-sibling";
        files.Directory.CreateDirectory(sibling);
        var sentinel = files.Path.Combine(sibling, "sentinel");
        await files.File.WriteAllTextAsync(sentinel, "unrelated", token);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == "tool"), Arg.Any<CancellationToken>()).Returns(_ => cancel
            ? Task.FromCanceled<NativeProcessResult>(new CancellationToken(canceled: true))
            : Task.FromResult(new NativeProcessResult(7, "ordered stdout\nsecond stdout", "first stderr\nsecond stderr")));
        if (cancel)
        {
            await Assert.That(async () => { await owner.InstallAsync("version", token); }).ThrowsExactly<TaskCanceledException>();
        }
        else
        {
            await Assert.That(async () => { await owner.InstallAsync("version", token); }).ThrowsExactly<InvalidOperationException>()
                .WithMessage("Tool-path installation failed: ordered stdout\nsecond stdout\nfirst stderr\nsecond stderr", StringComparison.Ordinal);
            var result = await Assert.That(owner.InstallResult).IsNotNull();
            await Assert.That(result.ExitCode).IsEqualTo(7);
            await Assert.That(result.StandardOutput).IsEqualTo("ordered stdout\nsecond stdout");
            await Assert.That(result.StandardError).IsEqualTo("first stderr\nsecond stderr");
        }
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsFalse();
        await Assert.That(await files.File.ReadAllTextAsync(sentinel, token)).IsEqualTo("unrelated");
        await owner.DisposeAsync();
        await owner.DisposeAsync();
    }

    [Test]
    public async Task InstallAsync_Should_Require_Accepted_Layout_After_Zero_Exit(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == "tool"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NativeProcessResult(0, "Tool installed", "")));
        await Assert.That(async () => { await owner.InstallAsync("0.1.0-preview.1", token); }).ThrowsExactly<InvalidOperationException>()
            .WithMessage("The SDK reported success without the installed command: " + files.Path.Combine(owner.ToolPath, OperatingSystem.IsWindows() ? "opencode-mcp.cmd" : "opencode-mcp"), StringComparison.Ordinal);
    }

    [Test]
    public async Task InstallAsync_Should_Use_The_Exact_Tool_Path_Command(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == "tool"), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await SeedInstalledCommandAsync(files, owner.ToolPath, token);
            return new NativeProcessResult(0, "Tool installed", "");
        });
        var launch = await owner.InstallAsync("0.1.0-preview.1", token);
        var request = runner.ReceivedCalls().Select(call => call.GetArguments()[0]).OfType<NativeProcessRequest>().Last();
        await Assert.That(request.Arguments).IsEquivalentTo(["tool", "install", "OpenCodeDotNet.Mcp", "--version", "0.1.0-preview.1", "--tool-path", owner.ToolPath,
            "--configfile", files.Path.Combine(owner.OwnedRoot, "config", "local-feed.nuget.config"), "--framework", "net10.0", "-v", "minimal"], CollectionOrdering.Matching);
        await Assert.That(request.InheritEnvironment).IsFalse();
        await Assert.That(request.Environment["NUGET_PACKAGES"]).IsEqualTo(files.Path.Combine(owner.OwnedRoot, "cache", "packages"));
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(launch.FileName).IsEqualTo(files.Path.Combine(Environment.SystemDirectory, "cmd.exe"));
            await Assert.That(launch.Arguments).IsEmpty();
            await Assert.That(launch.VerbatimCommandLine).IsEqualTo("/d /e:on /v:off /s /c \"\"" + files.Path.Combine(owner.ToolPath, "opencode-mcp.cmd") + "\" --transport stdio\"");
        }
        else
        {
            await Assert.That(launch.FileName).IsEqualTo(files.Path.Combine(owner.ToolPath, "opencode-mcp"));
            await Assert.That(launch.Arguments).IsEquivalentTo(["--transport", "stdio"], CollectionOrdering.Matching);
        }
    }

    private static async Task SeedInstalledCommandAsync(MockFileSystem files, string toolPath, CancellationToken token)
    {
        var native = files.Path.Combine(toolPath, ".store", "child");
        files.Directory.CreateDirectory(native);
        var entry = OperatingSystem.IsWindows() ? "opencode-mcp.exe" : "opencode-mcp";
        await files.File.WriteAllTextAsync(files.Path.Combine(native, entry), "native bytes", token);
        var settings = new XDocument(new XElement("DotNetCliTool", new XElement("Commands", new XElement("Command", new XAttribute("Name", "opencode-mcp"),
            new XAttribute("Runner", "executable"), new XAttribute("EntryPoint", entry)))));
        await files.File.WriteAllTextAsync(files.Path.Combine(native, "DotnetToolSettings.xml"), settings.ToString(), token);
        await files.File.WriteAllTextAsync(files.Path.Combine(toolPath, OperatingSystem.IsWindows() ? "opencode-mcp.cmd" : "opencode-mcp"), "shim", token);
    }

    [Test]
    [Arguments("NuGet.Config")]
    [Arguments("nuget.config")]
    [Arguments("NuGet.config")]
    public async Task PrepareAsync_Should_Refuse_Ambient_NuGet_Configuration(string name, CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        var temp = files.Path.TrimEndingDirectorySeparator(files.Path.GetTempPath());
        var paths = new[] { files.Path.Combine(temp, name), files.Path.Combine(files.Path.GetPathRoot(temp) ?? throw new InvalidOperationException(), name) }
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var path in paths.Reverse())
        {
            await files.File.WriteAllTextAsync(path, "foreign configuration, never read", token);
        }
        await Assert.That(() => owner.PrepareAsync(repository, "feed", "10.0.403", token)).ThrowsExactly<InvalidOperationException>()
            .WithMessage("Ambient NuGet configuration cannot be owned:\n" + string.Join('\n', paths), StringComparison.Ordinal);
        await Assert.That(runner.ReceivedCalls()).IsEmpty();
        foreach (var path in paths)
        {
            await Assert.That(await files.File.ReadAllTextAsync(path, token)).IsEqualTo("foreign configuration, never read");
        }
    }

    [Test]
    [Arguments("cache/packages")]
    [Arguments("cache/http")]
    [Arguments("cache/plugins")]
    [Arguments("temp/nuget-scratch")]
    [Arguments("tool-path")]
    public async Task CreateDnxLaunch_Should_Refuse_A_Warmed_Acquisition(string relative, CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        var entry = files.Path.GetFullPath(files.Path.Combine(owner.OwnedRoot, relative, "previous-package"));
        files.Directory.CreateDirectory(entry);
        await Assert.That(() => owner.CreateDnxLaunch("version")).ThrowsExactly<InvalidOperationException>()
            .WithMessage("Cold package acquisition requires empty stores:\n" + entry, StringComparison.Ordinal);
        await Assert.That(runner.ReceivedCalls().Count()).IsEqualTo(2);
    }

    [Test]
    public async Task CreateDnxLaunch_Should_Use_The_Exact_Cold_Command(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        var feed = files.Path.GetFullPath("feed");
        await owner.PrepareAsync(repository, feed, "10.0.403", token);
        var launch = owner.CreateDnxLaunch("0.1.0-preview.1");
        var config = files.Path.Combine(owner.OwnedRoot, "config", "local-feed.nuget.config");
        await Assert.That(files.Path.IsPathRooted(launch.FileName)).IsTrue();
        await Assert.That(launch.FileName).IsNotEqualTo("dotnet");
        await Assert.That(launch.Arguments).IsEquivalentTo(["dnx", "-v", "minimal", "OpenCodeDotNet.Mcp@0.1.0-preview.1",
            "--configfile", config, "--yes", "--", "--transport", "stdio"], CollectionOrdering.Matching);
        await Assert.That(files.Directory.EnumerateFileSystemEntries(launch.WorkingDirectory).Select(files.Path.GetFileName))
            .IsEquivalentTo((string?[])["global.json"], CollectionOrdering.Matching);
        await Assert.That(await files.File.ReadAllBytesAsync(files.Path.Combine(launch.WorkingDirectory, "global.json"), token))
            .IsEquivalentTo(await files.File.ReadAllBytesAsync(files.Path.Combine(repository, "global.json"), token), CollectionOrdering.Matching);
        var sources = XDocument.Parse(await files.File.ReadAllTextAsync(config, token)).Descendants("packageSources").Single();
        await Assert.That(sources.Elements().Select(static element => element.Name.LocalName)).IsEquivalentTo(["clear", "add"], CollectionOrdering.Matching);
        await Assert.That(sources.Element("add")?.Attribute("value")?.Value).IsEqualTo(feed);
        var environment = owner.Process.CreateEnvironment(launch);
        string[] switches = ["DOTNET_NOLOGO", "DOTNET_GENERATE_ASPNET_CERTIFICATE", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH",
            "DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK", "DOTNET_CLI_UI_LANGUAGE"];
        await Assert.That(switches.Select(name => environment.GetValueOrDefault(name)))
            .IsEquivalentTo((string?[])["1", "false", "1", "false", "true", "en"], CollectionOrdering.Matching);
        await Assert.That(environment["PATH"]).IsEqualTo(files.Path.GetDirectoryName(launch.FileName) + files.Path.PathSeparator
            + (OperatingSystem.IsWindows() ? Environment.SystemDirectory : "/usr/bin:/bin"));
        await Assert.That(environment.Keys).DoesNotContain("ProgramData");
        await Assert.That(environment.Keys).DoesNotContain("ALLUSERSPROFILE");
        await Assert.That(environment.Keys).DoesNotContain("DOTNET_NUGET_SIGNATURE_VERIFICATION");
        foreach (var relative in new[] { "cache/packages", "cache/http", "cache/plugins", "temp/nuget-scratch", "tool-path" })
        {
            await Assert.That(files.Directory.EnumerateFileSystemEntries(files.Path.Combine(owner.OwnedRoot, relative))).IsEmpty();
        }
        await Assert.That(runner.ReceivedCalls().Select(call => call.GetArguments()[0]).OfType<NativeProcessRequest>().Select(request => request.Arguments.Single()))
            .IsEquivalentTo(["--version", "--list-runtimes"], CollectionOrdering.Matching);
    }

    private static INativeProcessRunner ProbeRunner(MockFileSystem files, string sdkVersion = "10.0.403")
    {
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<NativeProcessRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<NativeProcessRequest>();
            return Task.FromResult(new NativeProcessResult(0, request.Arguments[0] == "--version" ? sdkVersion + "\n" :
                "Microsoft.NETCore.App 10.0.14 [" + files.Path.Combine(files.Path.GetDirectoryName(request.FileName) ?? throw new InvalidOperationException(), "shared", "Microsoft.NETCore.App") + "]\n", ""));
        });
        return runner;
    }

    [Test]
    public async Task PrepareAsync_Should_Seed_Only_The_Sdk_First_Use_Notice(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        await using var owner = new OwnedToolAcquisition(files, ProbeRunner(files));
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        var sentinel = files.Path.Combine(owner.OwnedRoot, "home", ".dotnet", "10.0.403.dotnetFirstUseSentinel");
        await Assert.That(files.File.Exists(sentinel)).IsTrue();
        await Assert.That(files.Directory.EnumerateFiles(files.Path.Combine(owner.OwnedRoot, "home"), "*", SearchOption.AllDirectories))
            .IsEquivalentTo([sentinel], CollectionOrdering.Matching);
    }

    private static async Task<string> PrepareRepositoryAsync(MockFileSystem files, CancellationToken token, string installedVersion = "10.0.403")
    {
        var root = files.Path.GetFullPath("repository");
        files.Directory.CreateDirectory(files.Path.Combine(root, "packaging"));
        await files.File.WriteAllTextAsync(files.Path.Combine(root, "global.json"), new JsonObject { ["sdk"] = new JsonObject { ["version"] = "10.0.403" } }.ToJsonString(), token);
        await files.File.WriteAllTextAsync(files.Path.Combine(root, "Directory.Build.props"), new XDocument(new XElement("Project", new XElement("PropertyGroup",
            new XElement("TargetFramework", "net10.0")))).ToString(), token);
        await files.File.WriteAllTextAsync(files.Path.Combine(root, "packaging", "local-feed.nuget.config"), new XDocument(new XElement("configuration", new XElement("packageSources",
            new XElement("clear"), new XElement("add", new XAttribute("key", "local"), new XAttribute("value", "../artifacts/feed"))))).ToString(), token);
        var installation = files.Path.GetFullPath(files.Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var runtime = new JsonObject { ["runtimeOptions"] = new JsonObject { ["framework"] = new JsonObject { ["name"] = "Microsoft.NETCore.App", ["version"] = "10.0.14" } } };
        foreach (var relative in new[] { OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet", "sdk/10.0.403/.version", "sdk/10.0.403/dotnet.runtimeconfig.json",
            "shared/Microsoft.NETCore.App/10.0.14/coreclr.dll", "host/fxr/10.0.14/hostfxr.dll" })
        {
            var path = files.Path.Combine(installation, relative.Replace("10.0.403", installedVersion, StringComparison.Ordinal));
            files.Directory.CreateDirectory(files.Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
            await files.File.WriteAllTextAsync(path, relative.EndsWith(".json", StringComparison.Ordinal) ? runtime.ToJsonString() : relative, token);
        }
        return root;
    }

    [Test]
    [Arguments("NUGET_PACKAGES", "cache/packages")]
    [Arguments("NUGET_HTTP_CACHE_PATH", "cache/http")]
    [Arguments("NUGET_PLUGINS_CACHE_PATH", "cache/plugins")]
    [Arguments("NUGET_SCRATCH", "temp/nuget-scratch")]
    [Arguments("NUGET_PLUGIN_PATHS", "empty-plugins")]
    [Arguments("ProgramFiles", "machine/program-files")]
    [Arguments("ProgramFiles(x86)", "machine/program-files-x86")]
    [Arguments("NUGET_COMMON_APPLICATION_DATA", "machine")]
    public async Task CreateDnxLaunch_Should_Own_Each_Acquisition_Input(string name, string relative, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(relative);
        ArgumentNullException.ThrowIfNull(name);
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        await using var owner = new OwnedToolAcquisition(files, ProbeRunner(files));
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        var launch = owner.CreateDnxLaunch("version");
        var environment = owner.Process.CreateEnvironment(launch);
        if ((name.StartsWith("ProgramFiles", StringComparison.Ordinal) && !OperatingSystem.IsWindows())
            || (name == "NUGET_COMMON_APPLICATION_DATA" && !OperatingSystem.IsLinux()))
        {
            await Assert.That(environment.Keys).DoesNotContain(name);
            return;
        }
        await Assert.That(environment.Keys).Contains(name);
        await Assert.That(environment[name]).IsEqualTo(files.Path.Combine(owner.OwnedRoot, relative.Replace('/', files.Path.DirectorySeparatorChar)));
        await Assert.That(files.Directory.Exists(environment[name])).IsTrue();
    }
}
