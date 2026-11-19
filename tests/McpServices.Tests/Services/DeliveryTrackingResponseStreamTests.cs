using System.Buffers;
using System.IO.Pipelines;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     The delivery stream decides whether a single-use download was really delivered. It must report a lost
///     connection from the flush result alone, without waiting for the request-aborted token.
/// </summary>
public class DeliveryTrackingResponseStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WritesToAnOpenPipe_AreDelivered_AndDoNotMarkTheConnectionLost()
    {
        var pipe = new Pipe();
        var stream = new DeliveryTrackingResponseStream(pipe.Writer);

        await stream.WriteAsync(new byte[] { 1, 2, 3 }, Ct);
        await stream.FlushAsync(Ct);

        stream.ConnectionLost.Should().BeFalse();
        var read = await pipe.Reader.ReadAsync(Ct);
        read.Buffer.ToArray().Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task AWriteAfterTheReaderCompleted_MarksTheConnectionLost()
    {
        var pipe = new Pipe();
        var stream = new DeliveryTrackingResponseStream(pipe.Writer);
        await pipe.Reader.CompleteAsync(); // the client went away

        await stream.WriteAsync(new byte[] { 1 }, Ct);

        stream.ConnectionLost.Should().BeTrue();
    }

    [Fact]
    public async Task ACanceledFlush_MarksTheConnectionLost()
    {
        var pipe = new Pipe();
        var stream = new DeliveryTrackingResponseStream(pipe.Writer);
        pipe.Writer.CancelPendingFlush();

        await stream.FlushAsync(Ct);

        stream.ConnectionLost.Should().BeTrue();
    }

    [Fact]
    public void SynchronousWrites_AreRefused()
    {
        var stream = new DeliveryTrackingResponseStream(new Pipe().Writer);

        var write = () => stream.Write([1], 0, 1);

        write.Should().Throw<NotSupportedException>();
        stream.CanRead.Should().BeFalse();
        stream.CanWrite.Should().BeTrue();
    }
}
