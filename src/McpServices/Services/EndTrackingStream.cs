namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Read-only stream wrapper that records whether the last byte of the underlying file was handed out
///     (AB#6989). The single-use download deletes its entry only when this is true <i>and</i> the response
///     finished without the client aborting, so a partial read — an aborted transfer or a range that stops
///     before the end of the file — leaves the entry in place for a resume until the TTL sweeper removes it.
/// </summary>
internal sealed class EndTrackingStream : Stream
{
    private readonly Stream _inner;

    public EndTrackingStream(Stream inner)
    {
        _inner = inner;
    }

    /// <summary>True once a read returned the last byte (or hit end of file).</summary>
    public bool ReachedEnd { get; private set; }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Track(_inner.Read(buffer, offset, count), count);
    }

    public override int Read(Span<byte> buffer)
    {
        return Track(_inner.Read(buffer), buffer.Length);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return Track(await _inner.ReadAsync(buffer, cancellationToken), buffer.Length);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        return Track(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken), count);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        // A seek back (range request, resume) re-opens the question whether the end was served.
        var position = _inner.Seek(offset, origin);
        ReachedEnd = false;
        return position;
    }

    public override void Flush()
    {
    }

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

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        await base.DisposeAsync();
    }

    private int Track(int read, int requested)
    {
        if (read == 0 && requested > 0)
        {
            ReachedEnd = true;
        }
        else if (_inner.CanSeek && _inner.Position >= _inner.Length)
        {
            ReachedEnd = true;
        }

        return read;
    }
}
