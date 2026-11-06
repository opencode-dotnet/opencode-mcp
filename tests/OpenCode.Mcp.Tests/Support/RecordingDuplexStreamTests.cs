using System.Text;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Support;

/// <summary>Checks asynchronous transfer, exact observations and owned stream lifetime.</summary>
public sealed class RecordingDuplexStreamTests
{
    [Test]
    public async Task DisposeAsync_Should_Clear_Capabilities_Even_When_The_Inner_Stream_Retains_Them()
    {
        await using var recorder = new RecordingDuplexStream(Stream.Null, new WireTranscript(), fromServer: false);
        await Assert.That(recorder.CanRead).IsTrue();
        await Assert.That(recorder.CanWrite).IsTrue();
        await Assert.That(recorder.CanSeek).IsFalse();
        await recorder.DisposeAsync();
        await Assert.That(recorder.CanRead).IsFalse();
        await Assert.That(recorder.CanWrite).IsFalse();
        await Assert.That(recorder.CanSeek).IsFalse();
    }

    [Test]
    public async Task ReadAsync_Should_Record_Only_Returned_Bytes_When_A_Buffer_Is_Larger_Than_The_Frame()
    {
        var frame = await WireTranscriptTests.LoadAsync("tool-list-complete.json");
        using var inner = new MemoryStream(frame, writable: false);
        var transcript = new WireTranscript();
        await using var recorder = new RecordingDuplexStream(inner, transcript, fromServer: true);
        var buffer = new byte[frame.Length + 20];
        Array.Fill<byte>(buffer, 88);
        var count = await recorder.ReadAsync(buffer.AsMemory(3));
        await Assert.That(count).IsEqualTo(frame.Length);
        await Assert.That(buffer.AsSpan(3, count).ToArray()).IsEquivalentTo(frame, CollectionOrdering.Matching);
        await Assert.That(transcript.Results.Single().GetProperty("tools")[0].GetProperty("name").GetString()).IsEqualTo(ProtocolData.PingTool);
        await Assert.That(await recorder.ReadAsync(buffer.AsMemory())).IsEqualTo(0);
        await Assert.That(transcript.MalformedLines).IsEmpty();
    }

    [Test]
    public async Task WriteAsync_Should_Forward_Bytes_Exactly_Once_When_The_Client_Writes()
    {
        var bytes = WireTranscriptTests.Request(ProtocolData.ListMethod);
        using var inner = new MemoryStream();
        var transcript = new WireTranscript();
        await using (var recorder = new RecordingDuplexStream(inner, transcript, fromServer: false))
        {
            await recorder.WriteAsync(bytes.AsMemory());
            await recorder.FlushAsync();
            await Assert.That(transcript.Methods).IsEquivalentTo([ProtocolData.ListMethod], CollectionOrdering.Matching);
        }
        await Assert.That(inner.ToArray()).IsEquivalentTo(bytes, CollectionOrdering.Matching);
        await Assert.That(inner.CanWrite).IsFalse();
    }

    [Test]
    public async Task DisposeAsync_Should_Close_The_Owned_Stream_When_Disposal_Is_Repeated()
    {
        using var inner = new MemoryStream();
        await using var recorder = new RecordingDuplexStream(inner, new WireTranscript(), fromServer: false);
        await recorder.DisposeAsync();
        await recorder.DisposeAsync();
        await Assert.That(inner.CanWrite).IsFalse();
    }

    [Test]
    public async Task ReadAsync_Should_Complete_The_Transcript_When_The_Owned_Stream_Ends()
    {
        using var inner = new MemoryStream(Encoding.UTF8.GetBytes("trailing"));
        var transcript = new WireTranscript();
        await using var recorder = new RecordingDuplexStream(inner, transcript, fromServer: true);
        await recorder.CopyToAsync(Stream.Null);
        await Assert.That(transcript.MalformedLines).IsEquivalentTo(["trailing"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WriteAsync_Should_Not_Record_Bytes_When_The_Owned_Stream_Rejects_The_Write()
    {
        using var inner = new MemoryStream([], writable: false);
        var transcript = new WireTranscript();
        await using var recorder = new RecordingDuplexStream(inner, transcript, fromServer: false);
        await Assert.That(async () => await recorder.WriteAsync(WireTranscriptTests.Request(ProtocolData.ListMethod).AsMemory()))
            .ThrowsExactly<NotSupportedException>();
        await Assert.That(transcript.Methods).IsEmpty();
    }
}
