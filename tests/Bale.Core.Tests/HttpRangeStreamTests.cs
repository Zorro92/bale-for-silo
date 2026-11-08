using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackMediaTestSupport;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class HttpRangeStreamTests
{
    private const int BlockSize = 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] LargeData = CreateLargeData();

    private static byte[] CreateLargeData()
    {
        var data = new byte[48 * BlockSize + 317];
        new Random(314159).NextBytes(data);
        return data;
    }

    private static bool IsBlockRequest(HttpRequestMessage request) => request.Headers.Range?.Ranges.Single().To is not null;

    private static int BlockRequests(RangeHandler handler, long index) => handler.Requests.Count(request =>
        IsBlockRequest(request) && request.Headers.Range!.Ranges.Single().From == index * BlockSize);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ReadFullBlocksAsync(HttpRangeStream stream)
    {
        var bytes = new byte[BlockSize];
        for (var block = 0; block < LargeData.Length / BlockSize; block++)
        {
            await stream.ReadExactlyAsync(bytes);
            Assert.True(LargeData.AsSpan(block * BlockSize, bytes.Length).SequenceEqual(bytes), $"Block {block} differs from the source.");
        }
    }

    private sealed class CopyMemory(byte[] bytes, Action beforeCopy) : MemoryManager<byte>
    {
        public override Memory<byte> Memory => CreateMemory(bytes.Length);
        public override Span<byte> GetSpan()
        {
            beforeCopy();
            return bytes;
        }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    [Fact]
    public async Task SequentialReadMatchesTheFullSourceAndUsesAuthenticatedAlignedBlocks()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        Assert.Equal(Data.Length, stream.Length);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.True(Assert.Single(handler.Bodies).IsDisposed);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        Assert.Equal(Data, output.ToArray());
        Assert.Equal(Data.Length, stream.Position);
        Assert.Equal(0, await stream.ReadAsync(new byte[1].AsMemory()));
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("bytes=0-", handler.Requests[0].Headers.Range!.ToString());
        Assert.Equal(new[] { "bytes=0-1048575", "bytes=1048576-2097151", "bytes=2097152-3145727" },
            handler.Requests.Skip(1).OrderBy(request => request.Headers.Range!.Ranges.Single().From)
                .Select(request => request.Headers.Range!.ToString()));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer test-access", request.Headers.Authorization!.ToString());
            Assert.Equal("profile", Assert.Single(request.Headers.GetValues("X-Profile-Id")));
        });
        Assert.Equal(3, stream.BlockFetches);
        Assert.Equal(Data.Length, stream.BytesRead);
        await Task.WhenAll(handler.Bodies.Select(body => body.Disposal)).WaitAsync(Timeout);
        Assert.All(handler.Bodies, body => Assert.True(body.IsDisposed));
    }

    [Fact]
    public async Task NetworkCountersExcludeCacheReadsAndTheUnconsumedProbeBody()
    {
        var handler = new RangeHandler { Payload = Data[..32768] };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        Assert.Equal(0, stream.DownloadedBytes);
        Assert.Equal(TimeSpan.Zero, stream.DownloadTime);
        await stream.ReadExactlyAsync(new byte[100]);
        Assert.Equal(handler.Payload.Length, stream.DownloadedBytes);
        Assert.True(stream.DownloadTime > TimeSpan.Zero);
        var downloadTime = stream.DownloadTime;
        stream.Position = 0;
        await stream.ReadExactlyAsync(new byte[100]);
        Assert.Equal(handler.Payload.Length, stream.DownloadedBytes);
        Assert.Equal(downloadTime, stream.DownloadTime);
        Assert.Equal(200, stream.BytesRead);
    }

    [Fact]
    public async Task NetworkCountersIncludePartialDownloadsRetriedAfterBodyFailure()
    {
        var handler = new RangeHandler
        {
            Payload = Data[..32768],
            FailAfterBytes = (_, request) => request == 2 ? 8192 : null,
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        await stream.ReadExactlyAsync(new byte[100]);
        Assert.Equal(handler.Payload.Length + 8192, stream.DownloadedBytes);
        Assert.True(stream.DownloadTime > TimeSpan.Zero);
        Assert.Equal(100, stream.BytesRead);
        Assert.Equal(2, stream.BlockFetches);
    }

    [Fact]
    public async Task NetworkCountersIncludeBytesDiscardedBySequentialSeeks()
    {
        var handler = new RangeHandler { IgnoreRanges = true };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        await stream.ReadExactlyAsync(new byte[100]);
        stream.Position = 1000;
        await stream.ReadExactlyAsync(new byte[100]);
        Assert.Equal(1100, stream.DownloadedBytes);
        Assert.True(stream.DownloadTime > TimeSpan.Zero);
        Assert.Equal(200, stream.BytesRead);
        Assert.False(stream.SupportsRanges);
        Assert.Equal(0, stream.DemandWaits);
        Assert.Equal(TimeSpan.Zero, stream.DemandWaitTime);
        Assert.Equal(TimeSpan.Zero, stream.MaxDemandWaitTime);
    }

    [Fact]
    public async Task AlternatingPositionsTenMiBApartDoNotRefetchCachedBlocks()
    {
        var handler = new RangeHandler { Payload = LargeData };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var bytes = new byte[4096];
        for (var read = 0; read < 40; read++)
        {
            var position = read % 2 == 0 ? 37 : 10 * BlockSize + 73;
            stream.Position = position;
            await stream.ReadExactlyAsync(bytes);
            Assert.Equal(LargeData[position..(position + bytes.Length)], bytes);
        }
        await handler.WaitForRequestsAsync(7);
        Assert.Equal(7, handler.Requests.Count); // One probe, two demanded blocks, four speculative blocks.
        Assert.Equal(1, BlockRequests(handler, 0));
        Assert.Equal(1, BlockRequests(handler, 10));
        Assert.Equal(40, stream.Reads);
        Assert.Equal(38, stream.CacheHits);
        Assert.Equal(4096, stream.SmallestRead);
        Assert.Equal(40 * bytes.Length, stream.BytesRead);
    }

    [Fact]
    public async Task SequentialReadsPrefetchTheNextTwoBlocksWithAtMostThreeFetchesInFlight()
    {
        var release = Signal();
        var started = Signal();
        var active = 0;
        var maximum = 0;
        var counterLock = new object();
        var handler = new RangeHandler
        {
            BeforeReadAsync = async (request, ct) =>
            {
                if (!IsBlockRequest(request)) return;
                var current = Interlocked.Increment(ref active);
                lock (counterLock) maximum = Math.Max(maximum, current);
                if (current == 3) started.TrySetResult();
                try { await release.Task.WaitAsync(ct); }
                finally { Interlocked.Decrement(ref active); }
            },
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var bytes = new byte[100];
        var read = stream.ReadAsync(bytes.AsMemory()).AsTask();
        await started.Task.WaitAsync(Timeout);
        Assert.Equal(3, Volatile.Read(ref active));
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(new long?[] { 0, BlockSize, 2 * BlockSize }, handler.Requests.Skip(1)
            .Select(request => request.Headers.Range!.Ranges.Single().From).Order());
        release.SetResult();
        Assert.Equal(bytes.Length, await read);
        Assert.Equal(Data[..bytes.Length], bytes);
        await Task.WhenAll(handler.Bodies.Select(body => body.Disposal)).WaitAsync(Timeout);
        stream.Position = BlockSize;
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(Data[BlockSize..(BlockSize + bytes.Length)], bytes);
        stream.Position = 2 * BlockSize;
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(Data[(2 * BlockSize)..(2 * BlockSize + bytes.Length)], bytes);
        Assert.Equal(2, stream.PrefetchHits);
        Assert.Equal(Data.Length, stream.DownloadedBytes); // Demand and both concurrent prefetch bodies.
        Assert.True(stream.DownloadTime > TimeSpan.Zero);
        Assert.Equal(3, maximum);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    public async Task ConfiguredPrefetchDepthKeepsAlignedRequestsAndTheThreeFetchLimit(int depth)
    {
        var release = Signal();
        var started = Signal();
        var active = 0;
        var maximum = 0;
        var counterLock = new object();
        var handler = new RangeHandler
        {
            Payload = LargeData,
            BeforeReadAsync = async (request, ct) =>
            {
                if (!IsBlockRequest(request)) return;
                var current = Interlocked.Increment(ref active);
                lock (counterLock) maximum = Math.Max(maximum, current);
                if (current == 3) started.TrySetResult();
                try { await release.Task.WaitAsync(ct); }
                finally { Interlocked.Decrement(ref active); }
            },
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default, depth);
        Assert.Equal(depth, stream.PrefetchBlocks);
        var read = stream.ReadAsync(new byte[1].AsMemory()).AsTask();
        await started.Task.WaitAsync(Timeout);
        Assert.Equal(4, handler.Requests.Count); // Probe plus three active fetches; deeper speculation is queued.
        release.SetResult();
        Assert.Equal(1, await read.WaitAsync(Timeout));
        await handler.WaitForRequestsAsync(depth + 2);
        Assert.Equal(3, maximum);
        Assert.Equal(Enumerable.Range(0, depth + 1).Select(index => (long?)index * BlockSize),
            handler.Requests.Skip(1).Select(request => request.Headers.Range!.Ranges.Single().From).Order());
    }

    [Fact]
    public async Task PrefetchOverridesArePerStreamAndTheDefaultRemainsTwo()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        var media = client.Playback.OpenMedia(Plan());
        await using var tuned = await media.OpenStreamAsync(default, 8);
        await using var baseline = await media.OpenStreamAsync(default);
        Assert.Equal(8, tuned.PrefetchBlocks);
        Assert.Equal(2, HttpRangeStream.DefaultPrefetchBlocks);
        Assert.Equal(2, baseline.PrefetchBlocks);
        Assert.True(baseline.SupportsRanges);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(int.MaxValue)]
    public async Task InvalidPrefetchDepthIsRejectedBeforeMakingAnyRequest(int depth)
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Playback.OpenMedia(Plan()).OpenStreamAsync(default, depth));
        Assert.Equal("prefetchBlocks", error.ParamName);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DemandWaitsMeasureUnfinishedFetchesButNotCacheHitsEmptyReadsOrEof()
    {
        var release = Signal();
        var started = Signal();
        var handler = new RangeHandler
        {
            Payload = Data[..32768],
            BeforeReadAsync = async (_, ct) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
            },
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        Assert.Equal(0, stream.DemandWaits);
        Assert.Equal(TimeSpan.Zero, stream.DemandWaitTime);
        Assert.Equal(TimeSpan.Zero, stream.MaxDemandWaitTime);
        var read = stream.ReadAsync(new byte[100].AsMemory()).AsTask();
        await started.Task.WaitAsync(Timeout);
        Assert.Equal(1, stream.DemandWaits);
        Assert.Equal(TimeSpan.Zero, stream.DemandWaitTime); // The in-progress wait is counted but not timed until it finishes.
        release.SetResult();
        Assert.Equal(100, await read.WaitAsync(Timeout));
        Assert.True(stream.DemandWaitTime > TimeSpan.Zero);
        Assert.Equal(stream.DemandWaitTime, stream.MaxDemandWaitTime);
        var total = stream.DemandWaitTime;
        stream.Position = 0;
        await stream.ReadExactlyAsync(new byte[100]);
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        stream.Position = stream.Length;
        Assert.Equal(0, await stream.ReadAsync(new byte[1].AsMemory()));
        Assert.Equal(1, stream.DemandWaits);
        Assert.Equal(total, stream.DemandWaitTime);
        Assert.Equal(total, stream.MaxDemandWaitTime);
    }

    [Fact]
    public async Task JoiningUnfinishedPrefetchAddsOneWaitAndAccumulatesTotalAndMaximum()
    {
        var releaseFirst = Signal();
        var releasePrefetch = Signal();
        var prefetchStarted = Signal();
        var handler = new RangeHandler
        {
            BeforeReadAsync = async (request, ct) =>
            {
                if (request.Headers.Range!.Ranges.Single().From == 0) await releaseFirst.Task.WaitAsync(ct);
                if (request.Headers.Range!.Ranges.Single().From == BlockSize)
                {
                    prefetchStarted.TrySetResult();
                    await releasePrefetch.Task.WaitAsync(ct);
                }
            },
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var first = stream.ReadAsync(new byte[1].AsMemory()).AsTask();
        await prefetchStarted.Task.WaitAsync(Timeout);
        releaseFirst.SetResult();
        Assert.Equal(1, await first.WaitAsync(Timeout));
        var firstWait = stream.DemandWaitTime;
        Assert.Equal(1, stream.DemandWaits);
        Assert.True(firstWait > TimeSpan.Zero);
        stream.Position = BlockSize;
        var second = stream.ReadAsync(new byte[1].AsMemory()).AsTask();
        Assert.Equal(2, stream.DemandWaits);
        releasePrefetch.SetResult();
        Assert.Equal(1, await second.WaitAsync(Timeout));
        Assert.Equal(1, BlockRequests(handler, 1));
        Assert.Equal(0, stream.PrefetchHits); // Joining speculation is a wait, not an already-ready hit.
        Assert.True(stream.DemandWaitTime > firstWait);
        Assert.True(stream.MaxDemandWaitTime >= firstWait);
        Assert.True(stream.MaxDemandWaitTime <= stream.DemandWaitTime);
    }

    [Fact]
    public async Task AWaitSpansFailedSpeculationAndDemandRetriesWithoutDoubleCounting()
    {
        var release = Signal();
        var started = Signal();
        var attempts = 0;
        var handler = new RangeHandler
        {
            BeforeSendAsync = async (request, _, ct) =>
            {
                if (request.Headers.Range!.Ranges.Single().From != BlockSize) return;
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
                if (attempt <= 2) throw new IOException("Simulated speculation and first demand failures.");
            },
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        await stream.ReadExactlyAsync(new byte[1]);
        await started.Task.WaitAsync(Timeout);
        var previousWaits = stream.DemandWaits;
        var previousTime = stream.DemandWaitTime;
        stream.Position = BlockSize;
        var read = stream.ReadAsync(new byte[1].AsMemory()).AsTask();
        Assert.Equal(previousWaits + 1, stream.DemandWaits);
        release.SetResult();
        Assert.Equal(1, await read.WaitAsync(Timeout));
        Assert.Equal(3, attempts);
        Assert.Equal(previousWaits + 1, stream.DemandWaits);
        Assert.True(stream.DemandWaitTime > previousTime);
    }

    [Fact]
    public async Task FarForwardSeekFetchesLazilyAtAnAlignedBlock()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        stream.Position = 2 * BlockSize + 17;
        Assert.Single(handler.Requests);
        var bytes = new byte[4096];
        Assert.Equal(bytes.Length, await stream.ReadAsync(bytes.AsMemory()));
        Assert.Equal(Data[(2 * BlockSize + 17)..(2 * BlockSize + 17 + bytes.Length)], bytes);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("bytes=2097152-3145727", handler.Requests[1].Headers.Range!.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(256 * 1024)]
    public async Task ForwardSeeksInsideACachedBlockDoNotFetchAgain(int skip)
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var bytes = new byte[1024];
        await stream.ReadExactlyAsync(bytes);
        await handler.WaitForRequestsAsync(4);
        stream.Seek(skip, SeekOrigin.Current);
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(Data[(1024 + skip)..(2048 + skip)], bytes);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task BackwardSeekAndSeekFromEndUseCachedBlocksAndSynchronousReads()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var bytes = new byte[100];
        stream.ReadExactly(bytes);
        Assert.Equal(10, stream.Seek(10, SeekOrigin.Begin));
        Assert.Equal(100, stream.Read(bytes, 0, bytes.Length));
        Assert.Equal(Data[10..110], bytes);
        Assert.Equal(Data.Length - 100, stream.Seek(-100, SeekOrigin.End));
        stream.ReadExactly(bytes);
        Assert.Equal(Data[^100..], bytes);
        await handler.WaitForRequestsAsync(4);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task AReadSpansCachedBlockBoundariesAndHandlesTheLastPartialBlock()
    {
        var payload = Data[..(2 * BlockSize + 123)];
        var handler = new RangeHandler { Payload = payload };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        stream.Position = BlockSize;
        await stream.ReadExactlyAsync(new byte[1]);
        stream.Position = 2 * BlockSize;
        await stream.ReadExactlyAsync(new byte[1]);
        stream.Position = BlockSize - 17;
        var bytes = new byte[BlockSize + 140];
        Assert.Equal(bytes.Length, await stream.ReadAsync(bytes.AsMemory()));
        Assert.Equal(payload[(BlockSize - 17)..], bytes);
        Assert.Equal(payload.Length, stream.Position);
        Assert.Equal(0, await stream.ReadAsync(new byte[1].AsMemory()));
        Assert.Equal(4, handler.Requests.Count);
        var last = Assert.Single(handler.Requests, request =>
            request.Headers.Range!.Ranges.Single().From == 2 * BlockSize);
        Assert.Equal($"bytes={2 * BlockSize}-{payload.Length - 1}", last.Headers.Range!.ToString());
    }

    [Fact]
    public async Task AReadReturnsAvailableBytesWithoutWaitingForTheNextBlock()
    {
        var release = Signal();
        var handler = new RangeHandler
        {
            BeforeReadAsync = async (request, ct) =>
            {
                if (request.Headers.Range!.Ranges.Single().From > 0) await release.Task.WaitAsync(ct);
            },
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        stream.Position = BlockSize - 17;
        var bytes = new byte[100];
        Assert.Equal(17, await stream.ReadAsync(bytes.AsMemory()).AsTask().WaitAsync(Timeout));
        Assert.Equal(Data[(BlockSize - 17)..BlockSize], bytes[..17]);
        release.SetResult();
    }

    [Fact]
    public async Task SeededRandomSeekReadSequencesAlwaysMatchThePayload()
    {
        var handler = new RangeHandler { Payload = LargeData };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var random = new Random(271828);
        for (var read = 0; read < 128; read++)
        {
            var position = random.Next(LargeData.Length + 1024);
            var size = Math.Min(random.Next(1, 128 * 1024), Math.Max(0, LargeData.Length - position));
            stream.Seek(position, SeekOrigin.Begin);
            var bytes = new byte[size];
            await stream.ReadExactlyAsync(bytes);
            Assert.Equal(LargeData.AsSpan(Math.Min(position, LargeData.Length), size).ToArray(), bytes);
            Assert.Equal(position + size, stream.Position);
        }
    }

    [Theory]
    [InlineData(HttpRangeStream.DefaultPrefetchBlocks)]
    [InlineData(HttpRangeStream.MaximumPrefetchBlocks)]
    public async Task SequentialReadsReuseEvictedArraysWithBoundedFullBlockAllocations(int depth)
    {
        var handler = new RangeHandler { Payload = LargeData };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default, depth);
        for (var pass = 0; pass < 2; pass++)
        {
            stream.Position = 0;
            await ReadFullBlocksAsync(stream);
        }
        // The 32 cached blocks plus speculative/leased fetch headroom bound the warm-up allocations.
        Assert.InRange(stream.BlockAllocations, 1, 32 + depth + 4);
        Assert.True(stream.BlockReuses > 0);
    }

    [Fact]
    public async Task AlternatingFarApartPositionsRemainExactWhileTheCacheThrashes()
    {
        var handler = new RangeHandler { Payload = LargeData };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var bytes = new byte[4096];
        for (var read = 0; read < 96; read++)
        {
            var block = read / 2 % 24 + (read % 2 == 0 ? 0 : 24);
            var position = block * BlockSize + 73;
            stream.Position = position;
            await stream.ReadExactlyAsync(bytes);
            Assert.Equal(LargeData[position..(position + bytes.Length)], bytes);
        }
        Assert.True(BlockRequests(handler, 0) > 1);
        Assert.True(stream.BlockReuses > 0);
    }

    [Fact]
    public async Task ThePartialTailKeepsItsExactLengthAfterFullBlockReuse()
    {
        var handler = new RangeHandler { Payload = LargeData };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        await ReadFullBlocksAsync(stream);
        Assert.True(stream.BlockReuses > 0);
        await stream.ReadExactlyAsync(new byte[1]); // Make the tail ready before the cross-block read.
        var tailStart = LargeData.Length / BlockSize * BlockSize;
        stream.Position = tailStart - 17;
        var bytes = new byte[BlockSize];
        var count = await stream.ReadAsync(bytes.AsMemory());
        Assert.Equal(LargeData.Length - tailStart + 17, count);
        Assert.Equal(LargeData[(tailStart - 17)..], bytes[..count]);
        Assert.Equal(LargeData.Length, stream.Position);
        Assert.Equal(0, await stream.ReadAsync(bytes.AsMemory()));
    }

    [Fact]
    public async Task EvictionDuringACopyDoesNotRecycleTheLeasedArray()
    {
        // The copy deliberately blocks a pool thread while ~50 fetches need others; allow for slow
        // thread injection on a loaded CI machine (one 5 s timeout seen under a parallel build).
        var timeout = TimeSpan.FromSeconds(30);
        var releaseFirst = Signal();
        var releaseRest = Signal();
        var copying = Signal();
        var releaseCopy = Signal();
        var handler = new RangeHandler
        {
            Payload = LargeData,
            BeforeReadAsync = (request, ct) =>
                (request.Headers.Range!.Ranges.Single().From == 0 ? releaseFirst : releaseRest).Task.WaitAsync(ct),
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default, HttpRangeStream.MaximumPrefetchBlocks);
        // Canceled waits leave enough queued speculation to evict the demanded block during its copy.
        foreach (var block in new[] { 0, 17, 34 })
        {
            using var cancellation = new CancellationTokenSource();
            stream.Position = block * BlockSize;
            var canceled = stream.ReadAsync(new byte[1].AsMemory(), cancellation.Token).AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        }
        stream.Position = 0;
        var bytes = new byte[4096];
        using var memory = new CopyMemory(bytes, () =>
        {
            copying.TrySetResult();
            releaseCopy.Task.WaitAsync(timeout).GetAwaiter().GetResult();
        });
        var read = Task.Run(async () => await stream.ReadAsync(memory.Memory));
        try
        {
            releaseFirst.SetResult();
            await copying.Task.WaitAsync(timeout);
            releaseRest.SetResult();
            await handler.WaitForRequestsAsync(1 + (LargeData.Length + BlockSize - 1) / BlockSize);
            await Task.WhenAll(handler.Bodies.Select(body => body.Disposal)).WaitAsync(timeout);
            Assert.True(stream.BlockReuses > 0);
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseRest.TrySetResult();
            releaseCopy.TrySetResult();
        }
        Assert.Equal(bytes.Length, await read.WaitAsync(timeout));
        Assert.Equal(LargeData[..bytes.Length], bytes);
        Assert.Equal(1, BlockRequests(handler, 0));
    }

    [Fact]
    public async Task LeastRecentlyUsedBlocksAreEvictedAfterTheThirtyTwoBlockLimit()
    {
        var handler = new RangeHandler { Payload = LargeData };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        for (var block = 0; block <= 34; block++)
        {
            stream.Position = block * BlockSize;
            var bytes = new byte[1];
            await stream.ReadExactlyAsync(bytes);
            Assert.Equal(LargeData[block * BlockSize], bytes[0]);
        }
        Assert.Equal(1, BlockRequests(handler, 0));
        stream.Position = 0;
        var first = new byte[100];
        await stream.ReadExactlyAsync(first);
        Assert.Equal(LargeData[..100], first);
        Assert.Equal(2, BlockRequests(handler, 0));
        var recentFetches = BlockRequests(handler, 34);
        stream.Position = 34 * BlockSize;
        await stream.ReadExactlyAsync(first);
        Assert.Equal(LargeData[(34 * BlockSize)..(34 * BlockSize + 100)], first);
        Assert.Equal(recentFetches, BlockRequests(handler, 34));
    }

    [Fact]
    public async Task LengthComesFromContentRangeNotThePartialBodyLength()
    {
        var handler = new RangeHandler
        {
            ConfigureResponse = (response, _) =>
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, Data.Length - 1, Data.Length + 1000),
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        Assert.Equal(Data.Length + 1000, stream.Length);
        Assert.True(Assert.Single(handler.Bodies).IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATransientDemandBodyFailureRetriesTheWholeBlockOnce(bool httpError)
    {
        var attempts = 0;
        var handler = new RangeHandler
        {
            FailAfterBytes = (request, _) => IsBlockRequest(request) && request.Headers.Range!.Ranges.Single().From == 0
                && Interlocked.Increment(ref attempts) == 1 ? 8192 : null,
            ReadError = () => httpError ? new HttpRequestException("Simulated body failure.") : new IOException("Simulated body failure."),
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var bytes = new byte[16384];
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(Data[..bytes.Length], bytes);
        Assert.Equal(2, attempts);
        Assert.Equal(2, BlockRequests(handler, 0));
        Assert.Equal(bytes.Length, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedFullBlockDemandAttemptRecyclesItsArrayBeforeRetrying(bool httpError)
    {
        var attempts = 0;
        var handler = new RangeHandler
        {
            Payload = Data[..BlockSize],
            FailAfterBytes = (request, _) => IsBlockRequest(request) && Interlocked.Increment(ref attempts) == 1 ? 8192 : null,
            ReadError = () => httpError ? new HttpRequestException("Simulated body failure.") : new IOException("Simulated body failure."),
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var bytes = new byte[BlockSize];
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(handler.Payload, bytes);
        Assert.Equal(2, attempts);
        Assert.Equal(2, BlockRequests(handler, 0));
        Assert.Equal(1, stream.BlockAllocations);
        Assert.Equal(1, stream.BlockReuses);
        Assert.Equal(BlockSize + 8192, stream.DownloadedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATransientDemandRequestFailureIsRetriedOnce(bool httpError)
    {
        var attempts = 0;
        var handler = new RangeHandler
        {
            BeforeSendAsync = (request, _, _) =>
            {
                if (IsBlockRequest(request) && request.Headers.Range!.Ranges.Single().From == 0
                    && Interlocked.Increment(ref attempts) == 1)
                    throw httpError ? new HttpRequestException("Simulated request failure.") : new IOException("Simulated request failure.");
                return Task.CompletedTask;
            },
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var bytes = new byte[100];
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(Data[..bytes.Length], bytes);
        Assert.Equal(2, attempts);
        Assert.Equal(2, BlockRequests(handler, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASecondDemandFailureThrowsWithoutReturningPartialBlockBytes(bool httpError)
    {
        var handler = new RangeHandler
        {
            FailAfterBytes = (request, _) => IsBlockRequest(request) && request.Headers.Range!.Ranges.Single().From == 0 ? 8192 : null,
            ReadError = () => httpError ? new HttpRequestException("Simulated body failure.") : new IOException("Simulated body failure."),
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var read = stream.ReadAsync(new byte[100].AsMemory()).AsTask();
        if (httpError) await Assert.ThrowsAsync<HttpRequestException>(() => read);
        else await Assert.ThrowsAsync<IOException>(() => read);
        Assert.Equal(2, BlockRequests(handler, 0));
        Assert.Equal(0, stream.Position);
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task AReadRefetchesAFailedPrefetchUsingTheDemandRetryPolicy()
    {
        var attempts = 0;
        var handler = new RangeHandler
        {
            BeforeSendAsync = (request, _, _) =>
            {
                if (request.Headers.Range!.Ranges.Single().From == BlockSize && Interlocked.Increment(ref attempts) <= 2)
                    throw new IOException("Simulated speculative and first demand failures.");
                return Task.CompletedTask;
            },
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        await stream.ReadExactlyAsync(new byte[1]);
        await handler.WaitForRequestsAsync(4);
        stream.Position = BlockSize;
        var bytes = new byte[100];
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(Data[BlockSize..(BlockSize + bytes.Length)], bytes);
        Assert.Equal(3, attempts);
        Assert.Equal(3, BlockRequests(handler, 1));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ignored-range")]
    [InlineData("wrong-start")]
    [InlineData("wrong-end")]
    [InlineData("changed-length")]
    [InlineData("wrong-unit")]
    public async Task InvalidBlockContentRangeThrows(string kind)
    {
        var handler = new RangeHandler
        {
            ConfigureResponse = (response, _) =>
            {
                if (response.Content.Headers.ContentRange?.From != BlockSize) return;
                if (kind == "ignored-range") response.StatusCode = HttpStatusCode.OK;
                else response.Content.Headers.ContentRange = kind switch
                {
                    "wrong-start" => new ContentRangeHeaderValue(BlockSize + 1, 2 * BlockSize - 1, Data.Length),
                    "wrong-end" => new ContentRangeHeaderValue(BlockSize, 2 * BlockSize - 2, Data.Length),
                    "changed-length" => new ContentRangeHeaderValue(BlockSize, 2 * BlockSize - 1, Data.Length + 1),
                    "wrong-unit" => new ContentRangeHeaderValue(BlockSize, 2 * BlockSize - 1, Data.Length) { Unit = "items" },
                    _ => null,
                };
            },
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        stream.Position = BlockSize;
        var error = await Assert.ThrowsAsync<IOException>(async () => await stream.ReadExactlyAsync(new byte[1]));
        Assert.Equal("The media server returned an invalid range response.", error.Message);
        Assert.Equal(2, BlockRequests(handler, 1));
        Assert.Equal(BlockSize, stream.Position);
    }

    [Fact]
    public async Task CallerCancellationOnlyCancelsTheWaitAndTheNextReadSharesTheFetch()
    {
        var release = Signal();
        var started = Signal();
        var active = 0;
        var canceled = 0;
        var handler = new RangeHandler
        {
            BeforeReadAsync = async (request, ct) =>
            {
                if (!IsBlockRequest(request)) return;
                if (Interlocked.Increment(ref active) == 3) started.TrySetResult();
                try { await release.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { Interlocked.Increment(ref canceled); throw; }
                finally { Interlocked.Decrement(ref active); }
            },
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        using var cancellation = new CancellationTokenSource();
        var first = stream.ReadAsync(new byte[100].AsMemory(), cancellation.Token).AsTask();
        await started.Task.WaitAsync(Timeout);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(0, stream.Position);
        Assert.Equal(0, canceled);
        Assert.Equal(3, active);
        Assert.Equal(1, stream.DemandWaits);
        Assert.True(stream.DemandWaitTime > TimeSpan.Zero);
        Assert.Equal(stream.DemandWaitTime, stream.MaxDemandWaitTime);
        var canceledWaitTime = stream.DemandWaitTime;
        var bytes = new byte[100];
        var second = stream.ReadAsync(bytes.AsMemory()).AsTask();
        Assert.Equal(2, stream.DemandWaits);
        release.SetResult();
        Assert.Equal(bytes.Length, await second.WaitAsync(Timeout));
        Assert.Equal(Data[..bytes.Length], bytes);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(1, BlockRequests(handler, 0));
        Assert.True(stream.DemandWaitTime > canceledWaitTime);
        Assert.True(stream.MaxDemandWaitTime <= stream.DemandWaitTime);
    }

    [Fact]
    public async Task QueuedDemandAndPrefetchRequestsNeverExceedThreeActiveFetches()
    {
        var release = Signal();
        var started = Signal();
        var active = 0;
        var maximum = 0;
        var counterLock = new object();
        var handler = new RangeHandler
        {
            Payload = LargeData,
            BeforeReadAsync = async (request, ct) =>
            {
                if (!IsBlockRequest(request)) return;
                lock (counterLock)
                {
                    active++;
                    maximum = Math.Max(maximum, active);
                    if (active == 3) started.TrySetResult();
                }
                try { await release.Task.WaitAsync(ct); }
                finally { lock (counterLock) active--; }
            },
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        using var cancellation = new CancellationTokenSource();
        var first = stream.ReadAsync(new byte[1].AsMemory(), cancellation.Token).AsTask();
        await started.Task.WaitAsync(Timeout);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        stream.Position = 10 * BlockSize;
        var bytes = new byte[100];
        var second = stream.ReadAsync(bytes.AsMemory()).AsTask();
        Assert.Equal(4, handler.Requests.Count);
        release.SetResult();
        Assert.Equal(bytes.Length, await second.WaitAsync(Timeout));
        await handler.WaitForRequestsAsync(7);
        Assert.Equal(LargeData[(10 * BlockSize)..(10 * BlockSize + bytes.Length)], bytes);
        Assert.Equal(3, maximum);
        Assert.Equal(7, handler.Requests.Count);
    }

    [Fact]
    public async Task DisposeCancelsAllInFlightFetchesAndDisposesTheirBodies()
    {
        var started = Signal();
        var allCanceled = Signal();
        var active = 0;
        var canceled = 0;
        var handler = new RangeHandler
        {
            BeforeReadAsync = async (request, ct) =>
            {
                if (!IsBlockRequest(request)) return;
                if (Interlocked.Increment(ref active) == 3) started.TrySetResult();
                try { await Task.Delay(System.Threading.Timeout.Infinite, ct); }
                catch (OperationCanceledException)
                {
                    if (Interlocked.Increment(ref canceled) == 3) allCanceled.TrySetResult();
                    throw;
                }
            },
        };
        using var client = Create(handler);
        using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var read = stream.ReadAsync(new byte[100].AsMemory()).AsTask();
        await started.Task.WaitAsync(Timeout);
        stream.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await allCanceled.Task.WaitAsync(Timeout);
        await Task.WhenAll(handler.Bodies.Select(body => body.Disposal)).WaitAsync(Timeout);
        Assert.Equal(3, canceled);
        Assert.All(handler.Bodies, body => Assert.True(body.IsDisposed));
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
    }

    [Fact]
    public async Task IgnoredRangesReadSequentiallyWithOneBody()
    {
        var handler = new RangeHandler { IgnoreRanges = true };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        Assert.Equal(Data, output.ToArray());
        Assert.Single(handler.Requests);
        Assert.Equal(0, stream.BlockFetches);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(256 * 1024)]
    public async Task IgnoredRangesSkipSmallForwardSeeksWithoutReopening(int skip)
    {
        var handler = new RangeHandler { IgnoreRanges = true };
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
    public async Task IgnoredRangesUseContentLengthAndReopenFromZeroToSeek()
    {
        var handler = new RangeHandler { IgnoreRanges = true };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        Assert.Equal(Data.Length, stream.Length);
        stream.Position = 2 * BlockSize;
        var bytes = new byte[100];
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(Data[(2 * BlockSize)..(2 * BlockSize + 100)], bytes);
        stream.Position = 10;
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(Data[10..110], bytes);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("bytes=0-", request.Headers.Range!.ToString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IgnoredRangesRetryAMidBodyFailureOnce(bool httpError)
    {
        var handler = new RangeHandler
        {
            IgnoreRanges = true,
            FailAfterBytes = (_, request) => request == 1 ? 8192 : null,
            ReadError = () => httpError ? new HttpRequestException("Simulated body failure.") : new IOException("Simulated body failure."),
        };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, 8192);
        Assert.Equal(Data, output.ToArray());
        Assert.Equal(2, handler.Requests.Count);
        Assert.True(handler.Bodies[0].IsDisposed);
    }

    [Fact]
    public async Task IgnoredRangesReturnASecondBodyFailureWithoutAReconnectLoop()
    {
        var handler = new RangeHandler { IgnoreRanges = true, FailAfterBytes = (_, request) => request == 1 ? 8192 : 0 };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        var bytes = new byte[8192];
        await stream.ReadExactlyAsync(bytes);
        await Assert.ThrowsAsync<IOException>(async () => await stream.ReadExactlyAsync(bytes));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(8192, stream.Position);
    }

    [Fact]
    public async Task IgnoredRangesRetryABodyFailureWhileSkipping()
    {
        var handler = new RangeHandler { IgnoreRanges = true, FailAfterBytes = (_, request) => request == 1 ? 8192 : null };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        stream.Position = 32 * 1024;
        var bytes = new byte[100];
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(Data[(32 * 1024)..(32 * 1024 + 100)], bytes);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("bytes=0-", handler.Requests[1].Headers.Range!.ToString());
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

    [Fact]
    public async Task SeekBeyondEndAndEmptyReadsDoNotFetchMoreBytes()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        stream.Position = Data.Length + 1L;
        Assert.Equal(0, await stream.ReadAsync(new byte[1].AsMemory()));
        stream.Position = BlockSize;
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.Single(handler.Requests);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
    }

    [Fact]
    public async Task BlockFetchesCannotCrossAProfileFence()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default);
        client.UseProfile("other-profile");
        stream.Position = BlockSize;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadExactlyAsync(new byte[1]));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalClosesTheBodyAndRejectsFurtherReads(bool ignoredRange)
    {
        var handler = new RangeHandler { IgnoreRanges = ignoredRange };
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
    public async Task CanceledReadDoesNotFetchAnyBlocks()
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
