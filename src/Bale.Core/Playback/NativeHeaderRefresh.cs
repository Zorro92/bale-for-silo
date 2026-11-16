using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Bale.Core.Playback;

/// <summary>
/// Keeps a Rust media stream's credentials current, matching the C# transport's handler: a minute timer
/// hands over headers the session refreshed ahead of expiry, and a 401 makes Rust ask (through
/// <see cref="Callback"/>) for a forced refresh with the rejected bearer, then retry once. Shared by the
/// range stream and the HLS stream (docs/rust-port.md, slice 7). Credentials: never log them.
/// </summary>
internal sealed class NativeHeaderRefresh : IDisposable
{
    private static readonly TimeSpan HeaderCheckInterval = TimeSpan.FromMinutes(1);
    private readonly PlaybackMedia _media;
    private readonly Action<byte[]> _setHeaders;
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>Serializes header hand-overs so <see cref="_generation"/> matches Rust's count.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GCHandle _self;
    private IReadOnlyList<KeyValuePair<string, string>> _headers;
    /// <summary>How many times the headers were replaced: Rust's generation for <see cref="_headers"/>.</summary>
    private ulong _generation;
    private volatile bool _disposed;

    /// <param name="setHeaders">Hands Rust the headers JSON; throws once the stream is closed.</param>
    internal NativeHeaderRefresh(PlaybackMedia media, IReadOnlyList<KeyValuePair<string, string>> headers, Action<byte[]> setHeaders)
    {
        _media = media;
        _headers = headers;
        _setHeaders = setHeaders;
    }

    /// <summary>Rust's 401 callback, for the stream's set_refresh export.</summary>
    internal static unsafe delegate* unmanaged[Cdecl]<nint, ulong, void> Callback => &OnRefreshRequested;

    /// <summary>
    /// Installs the callback with the stream's export (given this object's context) and starts the timer.
    /// After the stream's close returns Rust never calls back, so <see cref="Dispose"/> may then free it.
    /// </summary>
    internal void Start(Func<nint, int> install)
    {
        // Weak: an undisposed stream may still be collected; its callback then finds no target.
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        if (install(GCHandle.ToIntPtr(_self)) != 0)
            throw new InvalidOperationException("bale_core rejected the refresh callback.");
        _ = CheckPeriodicallyAsync(_lifetime.Token);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRefreshRequested(nint context, ulong generation)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is NativeHeaderRefresh refresh)
                _ = Task.Run(() => refresh.RefreshAfterRejectionAsync(generation));
        }
        catch
        {
            // Never unwind into Rust; the waiting request times out as Unauthorized.
        }
    }

    /// <summary>
    /// The headers of <paramref name="generation"/> got a 401: refresh the session unless that bearer
    /// already rotated, and hand over the result. On failure the old headers go back, so Rust's retry
    /// meets the 401 again and reports it now rather than after the wait.
    /// </summary>
    internal async Task RefreshAfterRejectionAsync(ulong generation)
    {
        try
        {
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (_disposed || generation != _generation) return;
            IReadOnlyList<KeyValuePair<string, string>> headers;
            try
            {
                headers = await _media.NativeHeadersAsync(_lifetime.Token, BearerToken(_headers)).ConfigureAwait(false);
            }
            catch (Exception) when (!_disposed)
            {
                headers = _headers;
            }
            Replace(headers);
        }
        catch (Exception)
        {
            // Disposed meanwhile: nothing waits for these headers.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The session refreshes two minutes before expiry; pick that up without waiting for a 401.</summary>
    private async Task CheckPeriodicallyAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeaderCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) await CheckAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var headers = await _media.NativeHeadersAsync(ct).ConfigureAwait(false);
            if (!_disposed && BearerToken(headers) != BearerToken(_headers)) Replace(headers);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // An outage or a changed profile: keep the current headers; a 401 still refreshes.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Under <see cref="_gate"/>.</summary>
    private void Replace(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        _setHeaders(Json(headers));
        _headers = headers;
        _generation++;
    }

    /// <summary>Stops the timer and pending refreshes. Call after the stream's close, which ends callbacks.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_self.IsAllocated) _self.Free();
    }

    internal static string? BearerToken(IReadOnlyList<KeyValuePair<string, string>> headers) =>
        headers.FirstOrDefault(header => header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).Value is { } value
        && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : null;

    internal static byte[] Json(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var (name, value) in headers)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(name);
                writer.WriteStringValue(value);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
