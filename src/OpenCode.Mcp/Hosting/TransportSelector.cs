using OpenCode.Mcp.Hosting.Models;

namespace OpenCode.Mcp.Hosting;

/// <summary>Selects the protocol channel before any host is composed.</summary>
public static class TransportSelector
{
    /// <summary>Consumes one optional transport selector and preserves the remaining host arguments.</summary>
    /// <param name="arguments">Command-line tokens to validate and separate.</param>
    /// <returns>The selected transport and a defensive read-only snapshot of the host arguments.</returns>
    /// <exception cref="ArgumentNullException">The argument list is null.</exception>
    /// <exception cref="ArgumentException">A token is null or the transport selection is invalid.</exception>
    public static LaunchOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        const string invalidSelection = "Select 'stdio' or 'http' once using '--transport <value>'.";
        var transport = McpTransport.Stdio;
        var selectorSeen = false;
        var hostArguments = new List<string>(arguments.Count);

        var index = 0;
        while (index < arguments.Count)
        {
            var argument = arguments[index++];
            if (argument is null || argument.StartsWith("--transport=", StringComparison.Ordinal))
            {
                throw new ArgumentException(invalidSelection, nameof(arguments));
            }

            if (!string.Equals(argument, "--transport", StringComparison.Ordinal))
            {
                hostArguments.Add(argument);
                continue;
            }

            if (selectorSeen || index == arguments.Count)
            {
                throw new ArgumentException(invalidSelection, nameof(arguments));
            }

            selectorSeen = true;
            transport = arguments[index++] switch
            {
                "stdio" => McpTransport.Stdio,
                "http" => McpTransport.Http,
                _ => throw new ArgumentException(invalidSelection, nameof(arguments)),
            };
        }

        return new LaunchOptions(transport, Array.AsReadOnly(hostArguments.ToArray()));
    }
}
