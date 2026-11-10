using System.ComponentModel;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Text.RegularExpressions;
using NSubstitute;
using OpenCode.Mcp.Tools.Native.Abstractions;
using Testably.Abstractions;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Exceptions;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Checks owned pipes, isolation and bounded diagnostics without a replacement MCP peer.</summary>
[NotInParallel("processes")]
public sealed class OwnedMcpProcessTests
{
    private readonly RealFileSystem _files = new();

    [Test]
    public async Task DisposeAsync_Should_End_The_Root_When_Owned_Process_Capture_Fails(CancellationToken token)
    {
        var launch = DiagnosticLaunch("for /L %i in (0,0,1) do @rem hold", "while :; do :; done");
        var failure = new IOException("owned root enumeration failed");
        var directory = ForwardDirectory();
        directory.EnumerateFiles(launch.OwnedRoot, "*", SearchOption.AllDirectories).Returns(_ => throw failure);
        var decorated = DecorateDirectory(directory);
        var owner = new OwnedMcpProcess(decorated);
        await owner.StartAsync(launch, token);
        using var root = Process.GetProcessById(owner.ProcessId);
        var exit = root.WaitForExitAsync(CancellationToken.None); // Backstop ownership is independent of disposal.
        var input = owner.StandardInput;
        Task? disposal = null;
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        guard.CancelAfter(TimeSpan.FromSeconds(45)); // Bounds only the disposal wait, not its expected capture failure.
        try
        {
            await Assert.That(root.HasExited).IsFalse();
            disposal = owner.DisposeAsync().AsTask();
            await Assert.That(() => disposal.WaitAsync(guard.Token)).ThrowsExactly<IOException>()
                .WithMessage("owned root enumeration failed", StringComparison.Ordinal);
            await Assert.That(owner.ExitCompleted).IsTrue(); // Before the backstop kill, including when stdin has closed.
            await Assert.That(root.HasExited).IsTrue();
            await Assert.That(owner.DiagnosticsCompleted).IsTrue();
            await Assert.That(input.CanWrite).IsFalse();
            await Assert.That(_files.Directory.Exists(launch.OwnedRoot)).IsTrue();
        }
        finally
        {
            if (!root.HasExited)
            {
                root.Kill(entireProcessTree: true);
            }
            await exit.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
            try
            {
                await owner.DisposeAsync(); // Joins the retained disposal after the independent backstop finishes.
            }
            catch (Exception error)
            {
                await Assert.That(error).IsSameReferenceAs(failure);
            }
            _files.Directory.Delete(launch.OwnedRoot, recursive: true);
        }
    }

