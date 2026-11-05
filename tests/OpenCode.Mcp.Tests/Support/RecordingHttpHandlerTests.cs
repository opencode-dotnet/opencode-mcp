using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Checks the observation-only recorder against owned socket exchanges and embedded wire data.</summary>
public sealed class RecordingHttpHandlerTests
{
    private static readonly string?[] ExpectedMethods = ["fixture/echo"];

    /// <summary>JSON requests and responses become owned frames with their actual identities.</summary>
    [Test]
    public async Task SendAsync_Should_Record_Request_And_Response_When_Content_Is_Json()
    {
        var fixture = await LoadFixtureAsync();
        await WithReceiverAsync(fixture.GetProperty("json").GetString()!, "application/json", (endpoint, _, token) =>
            WithClientAsync(4096, async (client, recorder) =>
        {
            using var response = await SendAsync(client, endpoint, fixture.GetProperty("request"), token);
            await Assert.That(recorder.Requests.Select(static frame => frame.GetProperty("method").GetString()))
                .IsEquivalentTo(ExpectedMethods, CollectionOrdering.Matching);
            await Assert.That(recorder.Responses.Select(static frame => frame.GetProperty("id").GetInt32()))
                .IsEquivalentTo([17], CollectionOrdering.Matching);
            await Assert.That(recorder.StatusCodes).IsEquivalentTo([HttpStatusCode.OK], CollectionOrdering.Matching);
        }));
    }

    /// <summary>Request-scoped SSE preserves event order, comments, escaped text and error frames.</summary>
    [Test]
    public async Task SendAsync_Should_Record_Frames_When_Content_Is_Request_Scoped_Sse()
    {
        var fixture = await LoadFixtureAsync();
        await WithReceiverAsync(fixture.GetProperty("sse").GetString()!, "text/event-stream", (endpoint, _, token) =>
            WithClientAsync(4096, async (client, recorder) =>
        {
            using var response = await SendAsync(client, endpoint, fixture.GetProperty("request"), token);
            await Assert.That(recorder.Responses.Select(static frame => frame.GetProperty("id").GetInt32()))
                .IsEquivalentTo([17, 3], CollectionOrdering.Matching);
            await Assert.That(recorder.Responses[0].GetProperty("result").GetProperty("text").GetString())
                .IsEqualTo(fixture.GetProperty("request").GetProperty("params").GetProperty("text").GetString());
            await Assert.That(recorder.Responses[1].GetProperty("error").GetProperty("code").GetInt32()).IsEqualTo(-32601);
            await Assert.That(recorder.Responses[1].GetProperty("error").GetProperty("message").GetString()).IsEqualTo("雪 not found");
        }));
    }

    /// <summary>Recording preserves both payload byte sequences and all content and exchange headers.</summary>
    [Test]
    [Arguments("json", "application/json")]
    [Arguments("sse", "text/event-stream")]
    public async Task SendAsync_Should_Preserve_Content_And_Headers_When_Content_Is_Recorded(string member, string mediaType)
    {
        var fixture = await LoadFixtureAsync();
        var body = fixture.GetProperty(member).GetString()!;
        await WithReceiverAsync(body, mediaType, (endpoint, received, token) =>
            WithClientAsync(4096, async (client, recorder) =>
        {
            using var response = await SendAsync(client, endpoint, fixture.GetProperty("request"), token);
            await Assert.That(await received.WaitAsync(token)).IsEquivalentTo(Encoding.UTF8.GetBytes(fixture.GetProperty("request").GetRawText()), CollectionOrdering.Matching);
            await Assert.That(await response.Content.ReadAsByteArrayAsync(token)).IsEquivalentTo(Encoding.UTF8.GetBytes(body), CollectionOrdering.Matching);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(mediaType);
            await Assert.That(response.Headers.GetValues("X-Content")).IsEquivalentTo(["response-content"], CollectionOrdering.Matching);
            await Assert.That(response.Content.Headers.ContentLanguage).IsEquivalentTo(["tr"], CollectionOrdering.Matching);
            await Assert.That(recorder.RequestHeaders.Single()["x-request"]).IsEquivalentTo(["first", "second"], CollectionOrdering.Matching);
            await Assert.That(recorder.RequestHeaders.Single()["x-content"]).IsEquivalentTo(["request-content"], CollectionOrdering.Matching);
            await Assert.That(recorder.ResponseHeaders.Single()["x-fixture"]).IsEquivalentTo(["response"], CollectionOrdering.Matching);
            await Assert.That(recorder.ResponseHeaders.Single()["content-type"]).IsEquivalentTo([$"{mediaType}; charset=utf-8"], CollectionOrdering.Matching);
        }));
    }

