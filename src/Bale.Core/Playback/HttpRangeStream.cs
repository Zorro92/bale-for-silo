using System.Buffers;
using System.Diagnostics;
using System.Net;

namespace Bale.Core.Playback;

/// <summary>
/// A read-only, seekable HTTP body for the platform's random-access stream adapter. Windows' MKV
/// source alternates read positions; one request per jump starved playback. Bounded, aligned blocks
/// keep both positions warm, with every fetch still inside PlaybackMedia's captured auth scope.
/// </summary>
public sealed class HttpRangeStream : Stream
{
    public const int DefaultPrefetchBlocks = 2;
    public const int MaximumPrefetchBlocks = 16;
    private const int BlockSize = 1024 * 1024;
    private const int CacheCapacity = 32;
    private const int ForwardSkipLimit = 256 * 1024;
    private readonly PlaybackMedia _media;
    private readonly long _length;
    private readonly bool _supportsRanges;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _fetchSlots = new(3, 3);
    private readonly CancellationTokenSource _fetchCancellation = new();
    private readonly CancellationToken _fetchToken;
    private readonly object _cacheLock = new();
    private readonly Dictionary<long, CachedBlock> _cache = [];
    private readonly LinkedList<long> _lru = new();
    private readonly Dictionary<long, BlockFetch> _inFlight = [];
    private readonly Stack<byte[]> _freeBlocks = new();
    private HttpResponseMessage? _response;
    private Stream? _body;
    private long _bodyPosition;
    private long _position;
    private long _downloadedBytes;
    private long _downloadTicks;
    private long _demandWaits;
    private long _demandWaitTicks;
    private long _maxDemandWaitTicks;
    private long _blockAllocations;
    private long _blockReuses;
    private volatile bool _disposed;

    /// <summary>Immutable per-stream speculation depth; the cache and three-fetch concurrency limit are unchanged.</summary>
    public int PrefetchBlocks { get; }
    public bool SupportsRanges => _supportsRanges;

    /// <summary>Reads that awaited an unfinished block fetch, including joined prefetches; excludes cache hits and the read lock.</summary>
    public long DemandWaits => Interlocked.Read(ref _demandWaits);

    /// <summary>Completed demand waits, including canceled/failed reads and retries; unavailable without HTTP range support.</summary>
    public TimeSpan DemandWaitTime => TimeSpan.FromSeconds((double)Interlocked.Read(ref _demandWaitTicks) / Stopwatch.Frequency);
    public TimeSpan MaxDemandWaitTime => TimeSpan.FromSeconds((double)Interlocked.Read(ref _maxDemandWaitTicks) / Stopwatch.Frequency);

