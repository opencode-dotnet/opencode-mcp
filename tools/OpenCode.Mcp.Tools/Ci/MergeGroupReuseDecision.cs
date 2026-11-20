using System.Globalization;
using System.Text.Json;

namespace OpenCode.Mcp.Tools.Ci;

internal static class MergeGroupReuseDecision
{
    internal const string ApprovedReason = "all rules hold";

    public static string Reason(IReadOnlyDictionary<string, string> facts)
    {
        try
        {
            var head = facts["head"].Trim();
            if (!TryGetGroupNumber(facts, head, out var prNumber))
            {
                return "rule 1: invalid group";
            }
            using var pr = JsonDocument.Parse(facts["pr.json"]);
            var pull = pr.RootElement;
            if (pull.GetProperty("number").GetInt64() != prNumber || !pull.GetProperty("head").GetProperty("sha").ValueEquals(head)
                || !pull.GetProperty("base").GetProperty("ref").ValueEquals("master"))
            {
                return "rule 2: PR changed";
            }
            using var runs = JsonDocument.Parse(facts["runs.json"]);
            var latest = LatestRun(runs.RootElement, head);
            if (latest.ValueKind == JsonValueKind.Undefined || !latest.GetProperty("status").ValueEquals("completed")
                || !latest.GetProperty("conclusion").ValueEquals("success") || latest.GetProperty("run_attempt").GetInt64() < 1)
            {
                return "rule 3: no green latest CI result";
            }
            var attempt = latest.GetProperty("run_attempt").GetInt64();
            var id = latest.GetProperty("id").GetInt64();
            using var jobs = JsonDocument.Parse(facts["jobs.json"]);
            var attemptJobs = jobs.RootElement.GetProperty("jobs").EnumerateArray()
                .Where(job => job.GetProperty("run_id").GetInt64() == id && job.GetProperty("run_attempt").GetInt64() == attempt)
                .ToArray();
            if (SuccessfulJob(attemptJobs, "CI result").ValueKind == JsonValueKind.Undefined)
            {
                return "rule 3: no green latest CI result";
            }
            if (!HasArtifact(facts["artifacts.json"], attempt, SuccessfulJob(attemptJobs, "Changes")) || !TreesMatch(facts))
            {
                return "rule 4: unproven tree";
            }
            return facts["changed-paths"].Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(IsGuarded)
                ? "rule 5: guarded path"
                : ApprovedReason;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return "malformed facts";
        }
    }

    private static bool TryGetGroupNumber(IReadOnlyDictionary<string, string> facts, string head, out long number)
    {
        number = 0;
        var queue = facts["queue-ref"].Trim().Split('/');
        return facts["parent-count"].Trim() == "2" && queue.Length == 5
            && string.Join('/', queue[..4]) == "refs/heads/gh-readonly-queue/master"
            && queue[4].StartsWith("pr-", StringComparison.Ordinal)
            && long.TryParse(queue[4].Split('-').ElementAtOrDefault(1), CultureInfo.InvariantCulture, out number)
            && number > 0 && IsHash(head) && IsHash(facts["base"].Trim());
    }

    private static JsonElement LatestRun(JsonElement runs, string head) => runs.GetProperty("workflow_runs").EnumerateArray()
        .Where(run => run.GetProperty("path").ValueEquals(".github/workflows/ci.yml")
            && run.GetProperty("event").ValueEquals("pull_request") && run.GetProperty("head_sha").ValueEquals(head))
        .OrderByDescending(static run => Date(run, "created_at"))
        .ThenByDescending(static run => run.GetProperty("id").GetInt64())
        .FirstOrDefault();

    private static JsonElement SuccessfulJob(IEnumerable<JsonElement> jobs, string name)
    {
        var matches = jobs.Where(job => job.GetProperty("name").ValueEquals(name)).ToArray();
        return matches.Length == 1 && matches[0].GetProperty("conclusion").ValueEquals("success") ? matches[0] : default;
    }

    private static bool HasArtifact(string json, long attempt, JsonElement changes)
    {
        using var artifacts = JsonDocument.Parse(json);
        var name = "tested-tree-" + attempt.ToString(CultureInfo.InvariantCulture);
        var matches = artifacts.RootElement.GetProperty("artifacts").EnumerateArray()
            .Where(artifact => artifact.GetProperty("name").ValueEquals(name)).ToArray();
        if (matches.Length != 1 || matches[0].GetProperty("expired").GetBoolean() || changes.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }
        var created = Date(matches[0], "created_at");
        return created >= Date(changes, "started_at") && created <= Date(changes, "completed_at");
    }

    private static bool TreesMatch(IReadOnlyDictionary<string, string> facts)
    {
        var testedTree = facts["tested-tree"];
        var testedTrees = (testedTree.EndsWith('\n') ? testedTree[..^1] : testedTree).Split('\n', StringSplitOptions.TrimEntries);
        var tree = facts["tree"].Trim();
        var baseTree = facts["base-tree"].Trim();
        return testedTrees.Length == 2 && IsHash(tree) && tree == testedTrees[0] && IsHash(baseTree) && baseTree == testedTrees[1];
    }

    private static bool IsHash(string value) => value.Length == 40 && value.All(char.IsAsciiHexDigit);

    private static DateTimeOffset Date(JsonElement value, string field) =>
        DateTimeOffset.Parse(value.GetProperty(field).GetString() ?? throw new FormatException("Missing timestamp."), CultureInfo.InvariantCulture);

    private static bool IsGuarded(string path)
    {
        var leaf = path.Split('/')[^1];
        // Keep these guarded paths in sync with the reuse guard in .github/workflows/ci.yml.
        return path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("tools/", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(leaf, "Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            || string.Equals(leaf, "global.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(leaf, "nuget.config", StringComparison.OrdinalIgnoreCase);
    }
}