    /// <summary>Sequential observations retain insertion order rather than sorting by request identity.</summary>
    [Test]
    public async Task SendAsync_Should_Preserve_Observation_Order_When_Several_Requests_Are_Sent()
    {
        var fixture = await LoadFixtureAsync();
        await WithReceiverAsync(fixture.GetProperty("json").GetString()!, "application/json", (endpoint, _, token) =>
            WithClientAsync(4096, async (client, recorder) =>
        {
            using var first = await SendAsync(client, endpoint, fixture.GetProperty("request"), token);
            var changed = fixture.GetProperty("request").GetRawText().Replace("17", "3", StringComparison.Ordinal);
            using var document = JsonDocument.Parse(changed);
            using var second = await SendAsync(client, endpoint, document.RootElement, token);
            await Assert.That(recorder.Requests.Select(static frame => frame.GetProperty("id").GetInt32()))
                .IsEquivalentTo([17, 3], CollectionOrdering.Matching);
            await Assert.That(recorder.Responses.Select(static frame => frame.GetProperty("id").GetInt32()))
                .IsEquivalentTo([17, 17], CollectionOrdering.Matching);
        }));
    }

    /// <summary>Snapshots survive disposal of the caller's messages and content.</summary>
    [Test]
    public async Task SendAsync_Should_Keep_Owned_Frames_When_Original_Content_Is_Disposed()
    {
        var fixture = await LoadFixtureAsync();
        await WithReceiverAsync(fixture.GetProperty("json").GetString()!, "application/json", (endpoint, _, token) =>
            WithClientAsync(4096, async (client, recorder) =>
        {
            using (client)
            {
                using var response = await SendAsync(client, endpoint, fixture.GetProperty("request"), token);
            }

            await Assert.That(recorder.Requests.Single().GetProperty("id").GetInt32()).IsEqualTo(17);
            await Assert.That(recorder.Responses.Single().GetProperty("result").GetProperty("resultType").GetString()).IsEqualTo("complete");
            await Assert.That(recorder.RequestHeaders.Single()["x-request"]).IsEquivalentTo(["first", "second"], CollectionOrdering.Matching);
        }));
    }

    /// <summary>The supplied bound refuses either an oversized request or an oversized response.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SendAsync_Should_Refuse_Recording_When_Content_Exceeds_The_Supplied_Bound(bool oversizedRequest)
    {
        var fixture = await LoadFixtureAsync();
        await WithReceiverAsync(fixture.GetProperty("sse").GetString()!, "text/event-stream", (endpoint, _, token) =>
            WithClientAsync(oversizedRequest ? 1 : 200, async (client, _) =>
        {
            await Assert.That(async () =>
            {
                using var response = await SendAsync(client, endpoint, fixture.GetProperty("request"), token);
            }).ThrowsExactly<HttpRequestException>();
        }));
    }

    /// <summary>Malformed protocol content remains an observable parsing failure, not an omitted frame.</summary>
    [Test]
    [Arguments("malformedJson", "application/json")]
    [Arguments("malformedSse", "text/event-stream")]
    public async Task SendAsync_Should_Propagate_The_Error_When_Protocol_Content_Is_Malformed(string member, string mediaType)
    {
        var fixture = await LoadFixtureAsync();
        await WithReceiverAsync(fixture.GetProperty(member).GetString()!, mediaType, (endpoint, _, token) =>
            WithClientAsync(4096, async (client, _) =>
        {
            await Assert.That(async () =>
            {
                using var response = await SendAsync(client, endpoint, fixture.GetProperty("request"), token);
            }).Throws<JsonException>();
        }));
    }

    private static async Task WithClientAsync(int maximumBodyBytes, Func<HttpClient, RecordingHttpHandler, Task> action)
    {
        using var sockets = new SocketsHttpHandler { UseProxy = false };
        using var recorder = new RecordingHttpHandler(sockets, maximumBodyBytes);
        using var client = new HttpClient(recorder, disposeHandler: false);
        await action(client, recorder);
    }

    private static async Task<JsonElement> LoadFixtureAsync()
    {
        await using var stream = typeof(RecordingHttpHandlerTests).Assembly.GetManifestResourceStream("Fixtures.Protocol.http-complete.json")
            ?? throw new InvalidOperationException("The recorder fixture is missing.");
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Uri endpoint, JsonElement frame, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(frame.GetRawText(), Encoding.UTF8, "application/json");
        request.Headers.Add("X-Request", ["first", "second"]);
        request.Content.Headers.Add("X-Content", "request-content");
        request.Content.Headers.ContentLanguage.Add("tr");
        return await client.SendAsync(request, token);
    }

    private static async Task WithReceiverAsync(string body, string mediaType, Func<Uri, Task<byte[]>, CancellationToken, Task> action)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapPost("/", async context =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            received.TrySetResult(buffer.ToArray());
            context.Response.ContentType = $"{mediaType}; charset=utf-8";
            context.Response.Headers.Append("X-Fixture", "response");
            context.Response.Headers.Append("X-Content", "response-content");
            context.Response.Headers.ContentLanguage = "tr";
            await context.Response.WriteAsync(body, context.RequestAborted);
        });
        try
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await app.StartAsync(guard.Token);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var bound = await Assert.That(addresses).IsNotNull();
            await action(new Uri(bound.Addresses.Single()), received.Task, guard.Token);
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await app.StopAsync(cleanup.Token);
            }
            finally
            {
                await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }
}
