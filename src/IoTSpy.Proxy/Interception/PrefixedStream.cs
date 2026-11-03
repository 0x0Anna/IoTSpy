namespace IoTSpy.Proxy.Interception;

/// <summary>
/// Read-through stream that first returns bytes already consumed from <paramref name="inner"/>
/// (e.g. a ClientHello read to decide how to handle the connection), then continues reading
/// from <paramref name="inner"/>. Writes, flushes and everything else go straight to the inner stream.
/// Does not own <paramref name="inner"/>.
/// </summary>
internal sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
{
    private ReadOnlyMemory<byte> _prefix = prefix;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    // Zero-byte reads: SslStream issues ReadAsync(Memory<byte>.Empty) to wait until data is
    // available before renting a buffer. While prefix bytes remain, data IS available — answer
    // immediately. Forwarding it to the socket would block forever: the client already sent the
    // bytes we hold (its ClientHello) and is now waiting for us.
    public override int Read(Span<byte> buffer)
    {
        if (!_prefix.IsEmpty) return TakePrefix(buffer);
        return inner.Read(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (!_prefix.IsEmpty) return ValueTask.FromResult(TakePrefix(buffer.Span));
        return inner.ReadAsync(buffer, ct);
    }

    private int TakePrefix(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        var n = Math.Min(buffer.Length, _prefix.Length);
        _prefix.Span[..n].CopyTo(buffer);
        _prefix = _prefix[n..];
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
