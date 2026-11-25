using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Bale.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace Bale.Core.Playback;

/// <summary>
/// <see cref="HttpRangeStream"/>'s blocks, cache, prefetch and sequential fallback, implemented in Rust
/// (native/bale-core/src/range_stream.rs) on native threads the .NET garbage collector never pauses.
/// Same counters as the C# stream. Reads block their thread; use them from the player's read thread.
/// Credentials stay current two ways, matching the C# transport's handler: a minute timer hands over
/// headers the session refreshed ahead of expiry, and a 401 makes Rust ask for a forced refresh
/// (<see cref="RefreshAfterRejectionAsync"/>) and retry once.
/// </summary>
public sealed class RustRangeStream : MediaRangeStream
{
    private readonly RangeStreamHandle _handle;
    private readonly long _length;
    private readonly NativeHeaderRefresh _refresh;
    private long _position;
    private volatile bool _disposed;

    private RustRangeStream(RangeStreamHandle handle, long length, bool supportsRanges, int prefetchBlocks, PlaybackMedia media,
        IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        _handle = handle;
        _length = length;
        _refresh = new NativeHeaderRefresh(media, headers, SetHeaders);
        SupportsRanges = supportsRanges;
        PrefetchBlocks = prefetchBlocks;
    }

    internal static async Task<RustRangeStream> OpenAsync(PlaybackMedia media, CancellationToken ct, int prefetchBlocks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(prefetchBlocks, HttpRangeStream.DefaultPrefetchBlocks);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefetchBlocks, HttpRangeStream.MaximumPrefetchBlocks);
        var headers = await media.NativeHeadersAsync(ct).ConfigureAwait(false);
        var url = media.Uri.AbsoluteUri;
        ct.ThrowIfCancellationRequested();
        // The open request blocks; keep it off the caller's thread. No callback exists yet, so a 401
        // here is refreshed and retried once from this side.
        var (status, opened) = await Task.Run(() => Open(url, NativeHeaderRefresh.Json(headers), prefetchBlocks), ct).ConfigureAwait(false);
        if (status == Unauthorized && NativeHeaderRefresh.BearerToken(headers) is { } rejected)
        {
            headers = await media.NativeHeadersAsync(ct, rejected).ConfigureAwait(false);
            (status, opened) = await Task.Run(() => Open(url, NativeHeaderRefresh.Json(headers), prefetchBlocks), ct).ConfigureAwait(false);
        }
        ThrowFor(status);
        var stream = new RustRangeStream(opened!.Value.Handle, opened.Value.Length, opened.Value.SupportsRanges, prefetchBlocks,
            media, headers);
        stream.StartHeaderRefresh();
        return stream;
    }

    private static unsafe (int Status, (RangeStreamHandle Handle, long Length, bool SupportsRanges)? Opened) Open(string url,
        byte[] json, int prefetchBlocks)
    {
        BaleCoreNative.EnsureLoaded();
        var urlBytes = System.Text.Encoding.UTF8.GetBytes(url);
        nint raw;
        ulong length;
        byte supports;
        int status;
        fixed (byte* urlPointer = urlBytes)
        fixed (byte* jsonPointer = json)
        {
            status = bale_core_range_open(urlPointer, (nuint)urlBytes.Length, jsonPointer, (nuint)json.Length, (uint)prefetchBlocks,
                &raw, &length, &supports);
        }
        if (status != 0) return (status, null);
        var handle = new RangeStreamHandle();
        Marshal.InitHandle(handle, raw);
        return (0, (handle, checked((long)length), supports != 0));
    }

    private unsafe void StartHeaderRefresh() =>
        _refresh.Start(context => bale_core_range_set_refresh(_handle, NativeHeaderRefresh.Callback, context));

    /// <summary>Test hooks: the timer's check, and Rust's 401 callback for a header generation.</summary>
    internal Task CheckHeadersAsync(CancellationToken ct) => _refresh.CheckAsync(ct);
    internal Task RefreshAfterRejectionAsync(ulong generation) => _refresh.RefreshAfterRejectionAsync(generation);

    public override int PrefetchBlocks { get; }
    public override bool SupportsRanges { get; }

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

    public override unsafe int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        nuint count;
        int status;
        fixed (byte* pointer = buffer)
        {
            status = bale_core_range_read(_handle, (ulong)_position, pointer, (nuint)buffer.Length, &count);
        }
        ThrowFor(status);
        _position += (long)count;
        return (int)count;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(Task.Run(() => Read(buffer.Span), cancellationToken));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

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

    /// <summary>Replaces the request headers; later block requests use them. Only through <see cref="_refresh"/>.</summary>
    private unsafe void SetHeaders(byte[] json)
    {
        fixed (byte* pointer = json) bale_core_range_set_headers(_handle, pointer, (nuint)json.Length);
    }

    private unsafe NativeRangeStats Stats
    {
        get
        {
            NativeRangeStats stats = default;
            if (!_handle.IsClosed) bale_core_range_stats(_handle, &stats);
            return stats;
        }
    }

    // HttpRangeStream's diagnostics, with the same meanings.
    public override long DemandWaits => (long)Stats.DemandWaits;
    public override TimeSpan DemandWaitTime => TimeSpan.FromMicroseconds(Stats.DemandWaitUs);
    public override TimeSpan MaxDemandWaitTime => TimeSpan.FromMicroseconds(Stats.MaxDemandWaitUs);
    public override long DownloadedBytes => (long)Stats.DownloadedBytes;
    public override TimeSpan DownloadTime => TimeSpan.FromMicroseconds(Stats.DownloadUs);
    public override long BlockAllocations => (long)Stats.BlockAllocations;
    public override long BlockReuses => (long)Stats.BlockReuses;
    public override long Reads => (long)Stats.Reads;
    public override long BlockFetches => (long)Stats.BlockFetches;
    public override long CacheHits => (long)Stats.CacheHits;
    public override long PrefetchHits => (long)Stats.PrefetchHits;
    public override long BytesRead => (long)Stats.BytesRead;
    public override bool LastReadFailed => !_handle.IsClosed && bale_core_range_last_read_failed(_handle) == 1;
    public override long SmallestRead => Stats.SmallestRead == ulong.MaxValue ? long.MaxValue : (long)Stats.SmallestRead;

    /// <summary>The Rust WinRT stream over this one (winrt_stream.rs): reads on the Windows thread pool, no managed code.</summary>
    public override nint CreateNativeRandomAccessStream()
    {
        ThrowIfDisposed();
        return bale_core_range_winrt(_handle);
    }

    public override void Flush() => ThrowIfDisposed();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            // Wake reads blocked on a fetch first; the SafeHandle then frees once every call has returned.
            // After close returns Rust never calls the refresh callback again, so the GCHandle can go.
            if (!_handle.IsClosed) bale_core_range_close(_handle);
            _refresh.Dispose();
            _handle.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);


    private const int Unauthorized = 7;

    // range_stream.rs StreamError; messages are fixed, never transport text.
    private static void ThrowFor(int status)
    {
        switch (status)
        {
            case 0: return;
            case 1: throw new ObjectDisposedException(nameof(RustRangeStream));
            case 2: throw new HttpRequestException("The media request failed.");
            case 3: throw new IOException("The media server returned an invalid range response.");
            case 4: throw new EndOfStreamException("The media block ended before its declared length.");
            case 5: throw new IOException("The media connection failed.");
            case 6: throw new InvalidDataException("The media response has no valid total length.");
            case Unauthorized: throw new HttpRequestException("The media server rejected the credentials.", null, HttpStatusCode.Unauthorized);
            case 9: throw new HttpRequestException("The media server stayed busy after retries.", null, HttpStatusCode.ServiceUnavailable);
            default: throw new InvalidOperationException($"bale_core rejected a range stream call ({status}).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRangeStats
    {
        public ulong Reads, BytesRead, SmallestRead, BlockFetches, CacheHits, PrefetchHits, DemandWaits, DemandWaitUs,
            MaxDemandWaitUs, DownloadedBytes, DownloadUs, BlockAllocations, BlockReuses;
    }

    private sealed class RangeStreamHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle()
        {
            bale_core_range_destroy(handle);
            return true;
        }
    }

    private const string Library = "bale_core";
    [DllImport(Library, ExactSpelling = true)] private static extern unsafe int bale_core_range_open(byte* url, nuint urlLength, byte* headers, nuint headersLength, uint prefetchBlocks, nint* handle, ulong* length, byte* supportsRanges);
    [DllImport(Library, ExactSpelling = true)] private static extern unsafe int bale_core_range_read(RangeStreamHandle handle, ulong position, byte* buffer, nuint length, nuint* read);
    [DllImport(Library, ExactSpelling = true)] private static extern unsafe int bale_core_range_stats(RangeStreamHandle handle, NativeRangeStats* stats);
    [DllImport(Library, ExactSpelling = true)] private static extern int bale_core_range_last_read_failed(RangeStreamHandle handle);
    [DllImport(Library, ExactSpelling = true)] private static extern unsafe int bale_core_range_set_headers(RangeStreamHandle handle, byte* headers, nuint length);
    [DllImport(Library, ExactSpelling = true)] private static extern unsafe int bale_core_range_set_refresh(RangeStreamHandle handle, delegate* unmanaged[Cdecl]<nint, ulong, void> callback, nint context);
    [DllImport(Library, ExactSpelling = true)] private static extern void bale_core_range_close(RangeStreamHandle handle);
    [DllImport(Library, ExactSpelling = true)] private static extern void bale_core_range_destroy(nint handle);
    [DllImport(Library, ExactSpelling = true)] private static extern nint bale_core_range_winrt(RangeStreamHandle handle);
}
