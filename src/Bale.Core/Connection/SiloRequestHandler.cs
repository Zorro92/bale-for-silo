using System.Net.Http.Headers;

namespace Bale.Core.Connection;

/// <summary>
/// Stamps the Silo client headers and, when signed in, the bearer token onto every request.
/// Header names match Silo's official clients.
/// </summary>
internal sealed class SiloRequestHandler : DelegatingHandler
{
    private readonly ClientIdentity _identity;
    private readonly Func<string?>? _accessToken;

    public SiloRequestHandler(ClientIdentity identity, Func<string?>? accessToken, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _identity = identity;
        _accessToken = accessToken;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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

        if (headers.Authorization is null && _accessToken?.Invoke() is { Length: > 0 } token)
        {
            headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }

    private static void AddIfPresent(HttpRequestHeaders headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            headers.TryAddWithoutValidation(name, value);
        }
    }
}
