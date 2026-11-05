using System.IO.Abstractions;
using NSubstitute;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Checks exact artifact selection and launch ownership using the canonical filesystem double.</summary>
public sealed class ProductExecutableTests
{
    private readonly MockFileSystem _files = new();

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

    private static Dictionary<string, string?> Explicit(string path) => new(StringComparer.Ordinal)
    {
        ["OPENCODE_MCP_TEST_EXECUTABLE"] = path,
    };
}
