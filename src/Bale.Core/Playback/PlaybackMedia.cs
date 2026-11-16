using System.Net.Http.Headers;
using Bale.Core.Api.Models;
using Bale.Core.Connection;

namespace Bale.Core.Playback;

/// <summary>
/// Authenticated media transport for a captured playback owner. The platform player receives bytes,
/// never bearer credentials or a URL it could fetch without the profile/refresh handler.
/// </summary>
public sealed class PlaybackMedia
{
    private readonly HttpClient _http;
    private readonly Uri _server;
    private readonly PlaybackRequestScope _scope;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly HeaderCapture? _captureHeaders;

    /// <summary>The client's request headers for a URL and profile, without sending (SiloClient.CaptureHeadersAsync).</summary>
    internal delegate Task<IReadOnlyList<KeyValuePair<string, string>>> HeaderCapture(Uri uri, ProfileRequestOption profile,
        string? rejectedAccessToken, CancellationToken cancellationToken);

    internal PlaybackMedia(HttpClient http, Uri server, PlaybackRequestScope scope, PlaybackPlan plan, Uri uri,
        HeaderCapture? captureHeaders = null)
    {
        _captureHeaders = captureHeaders;
        _http = http;
        _server = server;
        _scope = scope;
        _headers = plan.Stream?.Headers?.AdditionalData
            .Where(header => header.Value is string && !header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key, header => (string)header.Value) ?? [];
        Uri = uri;
        MimeType = plan.Stream?.MimeType;
        Container = plan.Stream?.Container;
        Delivery = plan.Delivery;
        IsHls = string.Equals(plan.Stream?.Protocol, "hls", StringComparison.OrdinalIgnoreCase)
            || Delivery?.EndsWith("_hls", StringComparison.OrdinalIgnoreCase) == true
            || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        IsProgressive = !IsHls && PlaybackProgressive.IsProgressive(plan);
        DurationSeconds = plan.Source?.DurationSeconds;
        Timeline = PlaybackTimeline.From(plan.Timeline);
    }

    public Uri Uri { get; }

    /// <summary>The playback server: HLS children must stay on its media routes.</summary>
    internal Uri Server => _server;

    /// <summary>Debug only (the Rust HTTP spike): the plan's stream headers this transport adds. Never log them.</summary>
    public IReadOnlyDictionary<string, string> DebugStreamHeaders => _headers;
    public string? MimeType { get; }
    public string? Container { get; }
    public string? Delivery { get; }
    public bool IsHls { get; }
    /// <summary>A forward-only server_remux_progressive response: open it with <see cref="OpenProgressiveAsync"/>.</summary>
    public bool IsProgressive { get; }
    public double? DurationSeconds { get; }
    public PlaybackTimeline Timeline { get; }

    public Task<HttpResponseMessage> SendAsync(Uri uri, long? offset, long? length,
        HttpCompletionOption completion, CancellationToken ct) => SendAsync(uri, offset, length, completion, streamHeaders: true, ct);