    /// <summary>Actual HTTP body bytes, including prefetch, skipped bytes and partial failed downloads; cache reads do not count.</summary>
    public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);

    /// <summary>Time inside HTTP body reads, summed across concurrent fetches; excludes cache reads and idle playback.</summary>
    public TimeSpan DownloadTime => TimeSpan.FromSeconds((double)Interlocked.Read(ref _downloadTicks) / Stopwatch.Frequency);

    /// <summary>Full-size block arrays allocated on the large object heap; excludes the unpooled tail block.</summary>
    public long BlockAllocations => Interlocked.Read(ref _blockAllocations);

    /// <summary>Full-size block fetches that reused an evicted or failed attempt's array.</summary>
    public long BlockReuses => Interlocked.Read(ref _blockReuses);

    /// <summary>Diagnostics only: counts, never offsets tied to URLs. PrefetchHits counts first use of ready prefetched blocks.</summary>
    public long Reads, BlockFetches, CacheHits, PrefetchHits, BytesRead, SmallestRead = long.MaxValue;

    private sealed class CachedBlock(byte[] data, bool prefetched)
    {
        public byte[] Data { get; } = data;
        public bool Prefetched { get; set; } = prefetched;
        public LinkedListNode<long>? Node { get; set; }
        public int Leases { get; set; }
        public bool Evicted { get; set; }
    }

    private sealed record BlockFetch(Task<CachedBlock> Task, bool IsPrefetch)
    {
        public int Waiters { get; set; }
        public CachedBlock? Block { get; set; }
    }

    private HttpRangeStream(PlaybackMedia media, HttpResponseMessage? response, Stream? body, long length, bool supportsRanges, int prefetchBlocks)
    {
        PrefetchBlocks = prefetchBlocks;
        _media = media;
        _response = response;
        _body = body;
        _length = length;
        _supportsRanges = supportsRanges;
        _fetchToken = _fetchCancellation.Token;
    }

    internal static async Task<HttpRangeStream> OpenAsync(PlaybackMedia media, CancellationToken ct, int prefetchBlocks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(prefetchBlocks, DefaultPrefetchBlocks);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefetchBlocks, MaximumPrefetchBlocks);
        var response = await media.SendAsync(media.Uri, 0, null, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            PlaybackMedia.RequireSuccess(response);
            var supportsRanges = response.StatusCode == HttpStatusCode.PartialContent;
            long length;
            if (supportsRanges && TryRangeLength(response, 0, out var total)) length = total;
            else if (response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentLength is >= 0 and { } size) length = size;
            else throw new InvalidDataException("The media response has no valid total length.");

            if (supportsRanges)
            {
                // The open-ended probe supplies the total, not a body to keep alive between seeks.
                response.Dispose();
                return new(media, null, null, length, true, prefetchBlocks);
            }
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new(media, response, body, length, false, prefetchBlocks);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length { get { ThrowIfDisposed(); return _length; } }

    public override long Position
    {
        get { ThrowIfDisposed(); return _position; }
        set
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (buffer.Length == 0 || _position >= _length) return 0;
            Reads++;
            SmallestRead = Math.Min(SmallestRead, buffer.Length);
            var count = _supportsRanges
                ? await ReadBlocksAsync(buffer, cancellationToken).ConfigureAwait(false)
                : await ReadSequentialAsync(buffer, cancellationToken).ConfigureAwait(false);
            _position += count;
            BytesRead += count;
            return count;
        }
        finally
        {
            _readLock.Release();
        }
    }

    private async Task<int> ReadBlocksAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var copied = 0;
        while (copied < buffer.Length && _position + copied < _length)
        {
            var position = _position + copied;
            var index = position / BlockSize;
            CachedBlock? block;
            if (copied == 0)
            {
                var pending = GetBlockAsync(index, ct);
                var waitStarted = pending.IsCompleted ? (long?)null : Stopwatch.GetTimestamp();
                if (waitStarted is not null) Interlocked.Increment(ref _demandWaits);
                try
                {
                    PrefetchFollowingBlocks(index);
                    block = await pending.ConfigureAwait(false);
                }
                finally
                {
                    if (waitStarted is { } started)
                    {
                        // One read waits once even if failed speculation falls back to demand retries.
                        var elapsed = Stopwatch.GetTimestamp() - started;
                        Interlocked.Add(ref _demandWaitTicks, elapsed);
                        // _readLock serializes writers; the stats reader only needs atomic long reads.
                        Interlocked.Exchange(ref _maxDemandWaitTicks, Math.Max(_maxDemandWaitTicks, elapsed));
                    }
                }
            }
            else
            {
                bool disposed;
                lock (_cacheLock)
                {
                    disposed = _disposed;
                    block = disposed ? null : FindCachedBlockLocked(index);
                    if (block is not null) block.Leases++;
                }
                ThrowIfDisposed(disposed);
                // Stream reads need not fill the buffer: don't wait on another block after copying bytes.
                if (block is null) break;
            }
            try
            {
                if (copied != 0) PrefetchFollowingBlocks(index);
                var offset = (int)(position % BlockSize);
                var count = Math.Min(buffer.Length - copied, block.Data.Length - offset);
                block.Data.AsMemory(offset, count).CopyTo(buffer[copied..]);
                copied += count;
            }
            finally
            {
                lock (_cacheLock) ReleaseBlockLocked(block);
            }
        }
        return copied;
    }

    private CachedBlock? FindCachedBlockLocked(long index)
    {
        if (!_cache.TryGetValue(index, out var block)) return null;
        TouchBlockLocked(block);
        CacheHits++;
        if (block.Prefetched)
        {
            PrefetchHits++;
            block.Prefetched = false;
        }
        return block;
    }

    private void TouchBlockLocked(CachedBlock block)
    {
        _lru.Remove(block.Node!);
        _lru.AddLast(block.Node!);
    }

    private async Task<CachedBlock> GetBlockAsync(long index, CancellationToken ct)
    {
        BlockFetch? fetch = null;
        CachedBlock? ready = null;
        bool disposed;
        lock (_cacheLock)
        {
            disposed = _disposed;
            if (!disposed)
            {
                ready = FindCachedBlockLocked(index);
                if (ready is not null) ready.Leases++;
                else
                {
                    fetch = _inFlight.TryGetValue(index, out var existing) ? existing : StartFetchLocked(index, false);
                    fetch.Waiters++;
                    if (fetch.Block is { } completed) completed.Leases++;
                }
            }
        }
        ThrowIfDisposed(disposed);
        if (ready is not null) return ready;
        var shared = fetch!;
        try
        {
            CachedBlock block;
            try
            {
                // A caller abandoning its read must not cancel a fetch shared with later reads.
                block = await shared.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (shared.IsPrefetch && !ct.IsCancellationRequested && !_fetchToken.IsCancellationRequested)
            {
                // Speculation gets one attempt. A read joining failed speculation gets its own demand retry policy.
                return await GetBlockAsync(index, ct).ConfigureAwait(false);
            }
            lock (_cacheLock)
            {
                disposed = _disposed;
                if (!disposed)
                {
                    block.Leases++;
                    if (_cache.TryGetValue(index, out var cached) && ReferenceEquals(block, cached)) TouchBlockLocked(block);
                    // Waiting on speculation isn't a hit on an already-ready prefetched block.
                    block.Prefetched = false;
                }
            }
            ThrowIfDisposed(disposed);
            return block;
        }
        finally
        {
            lock (_cacheLock)
            {
                shared.Waiters--;
                if (shared.Block is { } completed) ReleaseBlockLocked(completed);
            }
        }
    }

    private void PrefetchFollowingBlocks(long index)
    {
        lock (_cacheLock)
        {
            if (_disposed) return;
            for (var next = index + 1; next <= index + PrefetchBlocks && next <= (_length - 1) / BlockSize; next++)
            {
                if (!_cache.ContainsKey(next) && !_inFlight.ContainsKey(next)) StartFetchLocked(next, true);
            }
        }
    }

    private BlockFetch StartFetchLocked(long index, bool prefetch)
    {
        // Schedule outside the read path. The cache lock ensures registration precedes completion,
        // even when a transport supplies a whole block synchronously.
        var fetch = new BlockFetch(Task.Run(() => FetchBlockAsync(index, prefetch)), prefetch);
        _inFlight.Add(index, fetch);
        _ = ObserveFetchAsync(fetch.Task);
        return fetch;
    }

    private static async Task ObserveFetchAsync(Task<CachedBlock> task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { } // Also observe demand fetches whose only waiter was canceled; never log transport details.
    }

    private async Task<CachedBlock> FetchBlockAsync(long index, bool prefetch)
    {
        CachedBlock? fetchedBlock = null;
        try
        {
            await _fetchSlots.WaitAsync(_fetchToken).ConfigureAwait(false);
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    byte[]? data = null;
                    try
                    {
                        var start = index * BlockSize;
                        var size = (int)Math.Min(BlockSize, _length - start);
                        Interlocked.Increment(ref BlockFetches);
                        using var response = await _media.SendAsync(_media.Uri, start, size,
                            HttpCompletionOption.ResponseHeadersRead, _fetchToken).ConfigureAwait(false);
                        PlaybackMedia.RequireSuccess(response);
                        if (!TryRangeLength(response, start, out var total) || total != _length
                            || response.Content.Headers.ContentRange!.To != start + size - 1)
                            throw new IOException("The media server returned an invalid range response.");

                        if (size == BlockSize)
                        {
                            lock (_cacheLock)
                            {
                                if (_freeBlocks.TryPop(out data)) Interlocked.Increment(ref _blockReuses);
                            }
                            if (data is null)
                            {
                                data = new byte[BlockSize];
                                Interlocked.Increment(ref _blockAllocations);
                            }
                        }
                        else data = new byte[size];
                        using var body = await response.Content.ReadAsStreamAsync(_fetchToken).ConfigureAwait(false);
                        var downloaded = 0;
                        while (downloaded < data.Length)
                        {
                            var count = await ReadNetworkAsync(body, data.AsMemory(downloaded), _fetchToken).ConfigureAwait(false);
                            if (count == 0) throw new EndOfStreamException("The media block ended before its declared length.");
                            downloaded += count;
                        }
                        var block = new CachedBlock(data, prefetch);
                        var disposed = false;
                        lock (_cacheLock)
                        {
                            disposed = _disposed;
                            if (!disposed)
                            {
                                var fetch = _inFlight[index];
                                // Pin the in-flight result and its waiters until each takes a copy lease or cancels.
                                block.Leases = 1 + fetch.Waiters;
                                AddBlockLocked(index, block);
                                fetch.Block = block;
                                fetchedBlock = block;
                                data = null; // The cache and leases now own the array, even if it is evicted.
                            }
                        }
                        ThrowIfDisposed(disposed);
                        return block;
                    }
                    catch (Exception error) when (!prefetch && attempt == 0 && error is IOException or HttpRequestException)
                    {
                        // Retry a demand block once; a second failure belongs to playback recovery.
                    }
                    finally
                    {
                        if (data is not null)
                        {
                            lock (_cacheLock) RecycleBlockLocked(data);
                        }
                    }
                }
            }
            finally
            {
                _fetchSlots.Release();
            }
        }
        finally
        {
            lock (_cacheLock)
            {
                _inFlight.Remove(index);
                if (fetchedBlock is not null) ReleaseBlockLocked(fetchedBlock);
            }
        }
    }

    private void AddBlockLocked(long index, CachedBlock block)
    {
        block.Node = _lru.AddLast(index);
        _cache.Add(index, block);
        if (_cache.Count > CacheCapacity)
        {
            var oldest = _lru.First!;
            var evicted = _cache[oldest.Value];
            _cache.Remove(oldest.Value);
            _lru.RemoveFirst();
            evicted.Evicted = true;
            if (evicted.Leases == 0) RecycleBlockLocked(evicted.Data);
        }
    }

    private void ReleaseBlockLocked(CachedBlock block)
    {
        if (--block.Leases == 0 && block.Evicted) RecycleBlockLocked(block.Data);
    }

    private void RecycleBlockLocked(byte[] data)
    {
        if (data.Length == BlockSize && _freeBlocks.Count < 4) _freeBlocks.Push(data);
    }

    private async Task<int> ReadSequentialAsync(Memory<byte> buffer, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _fetchToken);
        var token = cancellation.Token;
        var forward = _position - _bodyPosition;
        if (_body is null || forward < 0 || forward > ForwardSkipLimit)
            await ReopenSequentialAsync(token).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await SkipToPositionAsync(token).ConfigureAwait(false);
                var body = _body ?? throw new ObjectDisposedException(nameof(HttpRangeStream));
                var count = await ReadNetworkAsync(body, buffer[..(int)Math.Min(buffer.Length, _length - _position)], token)
                    .ConfigureAwait(false);
                if (count == 0) throw new IOException("The media stream ended before its declared length.");
                _bodyPosition += count;
                return count;
            }
            catch (Exception error) when (attempt == 0 && error is IOException or HttpRequestException)
            {
                await ReopenSequentialAsync(token).ConfigureAwait(false);
            }
        }
    }

    private async Task SkipToPositionAsync(CancellationToken ct)
    {
        if (_bodyPosition == _position) return;
        var body = _body ?? throw new ObjectDisposedException(nameof(HttpRangeStream));
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (_bodyPosition < _position)
            {
                var count = await ReadNetworkAsync(body, buffer.AsMemory(0, (int)Math.Min(buffer.Length, _position - _bodyPosition)), ct)
                    .ConfigureAwait(false);
                if (count == 0) throw new IOException("The media stream ended before the seek position.");
                _bodyPosition += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<int> ReadNetworkAsync(Stream body, Memory<byte> buffer, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var count = await body.ReadAsync(buffer, ct).ConfigureAwait(false);
            Interlocked.Add(ref _downloadedBytes, count);
            return count;
        }
        finally
        {
            // Also count time spent in failed reads. This runs outside _cacheLock, just like I/O.
            Interlocked.Add(ref _downloadTicks, Stopwatch.GetTimestamp() - started);
        }
    }

    private async Task ReopenSequentialAsync(CancellationToken ct)
    {
        HttpResponseMessage? previous = null;
        bool disposed;
        lock (_cacheLock)
        {
            disposed = _disposed;
            if (!disposed)
            {
                previous = _response;
                _response = null;
                _body = null;
            }
        }
        ThrowIfDisposed(disposed);
        previous?.Dispose();
        // A 200 response at open means Range is unsupported. Reopen at zero and let the same
        // skip-reader advance to the target, rather than mistaking a full body for a ranged one.
        var response = await _media.SendAsync(_media.Uri, 0, null, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            var validRange = TryRangeLength(response, 0, out var total) && total == _length
                && response.Content.Headers.ContentRange!.To == _length - 1;
            var validFullBody = response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentLength == _length;
            if (!validRange && !validFullBody) throw new IOException("The media server returned an invalid range response.");
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            lock (_cacheLock)
            {
                disposed = _disposed;
                if (!disposed)
                {
                    _response = response;
                    _body = body;
                    _bodyPosition = 0;
                }
            }
            ThrowIfDisposed(disposed);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static bool TryRangeLength(HttpResponseMessage response, long start, out long length)
    {
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode == HttpStatusCode.PartialContent && range is not null
            && range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) && range.From == start
            && range.To is { } end && end >= start && range.Length is { } total && total > end)
        {
            length = total;
            return true;
        }
        length = 0;
        return false;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return Position;
    }

    public override void Flush() => ThrowIfDisposed();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            HttpResponseMessage? response = null;
            var cancel = false;
            lock (_cacheLock)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _cache.Clear();
                    _lru.Clear();
                    _inFlight.Clear();
                    _freeBlocks.Clear();
                    response = _response;
                    _response = null;
                    _body = null;
                    cancel = true;
                }
            }
            if (cancel)
            {
                _fetchCancellation.Cancel();
                _fetchCancellation.Dispose();
                response?.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    // Every lock in this type records disposal and throws after leaving the lock. A disposal racing a
    // background fetch threw inside the lock, and on 2-CPU CI runners the lock stayed held by an idle
    // pool thread (seen in a dump), which hung Dispose for good.
    private void ThrowIfDisposed() => ThrowIfDisposed(_disposed);
    private void ThrowIfDisposed(bool disposed) => ObjectDisposedException.ThrowIf(disposed, this);
}
