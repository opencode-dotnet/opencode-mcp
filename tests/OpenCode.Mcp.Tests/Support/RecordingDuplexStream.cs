namespace OpenCode.Mcp.Tests.Support;

/// <summary>Observes transferred protocol bytes without introducing another stream reader.</summary>
/// <remarks>Ownership of the underlying stream transfers to this adapter. Only asynchronous I/O is supported.</remarks>
internal sealed class RecordingDuplexStream : Stream
{
    private readonly Stream _inner;
    private readonly WireTranscript _transcript;
    private readonly bool _fromServer;
    private bool _disposed;

    /// <summary>Receives an owned stream and the transcript direction it observes.</summary>
    public RecordingDuplexStream(Stream inner, WireTranscript transcript, bool fromServer)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(transcript);
        _inner = inner;
        _transcript = transcript;
        _fromServer = fromServer;
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    /// <inheritdoc />
    public override void Flush() => throw new NotSupportedException("This recording stream supports only asynchronous I/O.");
    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("This recording stream supports only asynchronous I/O.");
    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => throw new NotSupportedException("This recording stream supports only asynchronous I/O.");
    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await _inner.ReadAsync(buffer, cancellationToken);
        ObserveRead(buffer.Span[..count]);
        return count;
    }
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("This recording stream supports only asynchronous I/O.");
    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException("This recording stream supports only asynchronous I/O.");
    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken);
        _transcript.Record(buffer, _fromServer);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _inner.DisposeAsync();
        }
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private void ObserveRead(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            _transcript.Complete(_fromServer);
        }
        else
        {
            _transcript.Record(bytes.ToArray(), _fromServer);
        }
    }
}
