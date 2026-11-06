using System.IO.Abstractions;
using System.Runtime.InteropServices;
using NSubstitute;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Checks exact artifact selection and launch ownership using the canonical filesystem double.</summary>
public sealed class ProductExecutableTests
{
    private readonly MockFileSystem _files = new();

    [Test]
    public async Task Resolve_Should_Select_The_Exact_Native_Executable_When_Requirements_Are_Met()
    {
        var host = HostIdentity();
        var executable = await ArrangeNativeAsync(host[..host.IndexOf('-', StringComparison.Ordinal)],
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
        var launch = new ProductExecutable(_files).Resolve(Native(executable));
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
        var host = HostIdentity();
        var os = host[..host.IndexOf('-', StringComparison.Ordinal)];
        var architecture = RuntimeInformation.OSArchitecture == Architecture.X64 ? "arm64" : "x64";
        var executable = await ArrangeNativeAsync(os, architecture);
        await Assert.That(() => new ProductExecutable(_files).Resolve(Native(executable)))
            .ThrowsExactly<InvalidOperationException>()
            .WithMessage($"Native execution needs a compatible {host} image; observed {os}-{architecture}.", StringComparison.Ordinal);
    }

    [Test]
    public async Task Resolve_Should_Refuse_An_Incompatible_Os_When_Native_Is_Required()
    {
        var foreignOs = OperatingSystem.IsWindows() ? "linux" : "win";
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var executable = await ArrangeNativeAsync(foreignOs, architecture);
        await Assert.That(() => new ProductExecutable(_files).Resolve(Native(executable)))
            .ThrowsExactly<InvalidOperationException>()
            .WithMessage($"Native execution needs a compatible {HostIdentity()} image; observed {foreignOs}-{architecture}.", StringComparison.Ordinal);
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

    private static string HostIdentity()
    {
        var os = "osx";
        if (OperatingSystem.IsWindows())
        {
            os = "win";
        }
        else if (OperatingSystem.IsLinux())
        {
            os = "linux";
        }
        return os + "-" + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
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
