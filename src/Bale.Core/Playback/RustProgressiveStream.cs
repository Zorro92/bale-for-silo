using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Bale.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace Bale.Core.Playback;

/// <summary>
/// <see cref="ProgressiveHttpStream"/>'s forward-only response, pump and 16 MiB read-ahead, implemented in
/// Rust (native/bale-core/src/progressive_stream.rs) on a native thread the .NET garbage collector never
/// pauses. Same contract: reads never throw, and <see cref="Faulted"/> marks an ended transport. One
/// response carries the whole title, so a token expiring later cannot affect it; only the open
/// refreshes on 401.
/// </summary>
public sealed class RustProgressiveStream : MediaProgressiveStream
{
    private readonly ProgressiveHandle _handle;
    /// <summary>HLS only: the playlist, init and segment requests keep their credentials current.</summary>
    private NativeHeaderRefresh? _refresh;
    private volatile bool _disposed;
    /// <summary>The counters at close, so the flags stay readable after the handle is gone.</summary>
    private NativeProgressiveStats _final;

    private RustProgressiveStream(ProgressiveHandle handle) => _handle = handle;

    /// <summary>HLS: the playlist time (sum of EXTINF durations) where the first played segment starts.</summary>
    public double SegmentStartSeconds { get; private init; }

    /// <summary>
    /// Silo HLS as one forward-only stream for FFmpeg (native/bale-core/src/hls.rs): the init segment,
    /// then each segment from the one containing <paramref name="startSeconds"/> (playlist time). A
    /// playlist the reader won't play throws <see cref="HlsUnsupportedException"/>: keep Windows' source.
    /// </summary>
    internal static async Task<RustProgressiveStream> OpenHlsAsync(PlaybackMedia media, double startSeconds, CancellationToken ct)
    {
        var headers = await media.NativeHeadersAsync(ct).ConfigureAwait(false);
        var url = media.Uri.AbsoluteUri;
        var server = media.Server.AbsoluteUri;
        ct.ThrowIfCancellationRequested();
        var (status, handle, start) = await Task.Run(() => OpenHls(url, server, NativeHeaderRefresh.Json(headers), startSeconds), ct)
            .ConfigureAwait(false);
        if (status == Unauthorized && NativeHeaderRefresh.BearerToken(headers) is { } rejected)
        {
            headers = await media.NativeHeadersAsync(ct, rejected).ConfigureAwait(false);
            (status, handle, start) = await Task.Run(() => OpenHls(url, server, NativeHeaderRefresh.Json(headers), startSeconds), ct)
                .ConfigureAwait(false);
        }
        if (status == HlsUnsupported) throw new HlsUnsupportedException();
        ThrowFor(status);
        var stream = new RustProgressiveStream(handle!) { SegmentStartSeconds = start };
        try
        {
            stream.StartRefresh(media, headers);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        if (ct.IsCancellationRequested)
        {
            stream.Dispose();
            ct.ThrowIfCancellationRequested();
        }
        return stream;
    }

    private unsafe void StartRefresh(PlaybackMedia media, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        _refresh = new NativeHeaderRefresh(media, headers, SetHeaders);
        _refresh.Start(context => bale_core_progressive_set_refresh(_handle, NativeHeaderRefresh.Callback, context));
    }

    private unsafe void SetHeaders(byte[] json)
    {
        fixed (byte* pointer = json) bale_core_progressive_set_headers(_handle, pointer, (nuint)json.Length);
    }

    /// <summary>Test hook: the 401 refresh for a header generation (HLS only).</summary>
    internal Task RefreshAfterRejectionAsync(ulong generation) => _refresh!.RefreshAfterRejectionAsync(generation);

    private static unsafe (int Status, ProgressiveHandle? Handle, double Start) OpenHls(string url, string server, byte[] json, double start)
    {
        BaleCoreNative.EnsureLoaded();
        var urlBytes = System.Text.Encoding.UTF8.GetBytes(url);
        var serverBytes = System.Text.Encoding.UTF8.GetBytes(server);
        nint raw;
        double segmentStart;
        int status;
        fixed (byte* urlPointer = urlBytes)
        fixed (byte* serverPointer = serverBytes)
        fixed (byte* jsonPointer = json)
        {
            status = bale_core_hls_open(urlPointer, (nuint)urlBytes.Length, serverPointer, (nuint)serverBytes.Length, jsonPointer,
                (nuint)json.Length, start, &raw, &segmentStart);
        }
        if (status != 0) return (status, null, 0);
        var handle = new ProgressiveHandle();
        Marshal.InitHandle(handle, raw);
        return (0, handle, segmentStart);
    }

    /// <summary>
    /// Opens the stream. A 409 is <see cref="ProgressiveRangeRefusedException"/>, a 401 refreshes and
    /// retries once, any other non-2xx is the usual media HttpRequestException, and a success other than
    /// 200 is invalid data, as for the C# stream.
    /// </summary>
    internal static async Task<RustProgressiveStream> OpenAsync(PlaybackMedia media, CancellationToken mountToken)
    {
        var headers = await media.NativeHeadersAsync(mountToken).ConfigureAwait(false);
        var url = media.Uri.AbsoluteUri;
        mountToken.ThrowIfCancellationRequested();
        // The open blocks until the response headers arrive; keep it off the caller's thread.
        var (status, handle) = await Task.Run(() => Open(url, NativeHeaderRefresh.Json(headers)), mountToken).ConfigureAwait(false);
        if (status == Unauthorized && NativeHeaderRefresh.BearerToken(headers) is { } rejected)
        {
            headers = await media.NativeHeadersAsync(mountToken, rejected).ConfigureAwait(false);
            (status, handle) = await Task.Run(() => Open(url, NativeHeaderRefresh.Json(headers)), mountToken).ConfigureAwait(false);
        }
        ThrowFor(status);
        // This response's connection closes at the next seek: have a spare ready for that open.
        RustTransport.WarmConnection(media.Uri);
        var stream = new RustProgressiveStream(handle!);
        if (mountToken.IsCancellationRequested)
        {
            stream.Dispose();
            mountToken.ThrowIfCancellationRequested();
        }
        return stream;
    }

    private static unsafe (int Status, ProgressiveHandle? Handle) Open(string url, byte[] json)
    {
        BaleCoreNative.EnsureLoaded();
        var urlBytes = System.Text.Encoding.UTF8.GetBytes(url);
        nint raw;
        int status;
        fixed (byte* urlPointer = urlBytes)
        fixed (byte* jsonPointer = json)
        {
            status = bale_core_progressive_open(urlPointer, (nuint)urlBytes.Length, jsonPointer, (nuint)json.Length, &raw);
        }
        if (status != 0) return (status, null);
        var handle = new ProgressiveHandle();
        Marshal.InitHandle(handle, raw);
        return (0, handle);
    }

    private unsafe NativeProgressiveStats Stats
    {
        get
        {
            if (_disposed) return _final;
            NativeProgressiveStats stats = default;
            try { bale_core_progressive_stats(_handle, &stats); }
            catch (ObjectDisposedException) { return _final; }
            return stats;
        }
    }

    public override bool Faulted => Stats.Faulted != 0;
    public override bool Completed => Stats.Completed != 0;
    public override long BufferedBytes => (long)Stats.BufferedBytes;
    public override long DownloadedBytes => (long)Stats.DownloadedBytes;
    public override TimeSpan DownloadTime => TimeSpan.FromMicroseconds(Stats.DownloadUs);

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => (long)Stats.Position;
        set => throw new NotSupportedException();
    }

