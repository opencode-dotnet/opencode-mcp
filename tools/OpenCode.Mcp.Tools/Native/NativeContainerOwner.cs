using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeContainerOwner(INativeProcessRunner runner, NativeProcessRequest request, string name)
{
    private Task<IReadOnlyList<Exception>>? _cleanup;
    public bool AbsenceProven { get; private set; }

    public async Task<IReadOnlyList<Exception>> CleanupAsync(Exception? original = null)
    {
        var failures = await (_cleanup ??= RemoveAndVerifyAsync()).ConfigureAwait(false);
        if (original is null)
        {
            return failures;
        }
        var originals = OrderedFailures(original).ToArray();
        return [.. originals, .. failures.Except<Exception>(originals, ReferenceEqualityComparer.Instance)];
    }

    private static IEnumerable<Exception> OrderedFailures(Exception failure) => failure is AggregateException aggregate
        ? aggregate.InnerExceptions.SelectMany(OrderedFailures) : [failure];

    private async Task<IReadOnlyList<Exception>> RemoveAndVerifyAsync()
    {
        var failures = new List<Exception>();
        using (var removal = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            try
            {
                var result = await InvokeAsync(["rm", "-f", name], removal.Token).ConfigureAwait(false);
                if (result.ExitCode != 0 && !IsMissing(result))
                {
                    failures.Add(new InvalidOperationException(result.Output.Trim()));
                }
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }
        using (var absence = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            NativeProcessResult? inspection = null;
            NativeProcessResult? listing = null;
            try
            {
                inspection = await InvokeAsync(["container", "inspect", name], absence.Token).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
            try
            {
                listing = await InvokeAsync(["container", "ls", "--all", "--no-trunc", "--format", "{{.Names}}"], absence.Token).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
            if (inspection is not null && listing is not null)
            {
                var error = AbsenceError(inspection, listing);
                AbsenceProven = error is null;
                if (error is not null)
                {
                    failures.Add(new InvalidOperationException(error));
                }
            }
        }
        return failures.AsReadOnly();
    }

    private string? AbsenceError(NativeProcessResult inspection, NativeProcessResult listing)
    {
        if (inspection.ExitCode == 0)
        {
            return "Container remains: " + name;
        }
        if (!IsMissing(inspection))
        {
            return string.IsNullOrWhiteSpace(inspection.StandardError) ? "Unrecognized container inspection response" : inspection.StandardError.Trim();
        }
        if (inspection.StandardOutput.Trim() is not ("" or "[]"))
        {
            return "Unrecognized container inspection response";
        }
        if (listing.ExitCode != 0 || !string.IsNullOrWhiteSpace(listing.StandardError))
        {
            return "Container listing failed: " + listing.Output.Trim();
        }
        var names = listing.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => line.TrimEnd('\r')).ToArray();
        if (names.Contains(name, StringComparer.Ordinal))
        {
            return "Container remains: " + name;
        }
        return names.Any(static value => value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.')))
            ? "Malformed container listing" : null;
    }

    private bool IsMissing(NativeProcessResult result) => result.StandardError.Trim() == "Error: No such container: " + name
        || result.StandardError.Trim() == "Error response from daemon: No such container: " + name;

    private Task<NativeProcessResult> InvokeAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        // The runner observes the phase deadline and joins its CLI cleanup before returning ownership.
        return runner.RunAsync(request with { Arguments = arguments, Timeout = TimeSpan.FromSeconds(15), Container = null }, token);
    }
}
