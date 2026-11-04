using System.Net;
using System.Net.Http.Headers;
using static Bale.Core.Tests.PlaybackMediaTestSupport;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class HttpRangeStreamTests
{
    [Fact]
    public async Task SequentialReadMatchesTheFullSourceAndUsesOneAuthenticatedRangeRequest()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        Assert.Equal(Data.Length, stream.Length);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        Assert.Equal(Data, output.ToArray());
        Assert.Equal(Data.Length, stream.Position);
        Assert.Equal(0, await stream.ReadAsync(new byte[1].AsMemory()));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("bytes=0-", request.Headers.Range!.ToString());
        Assert.Equal("Bearer test-access", request.Headers.Authorization!.ToString());
        Assert.Equal("profile", Assert.Single(request.Headers.GetValues("X-Profile-Id")));
    }

    [Fact]
    public async Task FarForwardSeekReopensLazilyAtTheRequestedByte()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        stream.Position = 2 * 1024 * 1024;
        Assert.Single(handler.Requests);
        var bytes = new byte[4096];
        Assert.Equal(bytes.Length, await stream.ReadAsync(bytes.AsMemory()));
        Assert.Equal(Data[(2 * 1024 * 1024)..(2 * 1024 * 1024 + bytes.Length)], bytes);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("bytes=2097152-", handler.Requests[1].Headers.Range!.ToString());
        Assert.True(handler.Bodies[0].IsDisposed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(256 * 1024)]
    public async Task SmallForwardSeekSkipsWithoutOpeningANewRequest(int skip)
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var bytes = new byte[1024];
        await stream.ReadExactlyAsync(bytes);
        stream.Seek(skip, SeekOrigin.Current);
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(Data[(1024 + skip)..(2048 + skip)], bytes);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BackwardSeekAndSeekFromEndUseTheRightRangeAndSynchronousReads()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var bytes = new byte[100];
        stream.ReadExactly(bytes);
        Assert.Equal(10, stream.Seek(10, SeekOrigin.Begin));
        Assert.Equal(100, stream.Read(bytes, 0, bytes.Length));
        Assert.Equal(Data[10..110], bytes);
        Assert.Equal("bytes=10-", handler.Requests[1].Headers.Range!.ToString());
        Assert.Equal(Data.Length - 100, stream.Seek(-100, SeekOrigin.End));
        stream.ReadExactly(bytes);
        Assert.Equal(Data[^100..], bytes);
        Assert.Equal($"bytes={Data.Length - 100}-", handler.Requests[2].Headers.Range!.ToString());
    }

    [Fact]
    public async Task LengthComesFromContentRangeNotThePartialBodyLength()
    {
        var handler = new RangeHandler
        {
            ConfigureResponse = (response, _) =>
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, Data.Length - 1, Data.Length + 1000);
            },
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        Assert.Equal(Data.Length + 1000, stream.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MidBodyFailureReconnectsAtTheConfirmedPositionOnce(bool httpError)
    {
        var handler = new RangeHandler
        {
            FailAfterBytes = request => request == 1 ? 8192 : null,
            ReadError = () => httpError ? new HttpRequestException("Simulated body failure.") : new IOException("Simulated body failure."),
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, 8192);
        Assert.Equal(Data, output.ToArray());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("bytes=8192-", handler.Requests[1].Headers.Range!.ToString());
        Assert.True(handler.Bodies[0].IsDisposed);
    }

    [Fact]
    public async Task ASecondBodyFailureIsReturnedWithoutAReconnectLoop()
    {
        var handler = new RangeHandler { FailAfterBytes = request => request == 1 ? 8192 : 0 };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var bytes = new byte[8192];
        await stream.ReadExactlyAsync(bytes);
        await Assert.ThrowsAsync<IOException>(async () => await stream.ReadExactlyAsync(bytes));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(8192, stream.Position);
    }

    [Fact]
    public async Task ABodyFailureWhileSkippingReopensAtTheDesiredPosition()
    {
        var handler = new RangeHandler { FailAfterBytes = request => request == 1 ? 8192 : null };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        stream.Position = 32 * 1024;
        var bytes = new byte[100];
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(Data[(32 * 1024)..(32 * 1024 + 100)], bytes);
        Assert.Equal("bytes=32768-", handler.Requests[1].Headers.Range!.ToString());
    }

    [Fact]
    public async Task IgnoredRangesUseContentLengthAndReopenFromZeroToSeek()
    {
        var handler = new RangeHandler { IgnoreRanges = true };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        Assert.Equal(Data.Length, stream.Length);
        stream.Position = 2 * 1024 * 1024;
        var bytes = new byte[100];
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(Data[(2 * 1024 * 1024)..(2 * 1024 * 1024 + 100)], bytes);
        stream.Position = 10;
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(Data[10..110], bytes);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("bytes=0-", request.Headers.Range!.ToString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownTotalIsRejectedAndTheResponseIsDisposed(bool ignoredRange)
    {
        var handler = new RangeHandler
        {
            IgnoreRanges = ignoredRange,
            ConfigureResponse = (response, _) =>
            {
                if (ignoredRange) response.Content.Headers.ContentLength = null;
                else response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, Data.Length - 1);
            },
        };
        using var client = Create(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Playback.OpenMedia(Plan()).OpenStreamAsync(default));
        Assert.True(Assert.Single(handler.Bodies).IsDisposed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-start")]
    [InlineData("wrong-unit")]
    public async Task InvalidInitialContentRangeIsRejected(string kind)
    {
        var handler = new RangeHandler
        {
            ConfigureResponse = (response, _) => response.Content.Headers.ContentRange = kind switch
            {
                "wrong-start" => new ContentRangeHeaderValue(1, Data.Length - 1, Data.Length),
                "wrong-unit" => new ContentRangeHeaderValue(0, Data.Length - 1, Data.Length) { Unit = "items" },
                _ => null,
            },
        };
        using var client = Create(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Playback.OpenMedia(Plan()).OpenStreamAsync(default));
        Assert.True(Assert.Single(handler.Bodies).IsDisposed);
    }

    [Theory]
    [InlineData("ignored-range")]
    [InlineData("wrong-start")]
    [InlineData("changed-length")]
    public async Task InvalidReopenedResponsesThrowWithoutRetrying(string kind)
    {
        var handler = new RangeHandler
        {
            ConfigureResponse = (response, request) =>
            {
                if (request == 1) return;
                if (kind == "ignored-range") response.StatusCode = HttpStatusCode.OK;
                else if (kind == "wrong-start") response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, Data.Length - 1, Data.Length);
                else response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1024 * 1024, Data.Length - 1, Data.Length + 1);
            },
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        stream.Position = 1024 * 1024;
        var error = await Assert.ThrowsAsync<IOException>(async () => await stream.ReadExactlyAsync(new byte[1]));
        Assert.Equal("The media server returned an invalid range response.", error.Message);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Bodies, body => Assert.True(body.IsDisposed));
    }

    [Fact]
    public async Task SeekBeyondEndAndEmptyReadsDoNotFetchMoreBytes()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        stream.Position = Data.Length + 1L;
        Assert.Equal(0, await stream.ReadAsync(new byte[1].AsMemory()));
        stream.Position = 1024 * 1024;
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.Single(handler.Requests);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
    }

    [Fact]
    public async Task ReopensCannotCrossAProfileFence()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        client.UseProfile("other-profile");
        stream.Position = 1024 * 1024;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadExactlyAsync(new byte[1]));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DisposalClosesTheBodyAndRejectsFurtherReads()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        stream.Dispose();
        stream.Dispose();
        Assert.True(Assert.Single(handler.Bodies).IsDisposed);
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await stream.ReadExactlyAsync(new byte[1]));
        Assert.Throws<ObjectDisposedException>(() => stream.Position = 0);
    }

    [Fact]
    public async Task CanceledReadDoesNotReopenTheStream()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await stream.ReadExactlyAsync(new byte[1], cancellation.Token));
        Assert.Equal(0, stream.Position);
        Assert.Single(handler.Requests);
    }
}
