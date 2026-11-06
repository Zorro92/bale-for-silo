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

    internal PlaybackMedia(HttpClient http, Uri server, PlaybackRequestScope scope, PlaybackPlan plan, Uri uri)
    {
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
        DurationSeconds = plan.Source?.DurationSeconds;
        Timeline = PlaybackTimeline.From(plan.Timeline);
    }

    public Uri Uri { get; }
    public string? MimeType { get; }
    public string? Container { get; }
    public string? Delivery { get; }
    public bool IsHls { get; }
    public double? DurationSeconds { get; }
    public PlaybackTimeline Timeline { get; }

    public async Task<HttpResponseMessage> SendAsync(Uri uri, long? offset, long? length,
        HttpCompletionOption completion, CancellationToken ct)
    {
        if (uri != Uri && !PlaybackMediaUrl.IsAllowedChild(uri, _server))
            throw new InvalidOperationException("The media request is outside the playback server's media routes.");
        if (offset is < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length is <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
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

    public Task<HttpRangeStream> OpenStreamAsync(CancellationToken ct, int prefetchBlocks = HttpRangeStream.DefaultPrefetchBlocks) =>
        HttpRangeStream.OpenAsync(this, ct, prefetchBlocks);

    internal static void RequireSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("The media request failed.", null, response.StatusCode);
    }
}
