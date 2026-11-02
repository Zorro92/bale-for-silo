using System.Net;
using System.Net.Http.Headers;

namespace Bale.Core.Connection;

/// <summary>
/// Stamps the Silo client headers and, when signed in, the bearer token and acting profile onto every request.
/// Header names match Silo's official clients.
/// </summary>
internal sealed class SiloRequestHandler : DelegatingHandler
{
    private readonly ClientIdentity _identity;
    private readonly Func<CancellationToken, Task<RequestCredentials?>>? _credentials;
    private readonly Func<string, CancellationToken, Task<RequestCredentials?>>? _refreshRejected;

    public SiloRequestHandler(ClientIdentity identity, Func<CancellationToken, Task<RequestCredentials?>>? credentials, HttpMessageHandler innerHandler,
        Func<string, CancellationToken, Task<RequestCredentials?>>? refreshRejected = null)
        : base(innerHandler)
    {
        _identity = identity;
        _credentials = credentials;
        _refreshRejected = refreshRejected;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers;
        headers.TryAddWithoutValidation("X-Silo-Device-Id", _identity.DeviceId);
        headers.TryAddWithoutValidation("X-Silo-Device-Name", _identity.DeviceName);
        headers.TryAddWithoutValidation("X-Silo-Device-Platform", _identity.Platform);
        headers.TryAddWithoutValidation("X-Silo-Client", _identity.ClientName);
        AddIfPresent(headers, "X-Silo-Client-Version", _identity.ClientVersion);
        AddIfPresent(headers, "X-Silo-Client-Build", _identity.ClientBuild);
        AddIfPresent(headers, "X-Silo-Client-Channel", _identity.ClientChannel);
        AddIfPresent(headers, "X-Silo-Client-Family", _identity.ClientFamily);

        // Kiota asks only for application/problem+json on calls that answer 204 with no body (watchlist,
        // favorites, watched). Silo refuses that with 406, so also accept application/json.
        if (headers.Accept.Count == 1 && headers.Accept.First().MediaType == "application/problem+json")
        {
            headers.Accept.ParseAdd("application/json");
        }

        // Refresh must not wait on itself, and re-pairing must still work after a refresh 401.
        RequestCredentials? sent = null;
        if (headers.Authorization is null && _credentials is not null && !IsAnonymousAuth(request)
            && await _credentials(cancellationToken).ConfigureAwait(false) is { AccessToken.Length: > 0 } credentials)
        {
            sent = credentials;
            SetCredentials(headers, credentials);
        }

        // AuthInterceptorImpl.kt SiloAuthPlugin: any authenticated endpoint may discover a revoked
        // bearer. Refresh once and retry once; only a rejected refresh invalidates the session.
        // Buffer before sending so even a JSON POST's consumed stream can be retried faithfully.
        var body = sent is not null && _refreshRejected is not null && request.Content is not null
            ? await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false) : null;
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized || sent is null || _refreshRejected is null)
        {
            return response;
        }

        RequestCredentials? refreshed;
        try
        {
            refreshed = await _refreshRejected(sent.AccessToken, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
        if (refreshed is null) return response;
        response.Dispose();

        using var retry = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };
        foreach (var header in headers) retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) retry.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        retry.Headers.Remove("Authorization");
        retry.Headers.Remove("X-Profile-Id");
        retry.Headers.Remove("X-Profile-Token");
        SetCredentials(retry.Headers, refreshed);
        if (body is not null)
        {
            retry.Content = new ByteArrayContent(body);
            foreach (var header in request.Content!.Headers) retry.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Call the transport directly, not this handler again: a second 401 remains an API error,
        // never a refresh loop or a page's guess that otherwise valid credentials must be deleted.
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAnonymousAuth(HttpRequestMessage request) =>
        request.RequestUri?.AbsolutePath is { } path
        && (path.EndsWith("/api/v2/auth/refresh", StringComparison.Ordinal)
            || path.Contains("/api/v2/auth/device/", StringComparison.Ordinal));

    private static void SetCredentials(HttpRequestHeaders headers, RequestCredentials credentials)
    {
        headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        AddIfPresent(headers, "X-Profile-Id", credentials.ProfileId);
        AddIfPresent(headers, "X-Profile-Token", credentials.ProfileToken);
    }

    private static void AddIfPresent(HttpRequestHeaders headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            headers.TryAddWithoutValidation(name, value);
        }
    }
}
