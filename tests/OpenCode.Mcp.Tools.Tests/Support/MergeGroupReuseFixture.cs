using System.IO.Abstractions;
using System.Text.Json;

namespace OpenCode.Mcp.Tools.Tests.Support;

internal sealed class MergeGroupReuseFixture
{
    public const string ApprovedReason = "all rules hold";
    public const string InvalidGroupReason = "rule 1: invalid group";
    public const string ChangedPullRequestReason = "rule 2: PR changed";
    public const string NoGreenResultReason = "rule 3: no green latest CI result";
    public const string UnprovenTreeReason = "rule 4: unproven tree";
    public const string GuardedPathReason = "rule 5: guarded path";
    public const string MalformedFactsReason = "malformed facts";
    public const string UnreadableFactsReason = "unreadable facts";

    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Base = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Tree = "cccccccccccccccccccccccccccccccccccccccc";
    private const string BaseTree = "dddddddddddddddddddddddddddddddddddddddd";
    private const string Workflow = ".github/workflows/ci.yml";
    private const string ArtifactName = "tested-tree-2";
    private const string Latest = "2026-11-20T08:00:00Z";
    private const string Started = "2026-11-20T08:01:00Z";
    private const string Uploaded = "2026-11-20T08:02:00Z";
    private const string Completed = "2026-11-20T08:03:00Z";

    private readonly Dictionary<string, string> facts = new(StringComparer.Ordinal)
    {
        ["queue-ref"] = "refs/heads/gh-readonly-queue/master/pr-42-" + Base + "\n",
        ["parent-count"] = "2\n",
        ["head"] = Head + "\n",
        ["base"] = Base + "\n",
        ["tree"] = Tree + "\n",
        ["base-tree"] = BaseTree + "\n",
        ["tested-tree"] = Tree + "\n" + BaseTree + "\n",
        ["changed-paths"] = "src/ordinary.cs\0doc/readme.md\0",
        ["pr.json"] = PullRequest(Head),
        ["runs.json"] = Runs(Run(109, "2026-11-19T08:00:00Z"), Run(100, Latest), Run(101, Latest)),
        ["jobs.json"] = Jobs(Job("CI result"), Job("Changes")),
        ["artifacts.json"] = Artifacts(Artifact()),
    };

    public IReadOnlyDictionary<string, string> Facts => facts;

