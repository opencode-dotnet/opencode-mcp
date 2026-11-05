using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text.Json;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Observes bounded request-scoped protocol frames without changing wire content.</summary>
internal sealed class RecordingHttpHandler : DelegatingHandler
{
    private readonly long _maximumBodyBytes;
    private readonly List<JsonElement> _requests = [];
    private readonly List<JsonElement> _responses = [];
    private readonly List<IReadOnlyDictionary<string, IReadOnlyList<string>>> _requestHeaders = [];
    private readonly List<IReadOnlyDictionary<string, IReadOnlyList<string>>> _responseHeaders = [];
    private readonly List<HttpStatusCode> _statusCodes = [];

    /// <summary>Receives the real transport and the per-body recording bound.</summary>
    public RecordingHttpHandler(HttpMessageHandler innerHandler, long maximumBodyBytes) : base(innerHandler)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBodyBytes);
        _maximumBodyBytes = maximumBodyBytes;
    }

    /// <summary>Gets owned request-frame snapshots in observation order.</summary>
    public IReadOnlyList<JsonElement> Requests => _requests.AsReadOnly();

    /// <summary>Gets owned response-frame snapshots in observation order.</summary>
    public IReadOnlyList<JsonElement> Responses => _responses.AsReadOnly();

    /// <summary>Gets owned, case-insensitive header snapshots for observed requests.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, IReadOnlyList<string>>> RequestHeaders => _requestHeaders.AsReadOnly();

    /// <summary>Gets owned, case-insensitive header snapshots for observed responses.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, IReadOnlyList<string>>> ResponseHeaders => _responseHeaders.AsReadOnly();

    /// <summary>Gets observed response statuses in completion order.</summary>
    public IReadOnlyList<HttpStatusCode> StatusCodes => _statusCodes.AsReadOnly();

    /// <summary>Records bounded wire content while preserving it for the caller.</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requestHeaders.Add(SnapshotHeaders(request.Headers, request.Content));
        if (request.Content is not null)
        {
            request.Content = await RecordContentAsync(request.Content, _requests, cancellationToken);
        }

        var response = await base.SendAsync(request, cancellationToken);
        try
        {
            _statusCodes.Add(response.StatusCode);
            _responseHeaders.Add(SnapshotHeaders(response.Headers, response.Content));
            response.Content = await RecordContentAsync(response.Content, _responses, cancellationToken);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<ByteArrayContent> RecordContentAsync(HttpContent content, List<JsonElement> frames, CancellationToken token)
    {
        using (content)
        {
            await content.LoadIntoBufferAsync(_maximumBodyBytes, token);
            var bytes = await content.ReadAsByteArrayAsync(token);
            if (bytes.Length > 0 && content.Headers.ContentType?.MediaType == "application/json")
            {
                frames.Add(CloneFrame(bytes));
            }
            else if (content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                using var stream = new MemoryStream(bytes, writable: false);
                await foreach (var item in SseParser.Create(stream).EnumerateAsync(token))
                {
                    using var document = JsonDocument.Parse(item.Data);
                    frames.Add(document.RootElement.Clone());
                }
            }

            var replacement = new ByteArrayContent(bytes);
            foreach (var header in content.Headers)
            {
                replacement.Headers.Add(header.Key, header.Value);
            }

            return replacement;
        }
    }

    private static JsonElement CloneFrame(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static ReadOnlyDictionary<string, IReadOnlyList<string>> SnapshotHeaders(HttpHeaders headers, HttpContent? content) =>
        new(headers.Concat(content is null ? [] : content.Headers).ToDictionary(static header => header.Key,
            static header => (IReadOnlyList<string>)Array.AsReadOnly(header.Value.ToArray()), StringComparer.OrdinalIgnoreCase));
}
