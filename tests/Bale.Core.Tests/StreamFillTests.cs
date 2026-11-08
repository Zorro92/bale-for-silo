using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class StreamFillTests
{
    private static readonly byte[] Data = [1, 2, 3, 4, 5, 6, 7, 8, 9];

    [Theory]
    [InlineData(0u, false, 0)]
    [InlineData(0u, true, 0)]
    [InlineData(2u, false, 2)]
    [InlineData(2u, true, 2)]
    [InlineData(4u, false, 4)]
    [InlineData(4u, true, 4)]
    [InlineData(8u, false, 4)]
    [InlineData(8u, true, 4)]
    [InlineData(uint.MaxValue, false, 4)]
    [InlineData(uint.MaxValue, true, 4)]
    public async Task CountIsClampedToTheDestinationAndNeverReadsPastIt(uint count, bool partial, int expected)
    {
        using var stream = new MemoryStream(Data, writable: false);
        byte[] buffer = [99, 99, 99, 99, 99, 99, 99, 99];
        var read = await StreamFill.ReadAsync(stream, buffer.AsMemory(2, 4), count, partial);
        Assert.Equal(expected, read);
        Assert.Equal(expected, stream.Position);
        Assert.Equal(Data[..expected], buffer[2..(2 + expected)]);
        Assert.All(buffer[..2], value => Assert.Equal(99, value));
        Assert.All(buffer[(2 + expected)..], value => Assert.Equal(99, value));
    }

    [Theory]
    [InlineData(false, 6, 3)]
    [InlineData(true, 2, 1)]
    public async Task FullReadsContinueAcrossShortReadsButPartialReadsStopAfterOne(bool partial, int expected, int calls)
    {
        using var stream = new ShortReadStream(Data, 2);
        var buffer = new byte[8];
        var read = await StreamFill.ReadAsync(stream, buffer, 6, partial);
        Assert.Equal(expected, read);
        Assert.Equal(calls, stream.Reads);
        Assert.Equal(expected, stream.Position);
        Assert.Equal(Data[..expected], buffer[..read]);
        Assert.All(buffer[read..], value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndOfStreamReturnsZeroWithoutRetrying(bool partial)
    {
        using var stream = new ShortReadStream([], 2);
        var buffer = new byte[8];
        Assert.Equal(0, await StreamFill.ReadAsync(stream, buffer, 8, partial));
        Assert.Equal(1, stream.Reads);
        Assert.All(buffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task FullReadReturnsAccumulatedBytesAtEof()
    {
        using var stream = new ShortReadStream(Data[..5], 2);
        var buffer = new byte[8];
        Assert.Equal(5, await StreamFill.ReadAsync(stream, buffer, 8, partial: false));
        Assert.Equal(4, stream.Reads);
        Assert.Equal(Data[..5], buffer[..5]);
        Assert.All(buffer[5..], value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ZeroCountDoesNotReadTheStream()
    {
        using var stream = new ShortReadStream(Data, 2);
        Assert.Equal(0, await StreamFill.ReadAsync(stream, new byte[8], 0, partial: false));
        Assert.Equal(0, stream.Reads);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task EmptyDestinationDoesNotReadTheStream()
    {
        using var stream = new ShortReadStream(Data, 2);
        Assert.Equal(0, await StreamFill.ReadAsync(stream, Memory<byte>.Empty, uint.MaxValue, partial: false));
        Assert.Equal(0, stream.Reads);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task EachReadUsesTheStreamsCurrentPositionIncludingExternalSeeks()
    {
        using var stream = new MemoryStream(Data, writable: false);
        var buffer = new byte[2];
        Assert.Equal(2, await StreamFill.ReadAsync(stream, buffer, 2, partial: false));
        Assert.Equal(Data[..2], buffer);
        stream.Position = 5;
        Assert.Equal(2, await StreamFill.ReadAsync(stream, buffer, 2, partial: false));
        Assert.Equal(Data[5..7], buffer);
        Assert.Equal(7, stream.Position);
        stream.Position = 0;
        Assert.Equal(2, await StreamFill.ReadAsync(stream, buffer, 2, partial: true));
        Assert.Equal(Data[..2], buffer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationReachesTheUnderlyingRead(bool partial)
    {
        using var stream = new ShortReadStream(Data, 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await StreamFill.ReadAsync(stream, new byte[8], 8, partial, cancellation.Token));
        Assert.Equal(cancellation.Token, stream.LastToken);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task ReadFailurePropagatesWithoutClosingTheStream()
    {
        using var stream = new FailingReadStream();
        await Assert.ThrowsAsync<IOException>(async () =>
            await StreamFill.ReadAsync(stream, new byte[8], 8, partial: false));
        Assert.True(stream.CanRead);
    }

    private sealed class ShortReadStream(byte[] data, int maximumRead) : MemoryStream(data, writable: false)
    {
        public int Reads { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            LastToken = cancellationToken;
            return base.ReadAsync(buffer[..Math.Min(maximumRead, buffer.Length)], cancellationToken);
        }
    }

    private sealed class FailingReadStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Injected read failure."));
    }
}