    public MergeGroupReuseFixture WithRow(string row)
    {
        if (row == "all")
        {
            return this;
        }
        if (row is "non-hash-tree" or "non-hash-base-tree")
        {
            var tree = row == "non-hash-tree" ? "not-a-hash" : Tree;
            var baseTree = row == "non-hash-base-tree" ? "not-a-hash" : BaseTree;
            facts["tree"] = tree;
            facts["base-tree"] = baseTree;
            facts["tested-tree"] = tree + "\n" + baseTree + "\n";
            return this;
        }
        var change = row switch
        {
            "one-parent" => ("parent-count", "1"),
            "invalid-queue-ref" => ("queue-ref", "refs/heads/gh-readonly-queue/main/pr-42-base"),
            "invalid-pr-number" => ("queue-ref", "refs/heads/gh-readonly-queue/master/pr-0-base"),
            "non-pr-queue-ref" => ("queue-ref", "refs/heads/gh-readonly-queue/master/rq-42-deadbeef"),
            "invalid-head" => ("head", Head[..39]),
            "invalid-base" => ("base", "g" + Base[1..]),
            "moved-head" => ("pr.json", PullRequest(Base)),
            "feature-base" => ("pr.json", PullRequest(Head, branch: "feature")),
            "different-pr" => ("pr.json", PullRequest(Head, number: 43)),
            "no-run" => ("runs.json", Runs()),
            "failed-attempt" => ("runs.json", Runs(Run(101, Latest, conclusion: "failure"))),
            "running-attempt" => ("runs.json", Runs(Run(101, Latest, status: "in_progress", conclusion: null))),
            "invalid-attempt" => ("runs.json", Runs(Run(101, Latest, attempt: 0))),
            "newer-failed-run" => ("runs.json", Runs(Run(101, Latest), Run(102, Latest, conclusion: "failure"))),
            "other-workflow" => ("runs.json", Runs(Run(101, Latest, path: ".github/workflows/other.yml"))),
            "other-event" => ("runs.json", Runs(Run(101, Latest, eventName: "push"))),
            "other-run-head" => ("runs.json", Runs(Run(101, Latest, head: Base))),
            "failed-result" => ("jobs.json", Jobs(Job("CI result", conclusion: "failure"), Job("Changes"))),
            "old-result" => ("jobs.json", Jobs(Job("CI result", attempt: 1), Job("Changes"))),
            "other-run-result" => ("jobs.json", Jobs(Job("CI result", runId: 100), Job("Changes"))),
            "duplicate-result" => ("jobs.json", Jobs(Job("CI result"), Job("CI result"), Job("Changes"))),
            "no-artifact" => ("artifacts.json", Artifacts()),
            "old-artifact" => ("artifacts.json", Artifacts(Artifact(name: "tested-tree-1"))),
            "different-tree" => ("tree", Base),
            "different-base-tree" => ("base-tree", Base),
            "missing-base-tree" => ("tested-tree", Tree + "\n"),
            "extra-tested-tree-line" => ("tested-tree", Tree + "\n" + BaseTree + "\n" + Base + "\n"),
            "blank-tested-tree-line" => ("tested-tree", Tree + "\n\n" + BaseTree + "\n"),
            "extra-tested-tree-newline" => ("tested-tree", Tree + "\n" + BaseTree + "\n\n"),
            "crlf-tested-tree" => ("tested-tree", Tree + "\r\n" + BaseTree + "\r\n"),
            "empty-base-tree" => ("base-tree", ""),
            "replacement-after" => ("artifacts.json", Artifacts(Artifact(created: "2026-11-20T08:04:00Z"))),
            "replacement-before" => ("artifacts.json", Artifacts(Artifact(created: Latest))),
            "artifact-at-start" => ("artifacts.json", Artifacts(Artifact(created: Started))),
            "artifact-at-end" => ("artifacts.json", Artifacts(Artifact(created: Completed))),
            "duplicate-artifact" => ("artifacts.json", Artifacts(Artifact(), Artifact())),
            "expired-artifact" => ("artifacts.json", Artifacts(Artifact(expired: true))),
            "failed-changes" => ("jobs.json", Jobs(Job("CI result"), Job("Changes", conclusion: "failure"))),
            "malformed" => ("pr.json", "{"),
            "wrong-json-type" => ("artifacts.json", Artifacts(new { name = ArtifactName, expired = "no", created_at = Uploaded })),
            "null-run-time" => ("runs.json", Runs(Run(101, null))),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown reuse fact row."),
        };
        facts[change.Item1] = change.Item2;
        return this;
    }

    public MergeGroupReuseFixture WithChangedPath(string path)
    {
        facts["changed-paths"] += path + "\0";
        return this;
    }

    public MergeGroupReuseFixture WithoutTree()
    {
        _ = facts.Remove("tree");
        return this;
    }

    public async Task WriteAsync(IFileSystem files, string directory, CancellationToken token)
    {
        _ = files.Directory.CreateDirectory(directory);
        foreach (var (name, content) in facts)
        {
            await files.File.WriteAllTextAsync(files.Path.Combine(directory, name), content, token).ConfigureAwait(false);
        }
    }

    private static string PullRequest(string head, string branch = "master", long number = 42) =>
        JsonSerializer.Serialize(new { number, head = new { sha = head }, @base = new { @ref = branch } });

    private static string Runs(params object[] runs) => JsonSerializer.Serialize(new { workflow_runs = runs });

    private static object Run(long id, string? created, string status = "completed", string? conclusion = "success",
        string path = Workflow, long attempt = 2, string eventName = "pull_request", string head = Head) =>
        new { id, path, @event = eventName, head_sha = head, created_at = created, status, conclusion, run_attempt = attempt };

    private static string Jobs(params object[] jobs) => JsonSerializer.Serialize(new { jobs });

    private static object Job(string name, string conclusion = "success", long attempt = 2, long runId = 101) =>
        new { name, conclusion, run_id = runId, run_attempt = attempt, started_at = Started, completed_at = Completed };

    private static string Artifacts(params object[] artifacts) => JsonSerializer.Serialize(new { artifacts });

    private static object Artifact(string name = ArtifactName, string created = Uploaded, bool expired = false) =>
        new { name, expired, created_at = created };
}
