using OpenCode.Mcp.Hosting.Abstractions;
using OpenCode.Mcp.Hosting.Models;

namespace OpenCode.Mcp.Hosting;

/// <summary>Runs the selected host and reports transport-selection refusals.</summary>
public sealed class McpApplication
{
    private readonly IMcpHostFactory _factory;
    private readonly TextWriter _errors;

    /// <summary>Receives the host-creation and diagnostic-output boundaries.</summary>
    /// <param name="factory">Creates a host only after selection succeeds.</param>
    /// <param name="errors">Receives selection diagnostics; ownership stays with the caller.</param>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    public McpApplication(IMcpHostFactory factory, TextWriter errors)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(errors);
        _factory = factory;
        _errors = errors;
    }

    /// <summary>Validates transport selection before running and disposing the selected host.</summary>
    /// <param name="arguments">The command-line tokens.</param>
    /// <param name="cancellationToken">Requests host shutdown and cancels diagnostic output.</param>
    /// <returns>Two for a selector refusal; zero after normal host shutdown.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        LaunchOptions options;
        try
        {
            options = TransportSelector.Parse(arguments);
        }
        catch (ArgumentException error)
        {
            await _errors.WriteLineAsync(error.Message.AsMemory(), cancellationToken).ConfigureAwait(false);
            return 2;
        }

        await _factory.Create(options).RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
