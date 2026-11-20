using System.IO.Abstractions;

namespace OpenCode.Mcp.Tools.Ci;

internal sealed class MergeGroupReuseRunner(IFileSystem files, TextWriter output)
{
    public async Task RunAsync(string factsDirectory, string outputFile, CancellationToken token)
    {
        var facts = await ReadFactsAsync(factsDirectory, token).ConfigureAwait(false);
        var reason = facts is null ? "unreadable facts" : MergeGroupReuseDecision.Reason(facts);
        try
        {
            var reuse = reason == MergeGroupReuseDecision.ApprovedReason ? "true" : "false";
            await files.File.AppendAllTextAsync(outputFile, $"reuse={reuse}\nreason={reason}\n", token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await output.WriteLineAsync($"Cannot write reuse decision: {exception.Message}".AsMemory(), token).ConfigureAwait(false);
        }
        await output.WriteLineAsync(reason.AsMemory(), token).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>?> ReadFactsAsync(string factsDirectory, CancellationToken token)
    {
        string[] names = ["queue-ref", "parent-count", "head", "base", "base-tree", "tree", "tested-tree", "changed-paths", "pr.json", "runs.json", "jobs.json", "artifacts.json"];
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var name in names)
            {
                facts.Add(name, await files.File.ReadAllTextAsync(files.Path.Combine(factsDirectory, name), token).ConfigureAwait(false));
            }
            return facts;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await output.WriteLineAsync($"Cannot read reuse facts: {exception.Message}".AsMemory(), token).ConfigureAwait(false);
            return null;
        }
    }
}
