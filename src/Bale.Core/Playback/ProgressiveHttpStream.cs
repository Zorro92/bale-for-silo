using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;

namespace Bale.Core.Playback;

/// <summary>A progressive remux refused the request's range (HTTP 409): reopen at the current position.</summary>
public sealed class ProgressiveRangeRefusedException()
    : HttpRequestException("The progressive stream refused the request's range.", null, HttpStatusCode.Conflict);

/// <summary>
/// One forward-only <c>server_remux_progressive</c> response (PlaybackProgressive). It never sends a
/// Range: the server answers 200 from the plan's origin, and with progressive_range_refusal_v1 a
/// mid-stream range would be 409. A bounded pump reads ahead of FFmpeg to absorb network jitter.
/// <para>
/// Reads never throw. FFmpegInteropX's non-seekable input (FFmpegMediaSource.cpp InputStreamRead)
/// calls ReadAsync(...).get() with no exception handling, so a faulted read would unwind through
/// FFmpeg's C demuxer. A transport failure, cancellation or disposal instead ends the data (0 bytes),
/// and <see cref="Faulted"/> tells the player the end was not the title's: protocol §4 says an
/// incomplete progressive response is a transport failure, never the end of the title.
/// </para>
/// </summary>
public sealed class ProgressiveHttpStream : Stream
{
    public const int ChunkSize = 64 * 1024;
    /// <summary>16 MiB: about two seconds of a 4K HEVC remux, far below HttpRangeStream's 32 MiB cache.</summary>
    public const int BufferChunks = 256;
    private readonly HttpResponseMessage _response;
    private readonly Stream _body;
    private readonly Channel<Chunk> _chunks;
    private readonly CancellationTokenSource _cancellation;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly Task _pump;
    private Chunk? _current;
    private long _position;
    private long _downloadedBytes;
    private long _downloadTicks;
    private long _bufferedBytes;
    private int _faulted;
    private int _completed;
    private volatile bool _disposed;

    private sealed class Chunk(byte[] data, int length)
    {
        public byte[] Data { get; } = data;
        public int Length { get; } = length;
        public int Offset { get; set; }
    }

    private ProgressiveHttpStream(HttpResponseMessage response, Stream body, CancellationToken mountToken)
    {
        _response = response;
        _body = body;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(mountToken);
        _chunks = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(BufferChunks)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
        });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>
    /// Opens the stream. A 409 is <see cref="ProgressiveRangeRefusedException"/>; any other non-2xx is
    /// the usual media HttpRequestException. Only 200 is a progressive answer: a 206 would claim a
    /// byte range this delivery does not have.
    /// </summary>
    internal static async Task<ProgressiveHttpStream> OpenAsync(PlaybackMedia media, CancellationToken mountToken)
    {
        // offset: null sends no Range header at all, so the server streams from its seek origin.
        var response = await media.SendAsync(media.Uri, null, null, HttpCompletionOption.ResponseHeadersRead, mountToken)
            .ConfigureAwait(false);
        try
        {
            if (response.StatusCode == HttpStatusCode.Conflict) throw new ProgressiveRangeRefusedException();
            PlaybackMedia.RequireSuccess(response);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("The progressive stream did not answer from its origin.");
            var body = await response.Content.ReadAsStreamAsync(mountToken).ConfigureAwait(false);
            return new(response, body, mountToken);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>The response ended before the server finished it (abort, reset, stall reap), or was cancelled.</summary>
    public bool Faulted => Volatile.Read(ref _faulted) != 0;

    /// <summary>The server ended the response cleanly: FFmpeg read the source to its end.</summary>
    public bool Completed => Volatile.Read(ref _completed) != 0;

    /// <summary>Downloaded bytes not yet handed to the demuxer.</summary>
    public long BufferedBytes => Interlocked.Read(ref _bufferedBytes);

    /// <summary>Actual HTTP body bytes received; same meaning as HttpRangeStream.DownloadedBytes.</summary>
    public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);

    /// <summary>Time inside HTTP body reads, excluding time the pump waited for a full buffer to drain.</summary>
    public TimeSpan DownloadTime => TimeSpan.FromSeconds((double)Interlocked.Read(ref _downloadTicks) / Stopwatch.Frequency);

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => Interlocked.Read(ref _position);
        set => throw new NotSupportedException();
    }

    private async Task PumpAsync()
    {
        var token = _cancellation.Token;
        byte[]? buffer = null;
        try
        {
            while (true)
            {
                buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
                var started = Stopwatch.GetTimestamp();
                int count;
                try { count = await _body.ReadAsync(buffer.AsMemory(0, ChunkSize), token).ConfigureAwait(false); }
                finally { Interlocked.Add(ref _downloadTicks, Stopwatch.GetTimestamp() - started); }
                if (count == 0)
                {
                    Interlocked.Exchange(ref _completed, 1);
                    break;
                }
                Interlocked.Add(ref _downloadedBytes, count);
                Interlocked.Add(ref _bufferedBytes, count);
                var chunk = new Chunk(buffer, count);
                buffer = null;
                try { await _chunks.Writer.WriteAsync(chunk, token).ConfigureAwait(false); }
                catch
                {
                    Interlocked.Add(ref _bufferedBytes, -count);
                    ArrayPool<byte>.Shared.Return(chunk.Data);
                    throw;
                }
            }
        }
        catch (Exception)
        {
            // Never log transport details. Chunked HTTP/1.1 without its final chunk, an HTTP/2 reset
            // and the server's stall reap all land here; so do cancellation and disposal.
            Interlocked.Exchange(ref _faulted, 1);
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
            _chunks.Writer.TryComplete();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length == 0 || _disposed) return 0;
        try
        {
            await _readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return 0;
        }
        try
        {
            if (_disposed) return 0;
            if (_current is null)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
                while (!_chunks.Reader.TryRead(out _current))
                {
                    if (!await _chunks.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false)) return 0;
                }
            }
            var chunk = _current;
            var count = Math.Min(destination.Length, chunk.Length - chunk.Offset);
            chunk.Data.AsMemory(chunk.Offset, count).CopyTo(destination);
            chunk.Offset += count;
            Interlocked.Add(ref _bufferedBytes, -count);
            Interlocked.Add(ref _position, count);
            if (chunk.Offset == chunk.Length)
            {
                _current = null;
                ArrayPool<byte>.Shared.Return(chunk.Data);
            }
            return count;
        }
        catch (Exception)
        {
            // A cancelled read is an end of data the player did not ask for.
            if (!Completed) Interlocked.Exchange(ref _faulted, 1);
            return 0;
        }
        finally
        {
            _readLock.Release();
        }
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

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            // Unblock the pump (body read or full channel) and any waiting read before releasing buffers.
            try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
            _response.Dispose();
            try { _pump.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            // A read in flight ends promptly once cancelled; only recycle buffers no read can still touch.
            if (_readLock.Wait(TimeSpan.FromSeconds(2)))
            {
                while (_chunks.Reader.TryRead(out var chunk)) ArrayPool<byte>.Shared.Return(chunk.Data);
                if (_current is { } current) ArrayPool<byte>.Shared.Return(current.Data);
                _current = null;
                _readLock.Release();
            }
            Interlocked.Exchange(ref _bufferedBytes, 0);
            _cancellation.Dispose();
        }
        base.Dispose(disposing);
    }
}
