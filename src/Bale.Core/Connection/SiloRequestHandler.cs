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

    public SiloRequestHandler(ClientIdentity identity, Func<CancellationToken, Task<RequestCredentials?>>? credentials, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _identity = identity;
        _credentials = credentials;
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

        // The refresh call itself must not wait on credentials: getting them may be what triggered it.
        if (headers.Authorization is null && _credentials is not null && !IsRefresh(request)
            && await _credentials(cancellationToken).ConfigureAwait(false) is { AccessToken.Length: > 0 } credentials)
        {
            headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            AddIfPresent(headers, "X-Profile-Id", credentials.ProfileId);
            AddIfPresent(headers, "X-Profile-Token", credentials.ProfileToken);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsRefresh(HttpRequestMessage request) =>
        request.RequestUri?.AbsolutePath.EndsWith("/api/v2/auth/refresh", StringComparison.Ordinal) == true;

    private static void AddIfPresent(HttpRequestHeaders headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            headers.TryAddWithoutValidation(name, value);
        }
    }
}