    [Test]
    public async Task DisposeAsync_Should_End_Previously_Captured_Processes_When_The_Second_Capture_Fails(CancellationToken token)
    {
        var launch = DiagnosticLaunch("set /p hold=", "read hold");
        using var child = CreateOwnedChild(launch);
        var failure = new IOException("second owned root enumeration failed");
        var directory = ForwardDirectory();
        var captures = 0;
        directory.EnumerateFiles(launch.OwnedRoot, "*", SearchOption.AllDirectories).Returns(_ => ++captures == 3
            ? throw failure : _files.Directory.EnumerateFiles(launch.OwnedRoot, "*", SearchOption.AllDirectories));
        Task? childExit = null;
        var stdout = Task.CompletedTask;
        var stderr = Task.CompletedTask;
        Process? root = null;
        Task? rootExit = null;
        Task? disposal = null;
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        guard.CancelAfter(TimeSpan.FromSeconds(45)); // Bounds only the disposal wait; the capture failure remains the oracle.
        var owner = new OwnedMcpProcess(DecorateDirectory(directory));
        try
        {
            foreach (var entry in owner.CreateEnvironment(launch))
            {
                child.StartInfo.Environment[entry.Key] = entry.Value;
            }
            child.Start();
            childExit = child.WaitForExitAsync(CancellationToken.None); // Independent evidence survives owner disposal.
            stdout = child.StandardOutput.ReadToEndAsync(token);
            stderr = child.StandardError.ReadToEndAsync(token);
            await owner.StartAsync(launch, token);
            root = Process.GetProcessById(owner.ProcessId);
            rootExit = root.WaitForExitAsync(CancellationToken.None); // The test owns its backstop independently.
            await Assert.That(owner.ObserveOwnedExecutables().Keys).Contains(child.Id);
            await Assert.That(child.HasExited).IsFalse();
            disposal = owner.DisposeAsync().AsTask();
            var error = await Assert.That(() => disposal.WaitAsync(guard.Token)).ThrowsExactly<IOException>();
            await Assert.That(child.HasExited).IsTrue(); // Before any backstop: a second-capture failure must not skip the kill loop.
            await Assert.That(error).IsSameReferenceAs(failure);
            await Assert.That(owner.ExitCompleted).IsTrue();
            await Assert.That(root.HasExited).IsTrue();
            await Assert.That(captures).IsEqualTo(3);
            await Assert.That(_files.Directory.Exists(launch.OwnedRoot)).IsTrue();
        }
        finally
        {
            try
            {
                if (root is not null && !root.HasExited)
                {
                    root.Kill(entireProcessTree: true);
                }
                if (childExit is not null && !child.HasExited)
                {
                    child.Kill();
                }
                await Task.WhenAll(childExit ?? Task.CompletedTask, stdout, stderr, rootExit ?? Task.CompletedTask)
                    .WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); // Backstop cleanup must finish after caller cancellation.
            }
            finally
            {
                try
                {
                    await owner.DisposeAsync(); // Joins the retained disposal after both independently held processes exit.
                }
                catch (Exception error)
                {
                    await Assert.That(error).IsSameReferenceAs(failure);
                }
                root?.Dispose();
                _files.Directory.Delete(launch.OwnedRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task StartAsync_Should_Retain_Complete_Redacted_Acquisition_Diagnostics(CancellationToken token)
    {
        var launch = DiagnosticLaunch(">&2 echo first& >&2 echo NuGet TOKEN=synthetic& >&2 echo last", "printf '%s\n' first 'NuGet TOKEN=synthetic' last >&2");
        await using var owner = new OwnedMcpProcess(_files);
        await owner.StartAsync(launch, token);
        await owner.WaitForExitAsync(token);
        await Assert.That(owner.CompleteStandardError).IsEquivalentTo(["first", "NuGet TOKEN=***", "last"], CollectionOrdering.Matching);
        await Assert.That(owner.StandardError).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposeAsync_Should_Await_Every_Process_With_An_Executable_Inside_The_Root(bool rootExited, CancellationToken token)
    {
        var launch = DiagnosticLaunch(rootExited ? "exit /b 0" : "set /p hold=", rootExited ? "exit 0" : "read hold");
        using var child = CreateOwnedChild(launch);
        var directory = ForwardDirectory();
        directory.When(value => value.Delete(launch.OwnedRoot, recursive: true)).Do(_ =>
        {
            // Keep a live image available for the independent HasExited oracle, rather than a Windows delete error.
            if (child.HasExited)
            {
                _files.Directory.Delete(launch.OwnedRoot, recursive: true);
            }
        });
        var observedFiles = DecorateDirectory(directory);
        await using var owner = new OwnedMcpProcess(observedFiles);
        foreach (var entry in owner.CreateEnvironment(launch))
        {
            child.StartInfo.Environment[entry.Key] = entry.Value;
        }
        child.Start();
        var childExit = child.WaitForExitAsync(CancellationToken.None); // Independent evidence survives owner disposal.
        var stdout = child.StandardOutput.ReadToEndAsync(token);
        var stderr = child.StandardError.ReadToEndAsync(token);
        try
        {
            await owner.StartAsync(launch, token);
            using var root = rootExited ? null : Process.GetProcessById(owner.ProcessId);
            if (rootExited)
            {
                await owner.WaitForExitAsync(token);
            }
            await Assert.That(child.HasExited).IsFalse();
            await owner.DisposeAsync();
            await Assert.That(owner.ExitCompleted).IsTrue();
            if (root is not null)
            {
                await Assert.That(root.HasExited).IsTrue();
            }
            await Assert.That(child.HasExited).IsTrue(); // Before the backstop wait: root-only cleanup cannot pass.
            await Assert.That(_files.Directory.Exists(launch.OwnedRoot)).IsFalse();
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }
            await Task.WhenAll(childExit, stdout, stderr).WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
            if (_files.Directory.Exists(launch.OwnedRoot))
            {
                _files.Directory.Delete(launch.OwnedRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task StartAsync_Should_Pass_The_Shim_Command_Tail_Verbatim(CancellationToken token)
    {
        var launch = DiagnosticLaunch("exit /b 0", "exit 0");
        var directory = _files.Path.Combine(launch.OwnedRoot, "space & ampersand");
        _files.Directory.CreateDirectory(directory);
        var shim = _files.Path.Combine(directory, OperatingSystem.IsWindows() ? "probe.cmd" : "probe");
        if (OperatingSystem.IsWindows())
        {
            await _files.File.WriteAllTextAsync(shim, "@echo %1 %2\r\n@exit /b 0\r\n", token);
            launch = launch with { Arguments = [], VerbatimCommandLine = "/d /e:on /v:off /s /c \"\"" + shim + "\" --transport stdio\"" };
        }
        else
        {
            await _files.File.WriteAllTextAsync(shim, "#!/bin/sh\nprintf '%s %s\\n' \"$1\" \"$2\"\n", token);
            _files.File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            launch = launch with { FileName = shim, Arguments = ["--transport", "stdio"] };
        }
        await using var owner = new OwnedMcpProcess(_files);
        await owner.StartAsync(launch, token);
        await owner.CloseInputAsync();
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        guard.CancelAfter(TimeSpan.FromSeconds(45)); // Interpreter exit and pipe completion.
        await Assert.That(await owner.WaitForExitAsync(guard.Token)).IsEqualTo(0);
        await Assert.That(owner.Transcript.ServerLines).IsEquivalentTo(["--transport stdio"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task DisposeAsync_Should_Retain_And_Name_The_Root_When_Container_Absence_Is_Unproven()
    {
        var launch = ContainerDiagnosticLaunch();
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<NativeProcessRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<NativeProcessRequest>();
            if (request.FileName == "id")
            {
                return Task.FromResult(new NativeProcessResult(0, "1001", ""));
            }
            return Task.FromResult(request.Arguments[1] == "inspect" ? new NativeProcessResult(1, "", "Cannot connect to the Docker daemon")
                : new NativeProcessResult(0, "", ""));
        });
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        AggregateException? startup = null;
        try
        {
            await Assert.That(async () =>
            {
                await using var owner = new OwnedMcpProcess(_files, runner);
                try
                {
                    await owner.StartAsync(launch, cancellation.Token);
                }
                catch (AggregateException failure)
                {
                    startup = failure;
                }
            }).ThrowsExactly<AggregateException>();
            var failure = await Assert.That(startup).IsNotNull();
            await Assert.That(failure.InnerExceptions[0]).IsTypeOf<OperationCanceledException>();
            var nested = await Assert.That(failure.InnerExceptions[1]).IsTypeOf<AggregateException>();
            var cleanup = await Assert.That(nested).IsNotNull();
            await Assert.That(cleanup.InnerExceptions.Select(static error => error.Message)).IsEquivalentTo(
                ["Cannot connect to the Docker daemon", "Container ownership is unresolved; retained root: " + launch.OwnedRoot], CollectionOrdering.Matching);
            await Assert.That(_files.Directory.Exists(launch.OwnedRoot)).IsTrue();
        }
        finally
        {
            if (_files.Directory.Exists(launch.OwnedRoot))
            {
                _files.Directory.Delete(launch.OwnedRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task DisposeAsync_Should_End_The_Live_Cli_Before_Removal_And_Absence()
    {
        var managed = Launch();
        var assembly = managed.Arguments.Single(static argument => argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        var launch = ContainerDiagnosticLaunch() with
        {
            FileName = _files.Path.ChangeExtension(assembly, OperatingSystem.IsWindows() ? ".exe" : null),
            Arguments = [],
        };
        _files.Directory.Delete(managed.OwnedRoot, recursive: true);
        var runner = Substitute.For<INativeProcessRunner>();
        var phases = new List<string>();
        await using var owner = new OwnedMcpProcess(_files, runner);
        runner.RunAsync(Arg.Any<NativeProcessRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<NativeProcessRequest>();
            if (request.FileName == "id")
            {
                return Task.FromResult(new NativeProcessResult(0, "1001", ""));
            }
            if (request.Arguments[0] == "rm")
            {
                phases.Add(owner.HasExited ? "CLI ended" : "CLI live");
                phases.Add("rm -f");
            }
            else
            {
                phases.Add(request.Arguments[1]);
            }
            return Task.FromResult(request.Arguments[1] == "inspect"
                ? new NativeProcessResult(1, "[]", "Error: No such container: opencode-mcp-test-fixture") : new NativeProcessResult(0, "", ""));
        });
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Real child bootstrap before the cleanup-order oracle.
        await owner.StartAsync(launch, guard.Token);
        var client = await owner.ConnectStdioAsync(guard.Token);
        await Assert.That(client.NegotiatedProtocolVersion).IsEqualTo(ProtocolData.Revision);
        await Assert.That(owner.HasExited).IsFalse();
        await owner.DisposeAsync();
        await Assert.That(phases).IsEquivalentTo(["CLI ended", "rm -f", "inspect", "ls"], CollectionOrdering.Matching);
        await Assert.That(_files.Directory.Exists(launch.OwnedRoot)).IsFalse();
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("start failure")]
    [Arguments("exit")]
    public async Task DisposeAsync_Should_Remove_The_Container_On_Every_Owned_Exit_Path(string path)
    {
        var launch = DiagnosticLaunch("exit /b 0", "exit 0") with
        {
            Container = new ContainerLaunch("opencode-mcp-test-fixture", "registry.test/runtime@sha256:" + new string('b', 64), _files.Path.GetTempPath(), "/opencode-mcp-payload/opencode-mcp"),
        };
        if (path == "start failure")
        {
            launch = launch with { FileName = _files.Path.Combine(launch.OwnedRoot, "missing-child.exe") };
        }
        var runner = Substitute.For<INativeProcessRunner>();
        var calls = new List<string>();
        var cleanupTokens = new List<CancellationToken>();
        runner.RunAsync(Arg.Any<NativeProcessRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<NativeProcessRequest>();
            if (request.FileName == "id")
            {
                return Task.FromResult(new NativeProcessResult(0, "1001", ""));
            }
            calls.Add(string.Join(' ', request.Arguments));
            cleanupTokens.Add(call.Arg<CancellationToken>());
            return Task.FromResult(request.Arguments[1] == "inspect"
                ? new NativeProcessResult(1, "[]", "Error: No such container: opencode-mcp-test-fixture") : new NativeProcessResult(0, "", ""));
        });
        await using var owner = new OwnedMcpProcess(_files, runner);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Diagnostic CLI exit before the ownership oracle.
        if (path == "cancel")
        {
            await guard.CancelAsync();
            await Assert.That(() => owner.StartAsync(launch, guard.Token)).ThrowsExactly<OperationCanceledException>();
        }
        else if (path == "start failure")
        {
            await Assert.That(() => owner.StartAsync(launch, guard.Token)).ThrowsExactly<Win32Exception>();
        }
        else
        {
            await owner.StartAsync(launch, guard.Token);
            await owner.CloseInputAsync();
            await owner.WaitForExitAsync(guard.Token);
        }
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        await Assert.That(calls).IsEquivalentTo(["rm -f opencode-mcp-test-fixture", "container inspect opencode-mcp-test-fixture", "container ls --all --no-trunc --format {{.Names}}"], CollectionOrdering.Matching);
        await Assert.That(cleanupTokens.Select(static token => token.IsCancellationRequested)).IsEquivalentTo([false, false, false], CollectionOrdering.Matching);
        await Assert.That(cleanupTokens.Select(static token => token.CanBeCanceled)).IsEquivalentTo([true, true, true], CollectionOrdering.Matching);
        await Assert.That(cleanupTokens[0]).IsNotEqualTo(cleanupTokens[1]);
        await Assert.That(_files.Directory.Exists(launch.OwnedRoot)).IsFalse();
    }

    [Test]
    public async Task StartAsync_Should_Refuse_An_Unowned_Root_Without_Touching_A_Sibling_Working_Directory()
    {
        var launch = DiagnosticLaunch("exit /b 0", "exit 0");
        var unowned = _files.Path.Combine(_files.Path.GetTempPath(), "unowned-" + Guid.NewGuid().ToString("N"));
        _files.Directory.CreateDirectory(unowned);
        var sentinel = _files.Path.Combine(launch.WorkingDirectory, "sentinel.txt");
        await _files.File.WriteAllTextAsync(sentinel, "unrelated");
        try
        {
            await using var owner = new OwnedMcpProcess(_files);
            var context = await Assert.That(TestContext.Current).IsNotNull();
            await Assert.That(() => owner.StartAsync(launch with { OwnedRoot = unowned }, context.Execution.CancellationToken))
                .ThrowsExactly<ArgumentException>()
                .WithMessage("The launch requires an opencode-mcp- temp child root containing its working directory. (Parameter 'launch')", StringComparison.Ordinal);
            await Assert.That(_files.File.Exists(sentinel)).IsTrue();
            await Assert.That(await _files.File.ReadAllTextAsync(sentinel, context.Execution.CancellationToken)).IsEqualTo("unrelated");
        }
        finally
        {
            if (_files.Directory.Exists(launch.OwnedRoot))
            {
                _files.Directory.Delete(launch.OwnedRoot, recursive: true);
            }
            if (_files.Directory.Exists(unowned))
            {
                _files.Directory.Delete(unowned, recursive: true);
            }
        }
    }

    [Test]
    public async Task CloseInputAsync_Should_Close_The_Actual_Pipe_Before_Waiting_For_Exit()
    {
        var launch = Launch();
        await using var owner = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // EOF exit and reader completion.
        await owner.StartAsync(launch, guard.Token);
        var input = owner.StandardInput;
        await Assert.That(input.CanWrite).IsTrue();
        await owner.CloseInputAsync();
        // Inspect the transferred pipe, not a close-request flag. A no-op close fails here, before any wait.
        await Assert.That(input.CanWrite).IsFalse();
        await Assert.That(await owner.WaitForExitAsync(guard.Token)).IsEqualTo(0);
    }

    [Test]
    public async Task StartAsync_Should_Refuse_A_Second_Start_Without_Replacing_The_First_Child()
    {
        var launch = DiagnosticLaunch("exit /b 0", "exit 0");
        await using var owner = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // First child exit before the refused second start.
        await owner.StartAsync(launch, guard.Token);
        await Assert.That(await owner.WaitForExitAsync(guard.Token)).IsEqualTo(0);
        await Assert.That(() => owner.StartAsync(launch, guard.Token)).ThrowsExactly<InvalidOperationException>()
            .WithMessage("The owner can start only one child.", StringComparison.Ordinal);
        await Assert.That(_files.Directory.Exists(launch.OwnedRoot)).IsTrue();
    }

    [Test]
    public async Task StartAsync_Should_Preserve_The_Original_Failure_First_When_Cleanup_Also_Fails()
    {
        var files = new MockFileSystem();
        var root = files.Path.Combine(files.Path.GetTempPath(), "opencode-mcp-" + Guid.NewGuid().ToString("N"));
        var launch = new ProcessLaunch
        {
            OwnedRoot = root,
            WorkingDirectory = files.Path.Combine(root, "work"),
            FileName = files.Path.Combine(root, "missing-child.exe"),
            Arguments = [],
            Environment = new Dictionary<string, string?>(StringComparer.Ordinal),
        };
        var cleanup = new IOException("owned root deletion failed");
        var directory = Substitute.For<IDirectory>();
        directory.CreateDirectory(Arg.Any<string>()).Returns(call => files.Directory.CreateDirectory(call.Arg<string>()));
        directory.Exists(root).Returns(_ => files.Directory.Exists(root));
        directory.When(value => value.Delete(root, recursive: true)).Do(_ => throw cleanup);
        var decorated = Substitute.For<IFileSystem>();
        decorated.Path.Returns(files.Path);
        decorated.Directory.Returns(directory);
        AggregateException? startupFailure = null;
        var disposalFailure = await Assert.That(async () =>
        {
            await using var owner = new OwnedMcpProcess(decorated);
            var context = await Assert.That(TestContext.Current).IsNotNull();
            try
            {
                await owner.StartAsync(launch, context.Execution.CancellationToken);
            }
            catch (AggregateException failure)
            {
                startupFailure = failure;
            }
        }).ThrowsExactly<IOException>();
        await Assert.That(disposalFailure).IsSameReferenceAs(cleanup);
        var error = await Assert.That(startupFailure).IsNotNull();
        await Assert.That(error.InnerExceptions.Select(static value => value.GetType()))
            .IsEquivalentTo([typeof(Win32Exception), typeof(IOException)], CollectionOrdering.Matching);
        await Assert.That(error.InnerExceptions[0].Message).Contains(launch.FileName);
        await Assert.That(error.InnerExceptions[1]).IsSameReferenceAs(cleanup);
    }

    [Test]
    public async Task StartAsync_Should_Scrub_Ambient_Inputs_And_Apply_Explicit_Inputs_Before_Isolation()
    {
        var launch = Launch() with
        {
            Environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["OPENCODE_TEST_INPUT"] = "explicit",
                ["DOTNET_ENVIRONMENT"] = "Development",
            },
        };
        var parent = new ProcessStartInfo().Environment;
        await using var owner = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Child exit after input closes.
        await owner.StartAsync(launch, guard.Token);
        var environment = owner.ChildEnvironment;
        string[] necessities = ["PATH", "SystemRoot", "WINDIR", "ComSpec", "PATHEXT", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64",
            "DOTNET_HOST_PATH", "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE"];
        string[] isolated = ["HOME", "USERPROFILE", "DOTNET_CLI_HOME", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "OPENCODE_CONFIG_DIR",
            "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "TEMP", "TMP", "TMPDIR", "XDG_RUNTIME_DIR"];
        var expected = necessities.Where(parent.ContainsKey).Concat(isolated).Concat(["NO_PROXY", "OPENCODE_TEST_INPUT", "DOTNET_ENVIRONMENT"]);
        await Assert.That(environment.Keys.Order(StringComparer.OrdinalIgnoreCase))
            .IsEquivalentTo(expected.Order(StringComparer.OrdinalIgnoreCase), CollectionOrdering.Matching);
        await Assert.That(environment["OPENCODE_TEST_INPUT"]).IsEqualTo("explicit");
        await Assert.That(environment["DOTNET_ENVIRONMENT"]).IsEqualTo("Development");
        await Assert.That(environment["NO_PROXY"]).IsEqualTo("localhost,127.0.0.1,::1");
        foreach (var name in isolated)
        {
            await Assert.That(environment[name]).StartsWith(launch.OwnedRoot + _files.Path.DirectorySeparatorChar);
            await Assert.That(_files.Directory.Exists(environment[name])).IsTrue();
        }
        await Assert.That(new ProcessStartInfo().Environment.Keys.Order(StringComparer.Ordinal))
            .IsEquivalentTo(parent.Keys.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
        await owner.CloseInputAsync();
        await Assert.That(await owner.WaitForExitAsync(guard.Token)).IsEqualTo(0);
    }

    [Test]
    [Arguments("HOME")]
    [Arguments("dotnet_cli_home")]
    [Arguments("XDG_CONFIG_HOME")]
    [Arguments("TEMP")]
    [Arguments("OPENCODE_CONFIG_DIR")]
    public async Task StartAsync_Should_Refuse_An_Explicit_Isolation_Path_And_Remove_The_Root(string name)
    {
        var launch = Launch() with { Environment = new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = "not-owned" } };
        await using var owner = new OwnedMcpProcess(_files);
        var context = await Assert.That(TestContext.Current).IsNotNull();
        await Assert.That(() => owner.StartAsync(launch, context.Execution.CancellationToken)).ThrowsExactly<ArgumentException>()
            .WithMessage("Explicit environment input cannot replace an isolation path: " + name + " (Parameter 'explicitInputs')", StringComparison.Ordinal);
        await Assert.That(_files.Directory.Exists(launch.OwnedRoot)).IsFalse();
    }

    [Test]
    public async Task StartAsync_Should_Preserve_Prepared_Files_Until_Completed_Cleanup()
    {
        var launch = Launch();
        var prepared = _files.Path.Combine(launch.WorkingDirectory, "prepared.txt");
        await using var owner = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // EOF exit before removing the prepared launch root.
        await _files.File.WriteAllTextAsync(prepared, "prepared", guard.Token);
        await owner.StartAsync(launch, guard.Token);
        await Assert.That(await _files.File.ReadAllTextAsync(prepared, guard.Token)).IsEqualTo("prepared");
        await owner.CloseInputAsync();
        await Assert.That(await owner.WaitForExitAsync(guard.Token)).IsEqualTo(0);
        await owner.DisposeAsync();
        await Assert.That(_files.File.Exists(prepared)).IsFalse();
    }

    [Test]
    public async Task StartAsync_Should_Clean_The_Transferred_Root_When_Startup_Is_Cancelled()
    {
        var launch = Launch();
        await using var owner = new OwnedMcpProcess(_files);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.That(() => owner.StartAsync(launch, cancellation.Token)).ThrowsExactly<OperationCanceledException>();
        await Assert.That(_files.Directory.Exists(launch.OwnedRoot)).IsFalse();
    }

    [Test]
    public async Task StartAsync_Should_Release_Ownership_When_Process_Start_Fails()
    {
        var launch = Launch();
        launch = launch with { FileName = _files.Path.Combine(launch.OwnedRoot, "missing-child.exe") };
        await using var owner = new OwnedMcpProcess(_files);
        var context = await Assert.That(TestContext.Current).IsNotNull();
        var failure = await Assert.That(() => owner.StartAsync(launch, context.Execution.CancellationToken)).ThrowsExactly<Win32Exception>();
        var error = await Assert.That(failure).IsNotNull();
        await Assert.That(error.NativeErrorCode).IsEqualTo(2);
        await Assert.That(error.Message).Contains(launch.FileName);
        await Assert.That(_files.Directory.Exists(launch.OwnedRoot)).IsFalse();
        await owner.DisposeAsync();
    }

    [Test]
    public async Task DisposeAsync_Should_Finish_Owned_Readers_And_Preserve_Siblings_When_An_Assertion_Fails()
    {
        var launch = Launch();
        var sibling = launch.OwnedRoot + "-sibling";
        _files.Directory.CreateDirectory(sibling);
        var sentinel = _files.Path.Combine(sibling, "sentinel.txt");
        await _files.File.WriteAllTextAsync(sentinel, "unrelated");
        try
        {
            await using var owner = new OwnedMcpProcess(_files);
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Real discovery before the assertion failure.
            await Assert.That(async () =>
            {
                await using (owner)
                {
                    await owner.StartAsync(launch, guard.Token);
                    var client = await owner.ConnectStdioAsync(guard.Token);
                    await Assert.That(client.NegotiatedProtocolVersion).IsEqualTo("intentional assertion failure");
                }
            }).ThrowsExactly<AssertionException>();
            await Assert.That(owner.DiagnosticsCompleted).IsTrue();
            await Assert.That(owner.ExitCompleted).IsTrue();
            await Assert.That(_files.Directory.Exists(launch.OwnedRoot)).IsFalse();
            await Assert.That(await _files.File.ReadAllTextAsync(sentinel, guard.Token)).IsEqualTo("unrelated");
            await owner.DisposeAsync();
        }
        finally
        {
            _files.Directory.Delete(sibling, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WaitForExitAsync_Should_Drain_Large_Stdout_Concurrently_With_Exit_And_Http_Discovery(bool http)
    {
        var line = new string('x', 80);
        var launch = DiagnosticLaunch(
            ">&2 echo info: Microsoft.Hosting.Lifetime[0]& >&2 echo       Now listening on: http://127.0.0.1:12345& (for /L %i in (1,1,2000) do @echo " + line + ")",
            "printf '%s\\n' 'info: Microsoft.Hosting.Lifetime[0]' '      Now listening on: http://127.0.0.1:12345' >&2; " +
            "i=0; while [ \"$i\" -lt 2000 ]; do printf '%s\\n' '" + line + "'; i=$((i+1)); done");
        await using var owner = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Concurrent pipe drain and short child exit.
        await owner.StartAsync(launch, guard.Token);
        if (http)
        {
            await Assert.That((await owner.WaitForHttpEndpointAsync(guard.Token)).AbsoluteUri).IsEqualTo("http://127.0.0.1:12345/");
            await Assert.That(owner.OutputDrainStarted).IsTrue();
        }
        var exit = owner.WaitForExitAsync(guard.Token);
        await Assert.That(owner.OutputDrainStarted).IsTrue();
        await Assert.That(await exit).IsEqualTo(0);
        await Assert.That(owner.Transcript.ServerByteCount).IsEqualTo(2000 * (line.Length + (OperatingSystem.IsWindows() ? 2 : 1)));
        await Assert.That(owner.Transcript.ServerLines).IsEquivalentTo(Enumerable.Repeat(line, 2000), CollectionOrdering.Matching);
    }

    [Test]
    public async Task StartAsync_Should_Keep_The_Exact_Selected_Diagnostics_When_A_Short_Child_Exits()
    {
        var launch = DiagnosticLaunch(
            ">&2 echo ignored& >&2 echo fail: apiTOKEN=synthetic& >&2 echo crit: owned=%CD%",
            "printf '%s\\n' ignored 'fail: apiTOKEN=synthetic' \"crit: owned=$PWD\" >&2");
        await using var owner = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Short child exit and diagnostic drain.
        await owner.StartAsync(launch, guard.Token);
        await Assert.That(await owner.WaitForExitAsync(guard.Token)).IsEqualTo(0);
        await Assert.That(owner.StandardError).IsEquivalentTo(
            ["fail: apiTOKEN=***", "crit: owned=<run>" + _files.Path.DirectorySeparatorChar + "work"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("Password: hunter2", "Password:*** ***", "hunter2")]
    [Arguments("ApiKey: abc123", "ApiKey:*** ***", "abc123")]
    [Arguments("Token = xyz", "Token =*** ***", "xyz")]
    public async Task StartAsync_Should_Redact_The_Next_Token_When_A_Sensitive_Separator_Ends_A_Token(string input, string expected, string secret)
    {
        var launch = DiagnosticLaunch(
            ">&2 echo fail: " + input,
            "printf '%s\\n' 'fail: " + input + "' >&2");
        await using var owner = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Short child exit and diagnostic drain.
        await owner.StartAsync(launch, guard.Token);
        await Assert.That(await owner.WaitForExitAsync(guard.Token)).IsEqualTo(0);
        await Assert.That(owner.StandardError).IsEquivalentTo(["fail: " + expected], CollectionOrdering.Matching);
        await Assert.That(string.Join(' ', owner.StandardError)).DoesNotContain(secret);
    }

    [Test]
    public async Task StartAsync_Should_Redact_Both_Owned_Root_Spellings_Through_Ancestor_Links()
    {
        var launch = DiagnosticLaunch("exit /b 0", "exit 0");
        var physicalContainer = launch.OwnedRoot + "-physical";
        var parent = _files.Path.Combine(physicalContainer, "longer-parent");
        var ancestor = _files.Path.Combine(physicalContainer, "link");
        var physicalRoot = _files.Path.Combine(parent, "child");
        _files.Directory.CreateDirectory(physicalRoot);
        _files.Directory.Delete(launch.OwnedRoot, recursive: true);
        try
        {
            await CreateDirectoryLinkAsync(ancestor, parent);
            await CreateDirectoryLinkAsync(launch.OwnedRoot, _files.Path.Combine(ancestor, "child"));
            var logicalWork = launch.WorkingDirectory;
            var physicalWork = _files.Path.Combine(physicalRoot, "work");
            launch = launch with
            {
                Arguments = OperatingSystem.IsWindows()
                    ? ["/d", "/q", "/c", " >&2 echo crit: logical=" + logicalWork + "& >&2 echo crit: physical=" + physicalWork]
                    // The OS reports the physical working directory; a computed path would keep /var on macOS.
                    : ["-c", "printf '%s\\n' 'crit: logical=" + logicalWork + "' \"crit: physical=$(pwd -P)\" >&2"],
            };
            await using var owner = new OwnedMcpProcess(_files);
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Short child exit and diagnostic drain through linked roots.
            await owner.StartAsync(launch, guard.Token);
            await Assert.That(await owner.WaitForExitAsync(guard.Token)).IsEqualTo(0);
            await Assert.That(owner.StandardError).IsEquivalentTo(
                ["crit: logical=<run>" + _files.Path.DirectorySeparatorChar + "work", "crit: physical=<run>" + _files.Path.DirectorySeparatorChar + "work"],
                CollectionOrdering.Matching);
        }
        finally
        {
            if (_files.Directory.Exists(launch.OwnedRoot))
            {
                _files.Directory.Delete(launch.OwnedRoot);
            }
            if (_files.DirectoryInfo.New(ancestor).LinkTarget is not null)
            {
                _files.Directory.Delete(ancestor);
            }
            _files.Directory.Delete(physicalContainer, recursive: true);
        }
    }

    [Test]
    public async Task StartAsync_Should_Drain_And_Redact_Selected_Stderr_After_The_Retention_Cap()
    {
        var launch = DiagnosticLaunch(
            ">&2 echo ignored& >&2 echo fail: TOKEN=one apiKEY:two Secret=three PASSWORD:four& >&2 echo crit: " + new string('x', 3000) +
            "& (for /L %i in (1,1,300) do @>&2 echo fail: selected-%i)& >&2 echo info: Microsoft.Hosting.Lifetime[0]& >&2 echo       Now listening on: http://127.0.0.1:12345",
            "printf '%s\\n' ignored 'fail: TOKEN=one apiKEY:two Secret=three PASSWORD:four' 'crit: " + new string('x', 3000) +
            "' >&2; i=1; while [ \"$i\" -le 300 ]; do printf 'fail: selected-%s\\n' \"$i\" >&2; i=$((i+1)); done; " +
            "printf '%s\\n' 'info: Microsoft.Hosting.Lifetime[0]' '      Now listening on: http://127.0.0.1:12345' >&2");
        await using var owner = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Child stderr EOF, including lines beyond the cap.
        await owner.StartAsync(launch, guard.Token);
        var endpoint = await owner.WaitForHttpEndpointAsync(guard.Token);
        await Assert.That(endpoint.AbsoluteUri).IsEqualTo("http://127.0.0.1:12345/");
        await Assert.That(await owner.WaitForExitAsync(guard.Token)).IsEqualTo(0);
        await Assert.That(owner.StandardError).IsEquivalentTo(
            new[] { "fail: TOKEN=*** apiKEY:*** Secret=*** PASSWORD:***", "crit: " + new string('x', 1980) + "...[truncated]" }
                .Concat(Enumerable.Range(1, 198).Select(static index => "fail: selected-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("http://example.com:3456")]
    [Arguments("http://127.0.0.1:0")]
    [Arguments("https://127.0.0.1:3456")]
    public async Task WaitForHttpEndpointAsync_Should_Refuse_An_Invalid_Announcement_When_Stderr_Ends(string address)
    {
        var launch = DiagnosticLaunch(
            ">&2 echo info: Microsoft.Hosting.Lifetime[0]& >&2 echo       Now listening on: " + address,
            "printf '%s\\n' 'info: Microsoft.Hosting.Lifetime[0]' '      Now listening on: " + address + "' >&2");
        await using var owner = new OwnedMcpProcess(_files);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Invalid announcement followed by stderr EOF.
        await owner.StartAsync(launch, guard.Token);
        await Assert.That(async () => { await owner.WaitForHttpEndpointAsync(guard.Token); }).ThrowsExactly<InvalidOperationException>()
            .WithMessage("The child closed stderr before announcing an HTTP endpoint.", StringComparison.Ordinal);
        await Assert.That(await owner.WaitForExitAsync(guard.Token)).IsEqualTo(0);
    }

    [Test]
    public async Task WaitForHttpEndpointAsync_Should_Use_An_Infinite_Timeout_For_The_Linear_Lifetime_Expression()
    {
        await Assert.That(OwnedMcpProcess.ListeningExpressionTimeout).IsEqualTo(Regex.InfiniteMatchTimeout);
    }

    private ProcessLaunch Launch() => new ProductExecutable(_files).Resolve(new Dictionary<string, string?>(StringComparer.Ordinal));

    private Process CreateOwnedChild(ProcessLaunch launch)
    {
        var path = _files.Path.Combine(launch.OwnedRoot, OperatingSystem.IsWindows() ? "owned-child.exe" : "owned-child");
        var source = OperatingSystem.IsWindows() ? launch.FileName : "/bin/cat";
        _files.File.Copy(source, path);
        if (!OperatingSystem.IsWindows())
        {
            _files.File.SetUnixFileMode(path, _files.File.GetUnixFileMode(source));
        }
        var child = new Process
        {
            StartInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = OperatingSystem.IsWindows() ? "/d /e:on /v:off /c set /p hold=" : "",
            },
        };
        child.StartInfo.Environment.Clear();
        return child;
    }

    private IDirectory ForwardDirectory()
    {
        var directory = Substitute.For<IDirectory>();
        directory.CreateDirectory(Arg.Any<string>()).Returns(call => _files.Directory.CreateDirectory(call.Arg<string>()));
        directory.Exists(Arg.Any<string>()).Returns(call => _files.Directory.Exists(call.Arg<string>()));
        directory.ResolveLinkTarget(Arg.Any<string>(), returnFinalTarget: true)
            .Returns(call => _files.Directory.ResolveLinkTarget(call.Arg<string>(), returnFinalTarget: true));
        directory.EnumerateFiles(Arg.Any<string>(), Arg.Any<string>(), SearchOption.AllDirectories)
            .Returns(call => _files.Directory.EnumerateFiles(call.ArgAt<string>(0), call.ArgAt<string>(1), SearchOption.AllDirectories));
        return directory;
    }

    private IFileSystem DecorateDirectory(IDirectory directory)
    {
        var decorated = Substitute.For<IFileSystem>();
        decorated.Path.Returns(_files.Path);
        decorated.File.Returns(_files.File);
        decorated.Directory.Returns(directory);
        return decorated;
    }

    private ProcessLaunch ContainerDiagnosticLaunch() => DiagnosticLaunch("exit /b 0", "exit 0") with
    {
        Container = new ContainerLaunch("opencode-mcp-test-fixture", "registry.test/runtime@sha256:" + new string('b', 64), _files.Path.GetTempPath(), "/opencode-mcp-payload/opencode-mcp"),
    };

    private async Task CreateDirectoryLinkAsync(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            // Expand quoted paths in cmd, after ProcessStartInfo has encoded its argument list.
            var launch = DiagnosticLaunch("mklink /J %MCP_LINK% %MCP_TARGET%", "exit 0") with
            {
                Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["MCP_LINK"] = "\"" + link + "\"",
                    ["MCP_TARGET"] = "\"" + target + "\"",
                },
            };
            await using var owner = new OwnedMcpProcess(_files);
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // Junction creation by the owned command interpreter.
            await owner.StartAsync(launch, guard.Token);
            await Assert.That(await owner.WaitForExitAsync(guard.Token)).IsEqualTo(0);
        }
        else
        {
            _files.Directory.CreateSymbolicLink(link, target);
        }
        await Assert.That(_files.DirectoryInfo.New(link).LinkTarget).IsNotNull();
    }

    private ProcessLaunch DiagnosticLaunch(string windowsCommand, string unixCommand) => Launch() with
    {
        FileName = OperatingSystem.IsWindows() ? _files.Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh",
        Arguments = OperatingSystem.IsWindows() ? ["/d", "/q", "/c", windowsCommand] : ["-c", unixCommand],
    };
}
