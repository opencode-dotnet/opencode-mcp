using System.Xml.Linq;
using NSubstitute;
using OpenCode.Mcp.Tools.Native.Abstractions;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Support;

public sealed partial class OwnedToolAcquisitionTests
{
    [Test]
    public async Task InstallGlobalAsync_Should_Use_The_Exact_Owned_Global_Command(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        await owner.PrepareRemoteAsync(repository, files.Path.Combine(repository, "packaging", "local-feed.nuget.config"), "10.0.403", token);
        runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == "tool"), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await SeedInstalledCommandAsync(files, owner.GlobalToolPath, token);
            return new NativeProcessResult(0, "installed", "");
        });
        var launch = await owner.InstallGlobalAsync("0.1.0-preview.1", token);
        var request = runner.ReceivedCalls().Select(call => call.GetArguments()[0]).OfType<NativeProcessRequest>().Last();
        await Assert.That(request.Arguments).IsEquivalentTo(["tool", "install", "-g", "OpenCodeDotNet.Mcp", "--version", "0.1.0-preview.1",
            "--configfile", files.Path.Combine(owner.OwnedRoot, "config", "local-feed.nuget.config")], CollectionOrdering.Matching);
        await Assert.That(request.InheritEnvironment).IsFalse();
        await Assert.That(request.Environment["DOTNET_CLI_HOME"]).IsEqualTo(files.Path.Combine(owner.OwnedRoot, "home"));
        await Assert.That(request.Environment["NUGET_PACKAGES"]).IsEqualTo(files.Path.Combine(owner.OwnedRoot, "cache", "packages"));
        await Assert.That(files.Directory.Exists(files.Path.Combine(owner.GlobalToolPath, ".store"))).IsTrue();
        await Assert.That(owner.InstalledExecutable).IsEqualTo(files.Path.Combine(owner.GlobalToolPath, ".store", "child",
            OperatingSystem.IsWindows() ? "opencode-mcp.exe" : "opencode-mcp"));
        await Assert.That(launch.FileName).IsEqualTo(OperatingSystem.IsWindows() ? files.Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : files.Path.Combine(owner.GlobalToolPath, "opencode-mcp"));
    }

    [Test]
    public async Task InstallGlobalAsync_Should_Require_Layout_After_Zero_Exit(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        await using var owner = new OwnedToolAcquisition(files, ProbeRunner(files));
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        await Assert.That(async () => { await owner.InstallGlobalAsync("version", token); }).ThrowsExactly<InvalidOperationException>()
            .WithMessage("The SDK reported success without the installed command: " + files.Path.Combine(owner.GlobalToolPath,
                OperatingSystem.IsWindows() ? "opencode-mcp.cmd" : "opencode-mcp"), StringComparison.Ordinal);
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsFalse();
    }

    [Test]
    public async Task PrepareRemoteAsync_Should_Copy_Only_The_Selected_Configuration(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var source = files.Path.Combine(repository, "remote.config");
        var configuration = new XDocument(new XElement("configuration", new XElement("packageSources",
            new XElement("clear"), new XElement("add", new XAttribute("key", "nuget.org"),
                new XAttribute("value", "https://api.nuget.org/v3/index.json"))))).ToString();
        await files.File.WriteAllTextAsync(source, configuration, token);
        await using var owner = new OwnedToolAcquisition(files, ProbeRunner(files));
        await owner.PrepareRemoteAsync(repository, source, "10.0.403", token);
        var launch = owner.CreateDnxLaunch("version");
        var owned = launch.Arguments[5];
        await Assert.That(await files.File.ReadAllTextAsync(owned, token)).IsEqualTo(configuration);
        await Assert.That(await files.File.ReadAllTextAsync(source, token)).IsEqualTo(configuration);
        await Assert.That(files.Directory.EnumerateFiles(files.Path.Combine(owner.OwnedRoot, "config")))
            .IsEquivalentTo([owned], CollectionOrdering.Matching);
        await Assert.That(owned).StartsWith(owner.OwnedRoot + files.Path.DirectorySeparatorChar);
    }

    [Test]
    public async Task InstallGlobalAsync_Should_Refuse_An_Outside_Global_Tools_Path_Before_Launch(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        var outside = owner.OwnedRoot + "-sibling";
        files.Directory.CreateDirectory(outside);
        var sentinel = files.Path.Combine(outside, "sentinel");
        await files.File.WriteAllTextAsync(sentinel, "unrelated tools", token);
        files.Directory.CreateSymbolicLink(owner.GlobalToolPath, outside);
        await Assert.That(async () => { await owner.InstallGlobalAsync("version", token); }).ThrowsExactly<InvalidOperationException>()
            .WithMessage("Global tools directory must remain inside the owned root: " + owner.GlobalToolPath, StringComparison.Ordinal);
        await Assert.That(runner.ReceivedCalls().Select(call => call.GetArguments()[0]).OfType<NativeProcessRequest>().Select(request => request.Arguments[0]))
            .IsEquivalentTo(["--version", "--list-runtimes"], CollectionOrdering.Matching);
        await Assert.That(await files.File.ReadAllTextAsync(sentinel, token)).IsEqualTo("unrelated tools");
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsFalse();
    }

    [Test]
    [Arguments("cache/packages")]
    [Arguments("cache/http")]
    [Arguments("cache/plugins")]
    [Arguments("temp/nuget-scratch")]
    [Arguments("tool-path")]
    [Arguments("home/.dotnet/tools")]
    public async Task InstallGlobalAsync_Should_Refuse_All_Warmed_Stores_In_Path_Order(string relative, CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        var store = files.Path.GetFullPath(files.Path.Combine(owner.OwnedRoot, relative));
        files.Directory.CreateDirectory(store);
        string[] entries = [files.Path.Combine(store, "a"), files.Path.Combine(store, "z")];
        foreach (var entry in entries.Reverse())
        {
            await files.File.WriteAllTextAsync(entry, "warm", token);
        }
        await Assert.That(async () => { await owner.InstallGlobalAsync("version", token); }).ThrowsExactly<InvalidOperationException>()
            .WithMessage("Cold package acquisition requires empty stores:\n" + string.Join('\n', entries), StringComparison.Ordinal);
        await Assert.That(runner.ReceivedCalls().Select(call => call.GetArguments()[0]).OfType<NativeProcessRequest>().Select(request => request.Arguments[0]))
            .IsEquivalentTo(["--version", "--list-runtimes"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("success")]
    [Arguments("failure")]
    [Arguments("cancel")]
    public async Task InstallGlobalAsync_Should_Release_Owned_State_And_Preserve_The_Real_Tools_Sentinel(string outcome, CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        var realTools = files.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
        files.Directory.CreateDirectory(realTools);
        var sentinel = files.Path.Combine(realTools, "sentinel");
        await files.File.WriteAllTextAsync(sentinel, "unrelated tools", token);
        runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == "tool"), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (outcome == "cancel")
            {
                throw new OperationCanceledException("installation cancelled", token);
            }
            await SeedInstalledCommandAsync(files, owner.GlobalToolPath, token);
            return new NativeProcessResult(outcome == "success" ? 0 : 7, "stdout", "stderr");
        });
        if (outcome == "success")
        {
            await owner.InstallGlobalAsync("version", token);
        }
        else if (outcome == "failure")
        {
            await Assert.That(async () => { await owner.InstallGlobalAsync("version", token); }).ThrowsExactly<InvalidOperationException>()
                .WithMessage("Global installation failed: stdout\nstderr", StringComparison.Ordinal);
            await Assert.That(owner.InstalledCommands).IsNotNull();
        }
        else
        {
            await Assert.That(async () => { await owner.InstallGlobalAsync("version", token); }).ThrowsExactly<OperationCanceledException>()
                .WithMessage("installation cancelled", StringComparison.Ordinal);
        }
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsFalse();
        await Assert.That(await files.File.ReadAllTextAsync(sentinel, token)).IsEqualTo("unrelated tools");
    }

    [Test]
    public async Task InstallGlobalAsync_Should_Report_Operation_Before_Cleanup_Failure(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var decorated = DecorateDirectory(files, out var directory);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(decorated, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        directory.When(value => value.Delete(owner.OwnedRoot, recursive: true)).Do(_ => throw new IOException("root deletion refused"));
        runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == "tool"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NativeProcessResult(7, "", "installation refused")));
        var failure = await Assert.That(async () => { await owner.InstallGlobalAsync("version", token); }).ThrowsExactly<AggregateException>();
        var combined = await Assert.That(failure).IsNotNull();
        await Assert.That(combined.InnerExceptions.Select(static error => error.Message))
            .IsEquivalentTo(["Global installation failed: installation refused", "root deletion refused"], CollectionOrdering.Matching);
        await Assert.That(combined.Message).IsEqualTo("Acquisition failed; retained root: " + owner.OwnedRoot
            + " (Global installation failed: installation refused) (root deletion refused)");
        await owner.DisposeAsync();
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsTrue();
    }

    [Test]
    public async Task InstallGlobalAsync_Should_Refuse_A_Zero_Exit_Without_The_Global_Store(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == "tool"), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await SeedInstalledCommandAsync(files, owner.GlobalToolPath, token);
            files.Directory.Move(files.Path.Combine(owner.GlobalToolPath, ".store"), files.Path.Combine(owner.GlobalToolPath, "wrong-store"));
            return new NativeProcessResult(0, "installed", "");
        });
        await Assert.That(async () => { await owner.InstallGlobalAsync("version", token); }).ThrowsExactly<InvalidOperationException>()
            .WithMessage("The SDK reported success without the global tool store: " + owner.GlobalToolPath, StringComparison.Ordinal);
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsFalse();
    }

    [Test]
    public async Task InstallGlobalAsync_Should_Remove_Retained_Credentials_Without_Contents_In_Diagnostics(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        const string sentinel = "credential-sentinel-not-for-diagnostics";
        var configuration = new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear")),
            new XElement("packageSourceCredentials", new XElement("opencode-dotnet",
                new XElement("add", new XAttribute("key", "Username"), new XAttribute("value", "actor")),
                new XElement("add", new XAttribute("key", "ClearTextPassword"), new XAttribute("value", sentinel))))));
        var source = files.Path.Combine(repository, "remote.config");
        await files.File.WriteAllTextAsync(source, configuration.ToString(), token);
        var decorated = DecorateDirectory(files, out var directory);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(decorated, runner);
        await owner.PrepareRemoteAsync(repository, source, "10.0.403", token);
        var owned = owner.CreateDnxLaunch("version").Arguments[5];
        directory.When(value => value.Delete(owner.OwnedRoot, recursive: true)).Do(_ => throw new IOException("root deletion refused"));
        runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == "tool"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NativeProcessResult(7, "", "installation refused")));
        var failure = await Assert.That(async () => { await owner.InstallGlobalAsync("version", token); }).ThrowsExactly<AggregateException>();
        var combined = await Assert.That(failure).IsNotNull();
        await Assert.That(combined.InnerExceptions.Select(static error => error.Message))
            .IsEquivalentTo(["Global installation failed: installation refused", "root deletion refused"], CollectionOrdering.Matching);
        await Assert.That(combined.Message).IsEqualTo("Acquisition failed; retained root: " + owner.OwnedRoot
            + " (Global installation failed: installation refused) (root deletion refused)");
        await Assert.That(combined.Message).DoesNotContain(sentinel);
        await owner.DisposeAsync();
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsTrue();
        await Assert.That(await files.File.ReadAllTextAsync(source, token)).IsEqualTo(configuration.ToString());
        configuration.Root?.Elements("packageSourceCredentials").Remove();
        var retained = XDocument.Parse(await files.File.ReadAllTextAsync(owned, token));
        await Assert.That(retained.ToString()).IsEqualTo(configuration.ToString());
        await Assert.That(retained.ToString()).DoesNotContain(sentinel);
    }

    [Test]
    public async Task CreateDnxLaunch_Should_Pass_Http_Arguments_To_The_Remote_Package(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        await using var owner = new OwnedToolAcquisition(files, ProbeRunner(files));
        await owner.PrepareRemoteAsync(repository, files.Path.Combine(repository, "packaging", "local-feed.nuget.config"), "10.0.403", token);
        var launch = owner.CreateDnxLaunch("version", ["--transport", "http", "--urls", "http://127.0.0.1:0", "--environment", "Development"]);
        await Assert.That(launch.Arguments).IsEquivalentTo(["dnx", "-v", "minimal", "OpenCodeDotNet.Mcp@version", "--configfile",
            files.Path.Combine(owner.OwnedRoot, "config", "local-feed.nuget.config"), "--yes", "--", "--transport", "http", "--urls", "http://127.0.0.1:0",
            "--environment", "Development"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task InstallGlobalAsync_Should_Detect_A_Changed_Real_Tools_Inventory_During_Cleanup(CancellationToken token)
    {
        var files = new MockFileSystem();
        var repository = await PrepareRepositoryAsync(files, token);
        var runner = ProbeRunner(files);
        await using var owner = new OwnedToolAcquisition(files, runner);
        await owner.PrepareAsync(repository, "feed", "10.0.403", token);
        runner.RunAsync(Arg.Is<NativeProcessRequest>(request => request.Arguments[0] == "tool"), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            var realTools = files.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
            files.Directory.CreateDirectory(realTools);
            await files.File.WriteAllTextAsync(files.Path.Combine(realTools, "foreign-command"), "changed synthetic inventory", token);
            await SeedInstalledCommandAsync(files, owner.GlobalToolPath, token);
            return new NativeProcessResult(7, "", "installation refused");
        });
        var failure = await Assert.That(async () => { await owner.InstallGlobalAsync("version", token); }).ThrowsExactly<AggregateException>();
        var error = await Assert.That(failure).IsNotNull();
        await Assert.That(error.InnerExceptions.Select(static exception => exception.Message))
            .IsEquivalentTo(["Global installation failed: installation refused", "The real profile global-tools inventory changed."], CollectionOrdering.Matching);
        await owner.DisposeAsync();
        await Assert.That(files.Directory.Exists(owner.OwnedRoot)).IsFalse();
    }
}
