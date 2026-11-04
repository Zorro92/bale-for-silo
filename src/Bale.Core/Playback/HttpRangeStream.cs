using System.Buffers;
using System.Net;

namespace Bale.Core.Playback;

/// <summary>
/// A read-only, seekable HTTP body for the platform's random-access stream adapter. Reads stay on one
/// connection where possible; every reopen still passes through PlaybackMedia's captured auth scope.
/// </summary>
public sealed class HttpRangeStream : Stream
{
    private const int ForwardSkipLimit = 256 * 1024;
    private readonly PlaybackMedia _media;
    private readonly long _length;
    private readonly bool _supportsRanges;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private HttpResponseMessage? _response;
    private Stream? _body;
    private long _bodyPosition;
    private long _position;
    private bool _disposed;

    private HttpRangeStream(PlaybackMedia media, HttpResponseMessage response, Stream body, long length, bool supportsRanges)
    {
        _media = media;
        _response = response;
        _body = body;
        _length = length;
        _supportsRanges = supportsRanges;
    }

    internal static async Task<HttpRangeStream> OpenAsync(PlaybackMedia media, CancellationToken ct)
    {
        var response = await media.SendAsync(media.Uri, 0, null, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            PlaybackMedia.RequireSuccess(response);
            var supportsRanges = response.StatusCode == HttpStatusCode.PartialContent;
            long length;
            if (supportsRanges && TryRangeLength(response, 0, out var total)) length = total;
            else if (response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentLength is >= 0 and { } size) length = size;
            else throw new InvalidDataException("The media response has no valid total length.");

            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new(media, response, body, length, supportsRanges);
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
            var forward = _position - _bodyPosition;
            if (_body is null || forward < 0 || forward > ForwardSkipLimit)
                await ReopenAsync(_position, cancellationToken).ConfigureAwait(false);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await SkipToPositionAsync(cancellationToken).ConfigureAwait(false);
                    var count = await _body!.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _length - _position)], cancellationToken)
                        .ConfigureAwait(false);
                    if (count == 0) throw new IOException("The media stream ended before its declared length.");
                    _bodyPosition += count;
                    _position += count;
                    return count;
                }
                catch (Exception error) when (attempt == 0 && error is IOException or HttpRequestException)
                {
                    // A paused body may have lost its connection. Reopen once at the last confirmed
                    // position; a second body failure is the caller's playback-recovery decision.
                    await ReopenAsync(_position, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    private async Task SkipToPositionAsync(CancellationToken ct)
    {
        if (_bodyPosition == _position) return;
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (_bodyPosition < _position)
            {
                var count = await _body!.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, _position - _bodyPosition)), ct)
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

    private async Task ReopenAsync(long position, CancellationToken ct)
    {
        _response?.Dispose();
        _response = null;
        _body = null;
        // A 200 response at open means Range is unsupported. Reopen at zero and let the same
        // skip-reader advance to the target, rather than mistaking a full body for a ranged one.
        var start = _supportsRanges ? position : 0;
        var response = await _media.SendAsync(_media.Uri, start, null, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            var validRange = TryRangeLength(response, start, out var total) && total == _length;
            var validFullBody = !_supportsRanges && response.StatusCode == HttpStatusCode.OK
                && response.Content.Headers.ContentLength == _length;
            if (!validRange && !validFullBody) throw new IOException("The media server returned an invalid range response.");
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            _response = response;
            _body = body;
            _bodyPosition = start;
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
        if (disposing && !_disposed)
        {
            _disposed = true;
            _response?.Dispose();
            _response = null;
            _body = null;
        }
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
