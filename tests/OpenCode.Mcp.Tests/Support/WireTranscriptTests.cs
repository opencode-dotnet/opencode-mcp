using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Checks ordered observations without implementing a protocol peer.</summary>
public sealed class WireTranscriptTests
{
    [Test]
    public async Task Record_Should_Count_Raw_Server_Bytes_And_Keep_All_Server_Lines_In_Order()
    {
        var transcript = new WireTranscript();
        var error = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new JsonObject { ["code"] = -32600, ["message"] = "x" },
        }.ToJsonString();
        var notification = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/message" }.ToJsonString();
        var result = Encoding.UTF8.GetString(await LoadAsync("tool-call-complete.json")).TrimEnd('\n', '\r');
        var bytes = Encoding.UTF8.GetBytes(error + "\r\n\n" + notification + "\n" + result + "\nlast fragment");
        transcript.Record(Request(ProtocolData.ListMethod), fromServer: false);
        foreach (var value in bytes)
        {
            transcript.Record(new byte[] { value }, fromServer: true);
        }
        transcript.Complete(fromServer: true);
        await Assert.That(transcript.ServerByteCount).IsEqualTo(bytes.Length);
        await Assert.That(transcript.ServerLines).IsEquivalentTo([error, "", notification, result, "last fragment"], CollectionOrdering.Matching);
        await Assert.That(transcript.Results.Single().GetProperty("content")[0].GetProperty("text").GetString()).IsEqualTo(ProtocolData.UnicodeSample);
        await Assert.That(transcript.MalformedLines).IsEquivalentTo(["last fragment"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Record_Should_Preserve_Result_Order_When_Complete_Frames_Arrive()
    {
        var transcript = new WireTranscript();
        foreach (var name in new[] { "discover-complete.json", "tool-list-complete.json", "tool-call-complete.json" })
        {
            transcript.Record(await LoadAsync(name), fromServer: true);
        }

        await Assert.That(transcript.Results.Select(static result => result.GetProperty("resultType").GetString() ?? ""))
            .IsEquivalentTo([ProtocolData.CompleteResult, ProtocolData.CompleteResult, ProtocolData.CompleteResult], CollectionOrdering.Matching);
        await Assert.That(transcript.Results.Select(static result => result.EnumerateObject().ElementAt(1).Name))
            .IsEquivalentTo(["supportedVersions", "tools", "content"], CollectionOrdering.Matching);
        await Assert.That(transcript.Results[0].GetProperty("supportedVersions")[0].GetString()).IsEqualTo(ProtocolData.Revision);
        await Assert.That(transcript.Results[1].GetProperty("tools")[0].GetProperty("name").GetString()).IsEqualTo(ProtocolData.PingTool);
        await Assert.That(transcript.Results[2].GetProperty("content")[0].GetProperty("text").GetString())
            .IsEqualTo(ProtocolData.UnicodeSample);
        await Assert.That(transcript.MalformedLines).IsEmpty();
    }

    [Test]
    public async Task Record_Should_Preserve_Method_Order_When_Client_Requests_Arrive()
    {
        var transcript = new WireTranscript();
        foreach (var method in new[] { ProtocolData.DiscoverMethod, ProtocolData.ListMethod, ProtocolData.CallMethod })
        {
            transcript.Record(Request(method), fromServer: false);
        }
        await Assert.That(transcript.Methods).IsEquivalentTo([ProtocolData.DiscoverMethod, ProtocolData.ListMethod, ProtocolData.CallMethod], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Record_Should_Preserve_Frames_When_Reads_Split_Json_And_Utf8()
    {
        var transcript = new WireTranscript();
        var bytes = await LoadAsync("tool-call-complete.json");
        foreach (var value in bytes)
        {
            transcript.Record(new byte[] { value }, fromServer: true);
        }
        await Assert.That(transcript.Results.Single().GetProperty("content")[0].GetProperty("text").GetString())
            .IsEqualTo(ProtocolData.UnicodeSample);
        await Assert.That(transcript.MalformedLines).IsEmpty();
    }

    [Test]
    public async Task Record_Should_Keep_Directions_Separate_When_Input_Is_Interleaved()
    {
        var transcript = new WireTranscript();
        var response = await LoadAsync("tool-list-complete.json");
        var request = Request(ProtocolData.ListMethod);
        transcript.Record(response.AsMemory(0, 12), fromServer: true);
        transcript.Record(request.AsMemory(0, 7), fromServer: false);
        transcript.Record(response.AsMemory(12), fromServer: true);
        transcript.Record(request.AsMemory(7), fromServer: false);
        await Assert.That(transcript.Methods).IsEquivalentTo([ProtocolData.ListMethod], CollectionOrdering.Matching);
        await Assert.That(transcript.Results.Single().GetProperty("tools")[0].GetProperty("name").GetString()).IsEqualTo(ProtocolData.PingTool);
        await Assert.That(transcript.MalformedLines).IsEmpty();
    }

    [Test]
    public async Task Record_Should_Report_Every_Malformed_Line_When_Stdout_Is_Not_Protocol()
    {
        var transcript = new WireTranscript();
        var bytes = await LoadAsync("malformed-stdout.txt");
        transcript.Record(bytes, fromServer: true);
        await Assert.That(transcript.MalformedLines).IsEquivalentTo(
            Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => line.TrimEnd('\r')),
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task Record_Should_Keep_Owned_Results_When_Parse_Documents_Are_Disposed()
    {
        var transcript = new WireTranscript();
        transcript.Record(await LoadAsync("tool-list-complete.json"), fromServer: true);
        var snapshot = transcript.Results;
        transcript.Record(await LoadAsync("discover-complete.json"), fromServer: true);
        await Assert.That(snapshot.Count).IsEqualTo(1);
        await Assert.That(snapshot[0].GetProperty("tools")[0].GetProperty("name").GetString()).IsEqualTo(ProtocolData.PingTool);
    }

    [Test]
    public async Task Record_Should_Observe_The_Final_Fragment_When_Stdout_Reaches_Eof()
    {
        var transcript = new WireTranscript();
        transcript.Record(Encoding.UTF8.GetBytes("last fragment"), fromServer: true);
        transcript.Record(Request(ProtocolData.ListMethod).AsMemory(0, 7), fromServer: false);
        transcript.Complete(fromServer: true);
        await Assert.That(transcript.MalformedLines).IsEquivalentTo(["last fragment"], CollectionOrdering.Matching);
        transcript.Complete(fromServer: false);
        transcript.Complete(fromServer: true);
        await Assert.That(transcript.MalformedLines).IsEquivalentTo(["last fragment", "{\"jsonr"], CollectionOrdering.Matching);
    }

    internal static byte[] Request(string method) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 17,
        ["method"] = method,
    }.ToJsonString() + "\r\n");

    internal static async Task<byte[]> LoadAsync(string name)
    {
        await using var stream = typeof(WireTranscriptTests).Assembly.GetManifestResourceStream("Fixtures.Protocol." + name)
            ?? throw new InvalidOperationException("The protocol fixture is missing: " + name);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
