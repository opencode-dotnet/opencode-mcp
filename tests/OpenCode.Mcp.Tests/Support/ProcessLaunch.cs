using System.Collections.ObjectModel;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Carries one owned child launch with immutable argument and environment snapshots.</summary>
internal sealed record ProcessLaunch
{
    /// <summary>Gets the executable started directly by the process owner.</summary>
    public required string FileName
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            field = value;
        }
    }

    /// <summary>Gets the child arguments in their original order.</summary>
    public required IReadOnlyList<string> Arguments
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly(value.ToArray());
        }
    }

    /// <summary>Gets explicit child environment additions and removals.</summary>
    public required IReadOnlyDictionary<string, string?> Environment
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(value, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>Gets the isolated root transferred to the process owner for cleanup.</summary>
    public required string OwnedRoot
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            field = value;
        }
    }

    /// <summary>Gets the child working directory inside the owned root.</summary>
    public required string WorkingDirectory
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            field = value;
        }
    }
}
