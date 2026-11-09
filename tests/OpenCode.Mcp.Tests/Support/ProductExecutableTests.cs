using System.IO.Abstractions;
using System.Runtime.InteropServices;
using NSubstitute;
using OpenCode.Mcp.Tools.Native.Abstractions;
using Testably.Abstractions;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Checks exact artifact selection and launch ownership using the canonical filesystem double.</summary>
public sealed class ProductExecutableTests
{
    private readonly MockFileSystem _files = new();

    [Test]
    public async Task Resolve_Should_Select_A_Container_Only_When_All_Native_Inputs_Are_Present()
    {
        var executable = await ArrangeNativeAsync("linux", "x64");
        var inputs = Native(executable);
        inputs["OPENCODE_MCP_TEST_CONTAINER_IMAGE"] = "registry.test/runtime@sha256:" + new string('b', 64);
        inputs["kept"] = "value";
        var launch = new ProductExecutable(_files, "linux", Architecture.X64, Architecture.X64).Resolve(inputs);
        await Assert.That(launch.FileName).IsEqualTo("docker");
        await Assert.That(launch.Arguments).IsEmpty();
        await Assert.That(launch.Environment.Select(static item => item.Key + "=" + item.Value)).IsEquivalentTo(["kept=value"], CollectionOrdering.Matching);
        var container = await Assert.That(launch.Container).IsNotNull();
        await Assert.That(container.Image).IsEqualTo("registry.test/runtime@sha256:" + new string('b', 64));
        await Assert.That(container.PayloadDirectory).IsEqualTo(_files.Path.GetDirectoryName(executable));
        await Assert.That(container.Executable).IsEqualTo("/opencode-mcp-payload/opencode-mcp.exe");
        await Assert.That(container.Name).Matches("^opencode-mcp-test-[0-9a-f]{32}$");
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<NativeProcessRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new NativeProcessResult(0, "1001", "")));
        var context = await Assert.That(TestContext.Current).IsNotNull();
        var request = await new ContainerLaunchBuilder(_files, runner).BuildAsync(launch,
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["DOTNET_ROOT"] = "host-runtime" }, context.Execution.CancellationToken);
        await Assert.That(request.Environment["DOTNET_ROOT"]).IsEqualTo("host-runtime");
        await Assert.That(string.Join(' ', request.Arguments)).DoesNotContain("DOTNET_ROOT");
    }

    [Test]
    public async Task Resolve_Should_Preserve_Direct_Launches_When_The_Container_Input_Is_Absent()
    {
        var product = ArrangeManaged();
        var resolver = new ProductExecutable(_files);
        ProcessLaunch? managedResult = null;
        await Assert.That(() => managedResult = resolver.Resolve(new Dictionary<string, string?>(StringComparer.Ordinal))).ThrowsNothing();
        var managed = await Assert.That(managedResult).IsNotNull();
        await Assert.That(managed.FileName).IsEqualTo("dotnet");
        await Assert.That(managed.Arguments).IsEquivalentTo([product], CollectionOrdering.Matching);
        await Assert.That(managed.Container).IsNull();
        var executable = await ArrangeNativeAsync("win", "x64");
        var direct = resolver.Resolve(Explicit(executable));
        await Assert.That(direct.FileName).IsEqualTo(executable);
        await Assert.That(direct.Arguments).IsEmpty();
        await Assert.That(direct.Environment).IsEmpty();
        await Assert.That(direct.Container).IsNull();
    }

    [Test]
    [Arguments("win", Architecture.X64, Architecture.X64)]
    [Arguments("osx", Architecture.X64, Architecture.X64)]
    [Arguments("linux", Architecture.Arm64, Architecture.X64)]
    public async Task Resolve_Should_Require_A_Matching_Linux_Test_Host(string os, Architecture osArchitecture, Architecture processArchitecture)
    {
        var executable = await ArrangeNativeAsync(os, osArchitecture.ToString().ToLowerInvariant());
        var inputs = Native(executable);
        inputs["OPENCODE_MCP_TEST_CONTAINER_IMAGE"] = "registry.test/runtime@sha256:" + new string('b', 64);
        await Assert.That(() => new ProductExecutable(_files, os, osArchitecture, processArchitecture).Resolve(inputs))
            .ThrowsExactly<InvalidOperationException>().WithMessage("Container execution requires a matching Linux test host.", StringComparison.Ordinal);
    }

    [Test]
    [Arguments(null)]
    [Arguments("false")]
    public async Task Resolve_Should_Require_Both_Native_Selectors_For_A_Container(string? native)
    {
        var inputs = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OPENCODE_MCP_TEST_CONTAINER_IMAGE"] = "registry.test/runtime@sha256:" + new string('b', 64),
        };
        if (native is not null)
        {
            inputs["OPENCODE_MCP_TEST_EXPECT_NATIVE"] = native;
        }
        await Assert.That(() => new ProductExecutable(_files, "linux", Architecture.X64, Architecture.X64).Resolve(inputs)).ThrowsExactly<InvalidOperationException>()
            .WithMessage("Container execution requires OPENCODE_MCP_TEST_EXPECT_NATIVE=true.\nContainer execution requires OPENCODE_MCP_TEST_EXECUTABLE.", StringComparison.Ordinal);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("ubuntu:22.04")]
    [Arguments("ubuntu@sha256:123")]
    [Arguments("ubuntu@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa extra")]
    public async Task Resolve_Should_Refuse_Invalid_Container_Inputs_In_Validation_Order(string? image)
    {
        ArrangeManaged();
        await Assert.That(() => new ProductExecutable(_files, "linux", Architecture.X64, Architecture.X64).Resolve(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OPENCODE_MCP_TEST_CONTAINER_IMAGE"] = image,
        })).ThrowsExactly<InvalidOperationException>().WithMessage(
            "OPENCODE_MCP_TEST_CONTAINER_IMAGE must be digest-qualified (sha256).\nContainer execution requires OPENCODE_MCP_TEST_EXPECT_NATIVE=true.\nContainer execution requires OPENCODE_MCP_TEST_EXECUTABLE.", StringComparison.Ordinal);
    }

    [Test]
    public async Task Resolve_Should_Select_The_Fixed_Windows_Arm64_Fixture_On_An_Arm64_Windows_Os()
    {
        var executable = await ArrangeNativeAsync("win", "arm64");
        ProcessLaunch? launch = null;
        await Assert.That(() => launch = new ProductExecutable(_files, "win", Architecture.Arm64).Resolve(Native(executable))).ThrowsNothing();
        var selected = await Assert.That(launch).IsNotNull();
        await Assert.That(selected.FileName).IsEqualTo(executable);
        await Assert.That(selected.Arguments).IsEmpty();
    }

    [Test]
    public async Task Resolve_Should_Select_The_Fixed_Windows_X64_Fixture_On_An_X64_Windows_Os()
    {
        var executable = await ArrangeNativeAsync("win", "x64");
        var selected = new ProductExecutable(_files, "win", Architecture.X64).Resolve(Native(executable));
        await Assert.That(selected.FileName).IsEqualTo(executable);
        await Assert.That(selected.Arguments).IsEmpty();
    }

    [Test]
    public async Task Resolve_Should_Refuse_A_Managed_Pe_Renamed_As_An_Executable()
    {
        var path = _files.Path.Combine(_files.Path.GetTempPath(), "managed.exe");
        _files.Directory.CreateDirectory(_files.Path.GetTempPath());
        await _files.File.WriteAllBytesAsync(path, await new RealFileSystem().File.ReadAllBytesAsync(typeof(ProductExecutable).Assembly.Location));
        await Assert.That(() => new ProductExecutable(_files, "win", Architecture.X64).Resolve(Native(path))).ThrowsExactly<InvalidOperationException>()
            .WithMessage("Native execution needs a compatible win-x64 image; observed managed.", StringComparison.Ordinal);
    }

    [Test]
    [Arguments("yes")]
    [Arguments("")]
    [Arguments(null)]
    public async Task Resolve_Should_Refuse_An_Invalid_Native_Selector(string? value)
    {
        ArrangeManaged();
        await Assert.That(() => new ProductExecutable(_files).Resolve(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OPENCODE_MCP_TEST_EXPECT_NATIVE"] = value,
        })).ThrowsExactly<InvalidOperationException>()
            .WithMessage("OPENCODE_MCP_TEST_EXPECT_NATIVE must be true or false.", StringComparison.Ordinal);
    }

    [Test]
    public async Task Resolve_Should_Select_The_Exact_Native_Executable_When_Requirements_Are_Met()
    {
        var executable = await ArrangeNativeAsync("linux", "x64");
        var launch = new ProductExecutable(_files, "linux", Architecture.X64).Resolve(Native(executable));
        await Assert.That(launch.FileName).IsEqualTo(executable);
        await Assert.That(launch.Arguments).IsEquivalentTo(Array.Empty<string>(), CollectionOrdering.Matching);
        await Assert.That(launch.WorkingDirectory).IsEqualTo(_files.Path.Combine(launch.OwnedRoot, "work"));
        await Assert.That(_files.Directory.EnumerateFileSystemEntries(launch.WorkingDirectory)).IsEmpty();
        await Assert.That(launch.Environment).IsEmpty();
    }

    [Test]
    public async Task Resolve_Should_Remove_Native_Test_Selectors_When_Preparing_The_Child()
    {
        var executable = await ArrangeNativeAsync("win", "x64");
        var environment = Explicit(executable);
        environment.Add("OPENCODE_MCP_TEST_EXPECT_NATIVE", "false");
        environment.Add("kept", "value");
        var launch = new ProductExecutable(_files).Resolve(environment);
        await Assert.That(launch.Environment.Select(static item => item.Key + "=" + item.Value))
            .IsEquivalentTo(["kept=value"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Resolve_Should_Refuse_An_Incompatible_Architecture_When_Native_Is_Required()
    {
        var executable = await ArrangeNativeAsync("osx", "x64");
        await Assert.That(() => new ProductExecutable(_files, "osx", Architecture.Arm64).Resolve(Native(executable)))
            .ThrowsExactly<InvalidOperationException>()
            .WithMessage("Native execution needs a compatible osx-arm64 image; observed osx-x64.", StringComparison.Ordinal);
    }

    [Test]
    public async Task Resolve_Should_Refuse_An_Incompatible_Os_When_Native_Is_Required()
    {
        var executable = await ArrangeNativeAsync("linux", "x64");
        await Assert.That(() => new ProductExecutable(_files, "win", Architecture.X64).Resolve(Native(executable)))
            .ThrowsExactly<InvalidOperationException>()
            .WithMessage("Native execution needs a compatible win-x64 image; observed linux-x64.", StringComparison.Ordinal);
    }

    [Test]
    public async Task Resolve_Should_Refuse_Managed_Fallback_When_Native_Is_Required_Without_An_Executable()
    {
        ArrangeManaged();
        await Assert.That(() => new ProductExecutable(_files).Resolve(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OPENCODE_MCP_TEST_EXPECT_NATIVE"] = "true",
        })).ThrowsExactly<InvalidOperationException>()
            .WithMessage("Native execution requires OPENCODE_MCP_TEST_EXECUTABLE; managed fallback is forbidden.", StringComparison.Ordinal);
    }

    [Test]
    public async Task Resolve_Should_Select_The_Product_Dll_When_No_Explicit_Executable_Is_Supplied()
    {
        var product = ArrangeManaged();
        var launch = new ProductExecutable(_files).Resolve(new Dictionary<string, string?>(StringComparer.Ordinal));
        await Assert.That(launch.FileName).IsEqualTo("dotnet");
        await Assert.That(launch.Arguments).IsEquivalentTo([product], CollectionOrdering.Matching);
        await Assert.That(_files.Directory.Exists(launch.WorkingDirectory)).IsTrue();
        await Assert.That(_files.Directory.EnumerateFileSystemEntries(launch.WorkingDirectory)).IsEmpty();
        await Assert.That(launch.WorkingDirectory).StartsWith(_files.Path.GetTempPath());
    }

    [Test]
    public async Task Resolve_Should_Use_The_Supplied_Dotnet_Host_Path_When_Managed_Output_Is_Selected()
    {
        var product = ArrangeManaged();
        var host = _files.Path.Combine(_files.Path.GetTempPath(), "host with spaces", "dotnet.exe");
        var launch = new ProductExecutable(_files).Resolve(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DOTNET_HOST_PATH"] = host,
        });
        await Assert.That(launch.FileName).IsEqualTo(host);
        await Assert.That(launch.Arguments).IsEquivalentTo([product], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Resolve_Should_Select_The_Exact_Executable_When_An_Explicit_Path_Is_Supplied()
    {
        var executable = _files.Path.Combine(_files.Path.GetTempPath(), "product with spaces", "opencode-mcp.exe");
        _files.Directory.CreateDirectory(_files.Path.GetDirectoryName(executable) ?? throw new InvalidOperationException());
        _files.Initialize().WithFile(executable);
        var launch = new ProductExecutable(_files).Resolve(Explicit(executable));
        await Assert.That(launch.FileName).IsEqualTo(executable);
        await Assert.That(launch.Arguments).IsEmpty();
        await Assert.That(launch.Environment.Keys).DoesNotContain("OPENCODE_MCP_TEST_EXECUTABLE");
    }

    [Test]
    [Arguments("missing.exe", false)]
    [Arguments("managed.dll", true)]
    public async Task Resolve_Should_Refuse_The_Path_When_An_Explicit_Executable_Is_Invalid(string name, bool exists)
    {
        ArrangeManaged();
        var path = _files.Path.Combine(_files.Path.GetTempPath(), name);
        if (exists)
        {
            _files.Initialize().WithFile(path);
        }
        await Assert.That(() => new ProductExecutable(_files).Resolve(Explicit(path)))
            .ThrowsExactly<InvalidOperationException>()
            .WithMessage("The explicit product executable must exist and must not be a DLL: " + path, StringComparison.Ordinal);
    }

    [Test]
    public async Task Resolve_Should_Report_Missing_Output_When_The_Managed_Product_Is_Not_Built()
    {
        var product = ArrangeManaged();
        _files.File.Delete(product);
        await Assert.That(() => new ProductExecutable(_files).Resolve(new Dictionary<string, string?>(StringComparer.Ordinal)))
            .ThrowsExactly<InvalidOperationException>()
            .WithMessage("Build the Release product before executable tests; missing artifact: " + product, StringComparison.Ordinal);
    }

    [Test]
    public async Task Resolve_Should_Propagate_The_Failure_When_Artifact_Access_Fails()
    {
        var decorated = Substitute.For<IFileSystem>();
        decorated.Path.Returns(_files.Path);
        var failure = new IOException("artifact access failed");
        decorated.File.Returns(_ => throw failure);
        var error = await Assert.That(() => new ProductExecutable(decorated).Resolve(Explicit("product.exe"))).ThrowsExactly<IOException>();
        await Assert.That(error).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task ProcessLaunch_Should_Preserve_Snapshots_When_Caller_Collections_Are_Mutated()
    {
        string[] arguments = ["target with spaces.dll", "--transport", "stdio"];
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["explicit"] = "before" };
        var launch = new ProcessLaunch { FileName = "dotnet", Arguments = arguments, Environment = environment, OwnedRoot = "root", WorkingDirectory = "work" };
        var replacement = launch with { Arguments = arguments, Environment = environment };
        arguments[0] = "wrong.dll";
        environment["explicit"] = "after";
        foreach (var snapshot in new[] { launch, replacement })
        {
            await Assert.That(snapshot.Arguments).IsEquivalentTo(["target with spaces.dll", "--transport", "stdio"], CollectionOrdering.Matching);
            await Assert.That(snapshot.Environment["explicit"]).IsEqualTo("before");
            await Assert.That(snapshot.Arguments).IsNotTypeOf<string[]>();
        }
    }

    private string ArrangeManaged()
    {
        var root = _files.Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        _files.Directory.CreateDirectory(root);
        _files.Initialize().WithFile(_files.Path.Combine(root, "OpenCode.Mcp.slnx"));
        var output = _files.Path.Combine(root, "src", "OpenCode.Mcp", "bin", "Release", "net10.0");
        _files.Directory.CreateDirectory(output);
        foreach (var name in new[] { "OpenCode.Mcp.dll", "OpenCode.Mcp.runtimeconfig.json", "OpenCode.Mcp.deps.json" })
        {
            _files.Initialize().WithFile(_files.Path.Combine(output, name));
        }
        return _files.Path.Combine(output, "OpenCode.Mcp.dll");
    }

    private async Task<string> ArrangeNativeAsync(string os, string architecture)
    {
        var executable = _files.Path.Combine(_files.Path.GetTempPath(), "native with spaces", "opencode-mcp.exe");
        _files.Directory.CreateDirectory(_files.Path.GetDirectoryName(executable) ?? throw new InvalidOperationException());
        var image = new byte[512];
        if (os == "win")
        {
            image[0] = 0x4d;
            image[1] = 0x5a;
            image[0x3c] = 0x80;
            image[0x80] = 0x50;
            image[0x81] = 0x45;
            image[0x84] = 0x64;
            image[0x85] = architecture == "x64" ? (byte)0x86 : (byte)0xaa;
            image[0x94] = 240;
            image[0x98] = 0x0b;
            image[0x99] = 0x02;
        }
        else if (os == "linux")
        {
            image[0] = 0x7f;
            image[1] = 0x45;
            image[2] = 0x4c;
            image[3] = 0x46;
            image[4] = 2;
            image[5] = 1;
            image[18] = architecture == "x64" ? (byte)62 : (byte)183;
        }
        else
        {
            image[0] = 0xcf;
            image[1] = 0xfa;
            image[2] = 0xed;
            image[3] = 0xfe;
            image[4] = architecture == "x64" ? (byte)7 : (byte)12;
            image[7] = 1;
        }
        await _files.File.WriteAllBytesAsync(executable, image);
        return executable;
    }

    private static Dictionary<string, string?> Native(string path) => new(StringComparer.Ordinal)
    {
        ["OPENCODE_MCP_TEST_EXECUTABLE"] = path,
        ["OPENCODE_MCP_TEST_EXPECT_NATIVE"] = "true",
    };

    private static Dictionary<string, string?> Explicit(string path) => new(StringComparer.Ordinal)
    {
        ["OPENCODE_MCP_TEST_EXECUTABLE"] = path,
    };
}
