using OpenCode.Mcp.Tools.Native.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeContainerOwner(INativeProcessRunner runner, NativeProcessRequest request, string name, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
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
        var removing = false;
        using (var removal = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            try
            {
                var result = await InvokeAsync(["rm", "-f", name], removal.Token).ConfigureAwait(false);
                removing = result.ExitCode != 0 && IsRemoving(result);
                if (result.ExitCode != 0 && !IsMissing(result) && !IsRemoving(result))
                {
                    failures.Add(new InvalidOperationException(result.Output.Trim()));
                }
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }
        using (var absence = new CancellationTokenSource(TimeSpan.FromSeconds(15), _clock))
        {
            NativeProcessResult? inspection = null;
            NativeProcessResult? listing = null;
            try
            {
                inspection = await InspectAsync(removing, absence.Token).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
            if (removing && inspection is null)
            {
                return failures.AsReadOnly();
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

    private async Task<NativeProcessResult> InspectAsync(bool removing, CancellationToken token)
    {
        var result = await InvokeAsync(["container", "inspect", name], token).ConfigureAwait(false);
        while (removing && result.ExitCode == 0)
        {
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                throw new InvalidOperationException(result.StandardError.Trim());
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), _clock, token).ConfigureAwait(false);
                result = await InvokeAsync(["container", "inspect", name], token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
        if (removing && InspectionError(result) is { } error)
        {
            throw new InvalidOperationException(error);
        }
        return result;
    }

    private string? InspectionError(NativeProcessResult inspection)
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
        return null;
    }

    private string? AbsenceError(NativeProcessResult inspection, NativeProcessResult listing)
    {
        var inspectionError = InspectionError(inspection);
        if (inspectionError is not null)
        {
            return inspectionError;
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

    private static bool IsRemoving(NativeProcessResult result) => result.StandardError.Trim().StartsWith("Error response from daemon: removal of container ", StringComparison.Ordinal)
        && result.StandardError.Trim().EndsWith(" is already in progress", StringComparison.Ordinal);

    private Task<NativeProcessResult> InvokeAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        // The runner observes the phase deadline and joins its CLI cleanup before returning ownership.
        return runner.RunAsync(request with { Arguments = arguments, Timeout = TimeSpan.FromSeconds(15), Container = null }, token);
    }
}
