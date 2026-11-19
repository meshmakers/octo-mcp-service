using System.IO.Pipelines;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Response body stream that writes straight to the response <see cref="PipeWriter" /> and records whether the
///     connection went away while the body was written.
///     <para>
///     Why: after a client aborts, Kestrel discards further response writes without an error, so a file copy can still
///     read the file to its end; and it cancels <c>HttpContext.RequestAborted</c> on the thread pool, which can lose
///     against the end of the request. The single-use download then deleted a half-served file under load. A flush
///     result that reports the connection completed (or canceled) is a synchronous signal and marks the delivery as
///     incomplete.
///     </para>
/// </summary>
internal sealed class DeliveryTrackingResponseStream : Stream
{
    private readonly PipeWriter _writer;

    public DeliveryTrackingResponseStream(PipeWriter writer)
    {
        _writer = writer;
    }

    /// <summary>True once a write or flush reported that the client side is gone.</summary>
    public bool ConnectionLost { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Track(await _writer.WriteAsync(buffer, cancellationToken));
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        Track(await _writer.FlushAsync(cancellationToken));
    }

    public override void Flush() => throw new NotSupportedException("Synchronous I/O is not supported.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Synchronous I/O is not supported.");

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void Track(FlushResult result)
    {
        if (result.IsCompleted || result.IsCanceled)
        {
            ConnectionLost = true;
        }
    }
}