    /// <summary>The next bytes, blocking this thread; 0 at the end, after a fault, or once disposed. Never throws.</summary>
    public override unsafe int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0 || _disposed) return 0;
        try
        {
            nuint count = 0;
            fixed (byte* pointer = buffer)
            {
                if (bale_core_progressive_read(_handle, pointer, (nuint)buffer.Length, &count) != 0) return 0;
            }
            return (int)count;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>A cancelled wait ends the data like the C# stream; the native read itself finishes on close.</summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0 || _disposed) return 0;
        try
        {
            return await Task.Run(() => Read(buffer.Span), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>The Rust WinRT input over this stream (winrt_stream.rs): reads on the Windows thread pool, no managed code.</summary>
    public override nint CreateNativeInputStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return bale_core_progressive_winrt(_handle);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            // End the data for a blocked read first and keep the final flags; the SafeHandle then
            // frees once every call has returned.
            if (!_handle.IsClosed)
            {
                bale_core_progressive_close(_handle);
                NativeProgressiveStats final = default;
                unsafe { bale_core_progressive_stats(_handle, &final); }
                _final = final;
            }
            // After close returns Rust never calls the refresh callback again.
            _refresh?.Dispose();
            _disposed = true;
            _handle.Dispose();
        }
        base.Dispose(disposing);
    }



    private const int Unauthorized = 7;
    private const int HlsUnsupported = 10;

    // range_stream.rs StreamError; messages are fixed, never transport text.
    private static void ThrowFor(int status)
    {
        switch (status)
        {
            case 0: return;
            case 2: throw new HttpRequestException("The media request failed.");
            case 3: throw new InvalidDataException("The progressive stream did not answer from its origin.");
            case 5: throw new IOException("The media connection failed.");
            case Unauthorized: throw new HttpRequestException("The media server rejected the credentials.", null, HttpStatusCode.Unauthorized);
            case 9: throw new HttpRequestException("The media server stayed busy after retries.", null, HttpStatusCode.ServiceUnavailable);
            case 8: throw new ProgressiveRangeRefusedException();
            default: throw new InvalidOperationException($"bale_core rejected a progressive stream call ({status}).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProgressiveStats
    {
        public ulong DownloadedBytes, DownloadUs, BufferedBytes, Position, Faulted, Completed;
    }

    private sealed class ProgressiveHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle()
        {
            bale_core_progressive_destroy(handle);
            return true;
        }
    }

    private const string Library = "bale_core";
    [DllImport(Library, ExactSpelling = true)] private static extern unsafe int bale_core_progressive_open(byte* url, nuint urlLength, byte* headers, nuint headersLength, nint* handle);
    [DllImport(Library, ExactSpelling = true)] private static extern unsafe int bale_core_progressive_read(ProgressiveHandle handle, byte* buffer, nuint length, nuint* read);
    [DllImport(Library, ExactSpelling = true)] private static extern unsafe int bale_core_progressive_stats(ProgressiveHandle handle, NativeProgressiveStats* stats);
    [DllImport(Library, ExactSpelling = true)] private static extern void bale_core_progressive_close(ProgressiveHandle handle);
    [DllImport(Library, ExactSpelling = true)] private static extern void bale_core_progressive_destroy(nint handle);
    [DllImport(Library, ExactSpelling = true)] private static extern unsafe int bale_core_hls_open(byte* url, nuint urlLength, byte* server, nuint serverLength, byte* headers, nuint headersLength, double startSeconds, nint* handle, double* segmentStart);
    [DllImport(Library, ExactSpelling = true)] private static extern unsafe int bale_core_progressive_set_headers(ProgressiveHandle handle, byte* headers, nuint length);
    [DllImport(Library, ExactSpelling = true)] private static extern unsafe int bale_core_progressive_set_refresh(ProgressiveHandle handle, delegate* unmanaged[Cdecl]<nint, ulong, void> callback, nint context);
    [DllImport(Library, ExactSpelling = true)] private static extern nint bale_core_progressive_winrt(ProgressiveHandle handle);
}

/// <summary>An HLS playlist the Rust reader won't play (hls.rs): keep Windows' own HLS source.</summary>
public sealed class HlsUnsupportedException() : NotSupportedException("The HLS playlist needs Windows' own HLS source.");
