using System.Text;
using System.Text.Json;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Records newline-delimited protocol observations while preserving fragmented input.</summary>
internal sealed class WireTranscript
{
    private readonly List<JsonElement> _results = [];
    private readonly List<string> _methods = [];
    private readonly List<string> _malformed = [];
    private readonly List<string> _serverLines = [];
    private readonly List<byte> _serverPending = [];
    private readonly List<byte> _clientPending = [];
    private readonly Lock _gate = new();
    private long _serverByteCount;

    /// <summary>Gets the number of raw bytes observed from the server.</summary>
    public long ServerByteCount
    {
        get
        {
            lock (_gate)
            {
                return _serverByteCount;
            }
        }
    }

    /// <summary>Gets every server line in wire order, including empty lines and non-result frames.</summary>
    public IReadOnlyList<string> ServerLines
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_serverLines.ToArray());
            }
        }
    }

    /// <summary>Gets observed client method names in wire order.</summary>
    public IReadOnlyList<string> Methods
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_methods.ToArray());
            }
        }
    }

    /// <summary>Gets owned response-result snapshots in wire order.</summary>
    public IReadOnlyList<JsonElement> Results
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_results.ToArray());
            }
        }
    }

    /// <summary>Gets observed nonempty lines that are not JSON-RPC messages.</summary>
    public IReadOnlyList<string> MalformedLines
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_malformed.ToArray());
            }
        }
    }

    /// <summary>Adds transferred bytes to the corresponding direction's pending observations.</summary>
    public void Record(ReadOnlyMemory<byte> bytes, bool fromServer)
    {
        lock (_gate)
        {
            if (fromServer)
            {
                _serverByteCount += bytes.Length;
            }
            var pending = fromServer ? _serverPending : _clientPending;
            foreach (var value in bytes.Span)
            {
                if (value == '\n')
                {
                    ObserveLine(Encoding.UTF8.GetString([.. pending]).TrimEnd('\r'), fromServer);
                    pending.Clear();
                }
                else
                {
                    pending.Add(value);
                }
            }
        }
    }

    /// <summary>Records an unterminated trailing fragment as malformed when a direction ends.</summary>
    public void Complete(bool fromServer)
    {
        lock (_gate)
        {
            var pending = fromServer ? _serverPending : _clientPending;
            if (pending.Count > 0)
            {
                var fragment = Encoding.UTF8.GetString([.. pending]);
                _malformed.Add(fragment);
                if (fromServer)
                {
                    _serverLines.Add(fragment);
                }
                pending.Clear();
            }
        }
    }

    private void ObserveLine(string line, bool fromServer)
    {
        if (fromServer)
        {
            _serverLines.Add(line);
        }
        if (line.Length == 0)
        {
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(line);
            var frame = document.RootElement;
            if (!IsEnvelope(frame))
            {
                _malformed.Add(line);
            }
            else if (fromServer && frame.TryGetProperty("result", out var result))
            {
                _results.Add(result.Clone());
            }
            else if (!fromServer && frame.TryGetProperty("method", out var method))
            {
                _methods.Add(method.GetString() ?? throw new InvalidOperationException("A method must be a string."));
            }
        }
        catch (JsonException)
        {
            _malformed.Add(line);
        }
    }

    private static bool IsEnvelope(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("jsonrpc", out var revision)
            || revision.ValueKind != JsonValueKind.String || revision.GetString() != "2.0")
        {
            return false;
        }
        var hasId = frame.TryGetProperty("id", out var id);
        if (hasId && id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.Null))
        {
            return false;
        }
        var hasResult = frame.TryGetProperty("result", out _);
        var hasError = frame.TryGetProperty("error", out var error);
        if (frame.TryGetProperty("method", out var method))
        {
            return method.ValueKind == JsonValueKind.String && !hasResult && !hasError;
        }
        return hasId && hasResult != hasError && (!hasError || error.ValueKind == JsonValueKind.Object);
    }
}
