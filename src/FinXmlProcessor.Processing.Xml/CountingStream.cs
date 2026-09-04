namespace FinXmlProcessor.Processing.Xml;

/// <summary>Read-only pass-through stream that counts bytes consumed so progress can be reported without touching the reader.</summary>
public sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private long _bytesRead;

    public CountingStream(Stream inner)
    {
        _inner = inner;
    }

    /// <summary>Safe to read from another thread; a torn read of a long is not possible on 64-bit runtimes.</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        Interlocked.Add(ref _bytesRead, read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        int read = _inner.Read(buffer);
        Interlocked.Add(ref _bytesRead, read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _bytesRead, read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
