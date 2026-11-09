using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed class NativeContainerOwnerTests
{
    private const string Name = "opencode-mcp-test-fixture";
    private readonly FakeNativeProcessRunner _runner = new();

    [Test]
    [Timeout(30_000)]
    [Arguments("opencode-mcp-test-fixture")]
    [Arguments("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public async Task CleanupAsync_Should_Check_Absence_When_Auto_Removal_Is_In_Progress(string identity, CancellationToken token)
    {
        _runner.Response = (request, _) => Task.FromResult(request.Arguments[0] == "rm"
            ? new NativeProcessResult(1, "", "Error response from daemon: removal of container " + identity + " is already in progress")
            : MissingOrListing(request));
        var owner = Owner();
        var errors = await owner.CleanupAsync().WaitAsync(token);
        await Assert.That(errors).IsEmpty();
        await Assert.That(owner.AbsenceProven).IsTrue();
        await Assert.That(_runner.Calls.Select(static call => string.Join(' ', call.Arguments)))
            .IsEquivalentTo(["rm -f " + Name, "container inspect " + Name, "container ls --all --no-trunc --format {{.Names}}"], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task CleanupAsync_Should_Preserve_Nested_Operation_Failures_Before_Later_Cleanup_Failures(CancellationToken token)
    {
        _runner.Response = static (request, _) =>
        {
            if (request.Arguments[0] == "rm")
            {
                return Task.FromResult(new NativeProcessResult(1, "", "removal failed"));
            }
            return Task.FromResult(request.Arguments[1] == "inspect"
                ? new NativeProcessResult(1, "", "absence failed") : new NativeProcessResult(0, "", ""));
        };
        var original = new AggregateException(new AggregateException(new IOException("original operation")), new IOException("CLI termination"));
        var errors = await Owner().CleanupAsync(original).WaitAsync(token);
        await Assert.That(errors.Select(static error => error.Message))
            .IsEquivalentTo(["original operation", "CLI termination", "removal failed", "absence failed"], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task CleanupAsync_Should_Accept_Auto_Removal_Only_After_Absence_Is_Proven(CancellationToken token)
    {
        _runner.Response = static (request, _) => Task.FromResult(request.Arguments[0] == "rm"
            ? new NativeProcessResult(1, "", "Error response from daemon: No such container: " + Name)
            : MissingOrListing(request));
        var owner = Owner();
        await Assert.That(await owner.CleanupAsync().WaitAsync(token)).IsEmpty();
        await Assert.That(owner.AbsenceProven).IsTrue();
        await Assert.That(await owner.CleanupAsync().WaitAsync(token)).IsEmpty();
        await Assert.That(_runner.Calls.Select(static call => string.Join(' ', call.Arguments)))
            .IsEquivalentTo(["rm -f " + Name, "container inspect " + Name, "container ls --all --no-trunc --format {{.Names}}"], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("daemon", "Cannot connect to the Docker daemon")]
    [Arguments("present", "Container remains: opencode-mcp-test-fixture")]
    [Arguments("listing", "Container remains: opencode-mcp-test-fixture")]
    [Arguments("malformed", "Unrecognized container inspection response")]
    [Arguments("listing failure", "Container listing failed: access denied")]
    [Arguments("malformed listing", "Malformed container listing")]
    [Arguments("wrong name", "Error: No such container: opencode-mcp-test-fixture-sibling")]
    public async Task CleanupAsync_Should_Reject_Daemon_Errors_And_A_Still_Present_Container(string defect, string diagnostic, CancellationToken token)
    {
        _runner.Response = (request, _) => Task.FromResult(request.Arguments[0] == "rm" ? new NativeProcessResult(0, Name, "")
            : Response(request, defect));
        var owner = Owner();
        var errors = await owner.CleanupAsync().WaitAsync(token);
        await Assert.That(errors.Select(static error => error.Message)).IsEquivalentTo([diagnostic], CollectionOrdering.Matching);
        await Assert.That(owner.AbsenceProven).IsFalse();
        await Assert.That(_runner.Calls.Select(static call => string.Join(' ', call.Arguments)))
            .IsEquivalentTo(["rm -f " + Name, "container inspect " + Name, "container ls --all --no-trunc --format {{.Names}}"], CollectionOrdering.Matching);
    }

    [Test]
    [Timeout(30_000)]
    public async Task CleanupAsync_Should_Preserve_Original_And_Cleanup_Failures_In_Order(CancellationToken token)
    {
        _runner.Response = static (request, _) => Task.FromResult(request.Arguments[0] == "rm"
            ? new NativeProcessResult(1, "", "removal failed") : MissingOrListing(request));
        var original = new IOException("original operation");
        var errors = await Owner().CleanupAsync(original).WaitAsync(token);
        await Assert.That(errors.Select(static error => error.Message)).IsEquivalentTo(["original operation", "removal failed"], CollectionOrdering.Matching);
        await Assert.That(errors[0]).IsSameReferenceAs(original);
    }

    private NativeContainerOwner Owner() => new(_runner,
        new NativeProcessRequest("docker", [], ".", new Dictionary<string, string?>(StringComparer.Ordinal), TimeSpan.FromSeconds(15)) { InheritEnvironment = false }, Name);

    private static NativeProcessResult MissingOrListing(NativeProcessRequest request) => request.Arguments[1] == "inspect"
        ? new NativeProcessResult(1, "[]", "Error: No such container: " + Name) : new NativeProcessResult(0, Name + "-sibling\n", "");

    private static NativeProcessResult Response(NativeProcessRequest request, string defect)
    {
        if (request.Arguments[1] == "ls")
        {
            if (defect == "listing failure")
            {
                return new NativeProcessResult(1, "", "access denied");
            }
            if (defect == "malformed listing")
            {
                return new NativeProcessResult(0, "bad name", "");
            }
            return new NativeProcessResult(0, defect == "listing" ? Name + "\n" : "", "");
        }
        return defect switch
        {
            "daemon" => new NativeProcessResult(1, "", "Cannot connect to the Docker daemon"),
            "present" => new NativeProcessResult(0, "[]", ""),
            "malformed" => new NativeProcessResult(1, "malformed", "Error: No such container: " + Name),
            "wrong name" => new NativeProcessResult(1, "[]", "Error: No such container: " + Name + "-sibling"),
            _ => MissingOrListing(request),
        };
    }
}