    private async Task<HttpResponseMessage> SendAsync(Uri uri, long? offset, long? length,
        HttpCompletionOption completion, bool streamHeaders, CancellationToken ct)
    {
        if (uri != Uri && !PlaybackMediaUrl.IsAllowedChild(uri, _server))
            throw new InvalidOperationException("The media request is outside the playback server's media routes.");
        if (offset is < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length is <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (streamHeaders)
            foreach (var header in _headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (offset is { } start)
            request.Headers.Range = new RangeHeaderValue(start, length is { } count ? checked(start + (count - 1)) : null);
        // Kiota 2.1.2 HttpRequestMessageExtensions.GetRequestOption<T> uses the type's FullName.
        request.Options.Set(new HttpRequestOptionsKey<ProfileRequestOption>(typeof(ProfileRequestOption).FullName!), _scope.Request);
        _scope.Request.CheckCurrent();
        return await _http.SendAsync(request, completion, ct).ConfigureAwait(false);
    }

    public async Task<(byte[] Data, string? ContentType)> GetBytesAsync(Uri uri, long? offset, long? length, CancellationToken ct)
    {
        using var response = await SendAsync(uri, offset, length, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        RequireSuccess(response);
        return (await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// The plan's subtitle sidecar as UTF-8 text bytes, fetched as this plan's credential owner. The
    /// stream's own plan headers are for the media URL only and are not sent.
    /// </summary>
    public async Task<byte[]> GetSubtitleAsync(PlaybackSubtitleSource source, CancellationToken ct)
    {
        if (source.IsBitmap) throw new ArgumentException("PGS requires a binary window fetch.", nameof(source));
        return PlaybackSubtitleText.ToUtf8(await GetCappedAsync(source.Uri, PlaybackSubtitleText.MaxBytes, ct).ConfigureAwait(false));
    }

    /// <summary>A bounded PGS window. Bytes are binary: never run through subtitle text conversion.</summary>
    public Task<byte[]> GetPgsSubtitlesAsync(PlaybackSubtitleSource source, PgsSubtitleRange range, CancellationToken ct)
    {
        if (!source.IsBitmap) throw new ArgumentException("A PGS source is required.", nameof(source));
        return GetCappedAsync(range.RequestUri(source.Uri), PgsSubtitleRange.MaxBytes, ct);
    }

    /// <summary>The embedded fonts of the plan's ASS/SSA track; empty when the plan names no bundle.</summary>
    public async Task<IReadOnlyList<SubtitleFont>> GetSubtitleFontsAsync(PlaybackSubtitleSource source, CancellationToken ct)
    {
        if (source.FontBundle is not { } bundle) return [];
        return SubtitleFontBundle.Parse(await GetCappedAsync(bundle, SubtitleFontBundle.MaxResponseBytes, ct).ConfigureAwait(false));
    }

    private async Task<byte[]> GetCappedAsync(Uri uri, int maxBytes, CancellationToken ct)
    {
        using var response = await SendAsync(uri, null, null, HttpCompletionOption.ResponseHeadersRead, streamHeaders: false, ct)
            .ConfigureAwait(false);
        RequireSuccess(response);
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("The subtitle response is too large.");
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidDataException("The subtitle response is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Every header a media request carries except Range: the client's (bearer token, profile proof,
    /// device) for this media's captured profile, then the plan's stream headers. For the Rust
    /// transport, which sends them itself. Credentials: never log them. <paramref name="rejectedAccessToken"/>:
    /// the bearer a 401 rejected, refreshed first (once) as the C# transport's handler does.
    /// </summary>
    internal async Task<IReadOnlyList<KeyValuePair<string, string>>> NativeHeadersAsync(CancellationToken ct,
        string? rejectedAccessToken = null)
    {
        var capture = _captureHeaders ?? throw new InvalidOperationException("This client cannot hand headers to the native transport.");
        _scope.Request.CheckCurrent();
        var client = await capture(Uri, _scope.Request, rejectedAccessToken, ct).ConfigureAwait(false);
        return client.Concat(_headers).ToArray();
    }

    /// <summary>The Rust range stream (docs/rust-port.md, slice 7): the same blocks, cache and prefetch, on native threads.</summary>
    public Task<RustRangeStream> OpenNativeStreamAsync(CancellationToken ct, int prefetchBlocks = HttpRangeStream.DefaultPrefetchBlocks) =>
        RustRangeStream.OpenAsync(this, ct, prefetchBlocks);

    public Task<HttpRangeStream> OpenStreamAsync(CancellationToken ct, int prefetchBlocks = HttpRangeStream.DefaultPrefetchBlocks) =>
        HttpRangeStream.OpenAsync(this, ct, prefetchBlocks);

    /// <summary>The Rust forward-only remux (docs/rust-port.md, slice 7 step 5): the same pump and buffer on a native thread.</summary>
    public Task<RustProgressiveStream> OpenNativeProgressiveAsync(CancellationToken mountToken)
    {
        if (!IsProgressive) throw new InvalidOperationException("The plan is not a progressive remux.");
        return RustProgressiveStream.OpenAsync(this, mountToken);
    }

    /// <summary>
    /// HLS through FFmpeg (docs/rust-port.md): the Rust reader's forward-only stream from the segment
    /// containing <paramref name="playerStartSeconds"/>; its <see cref="RustProgressiveStream.SegmentStartSeconds"/>
    /// is where the mount's player clock starts.
    /// </summary>
    public Task<RustProgressiveStream> OpenNativeHlsAsync(double playerStartSeconds, CancellationToken mountToken)
    {
        if (!IsHls) throw new InvalidOperationException("The plan is not HLS.");
        return RustProgressiveStream.OpenHlsAsync(this, playerStartSeconds, mountToken);
    }

    /// <summary>The forward-only remux from its origin; <paramref name="mountToken"/> also ends its read-ahead.</summary>
    public Task<ProgressiveHttpStream> OpenProgressiveAsync(CancellationToken mountToken)
    {
        if (!IsProgressive) throw new InvalidOperationException("The plan is not a progressive remux.");
        return ProgressiveHttpStream.OpenAsync(this, mountToken);
    }

    internal static void RequireSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("The media request failed.", null, response.StatusCode);
    }
}
