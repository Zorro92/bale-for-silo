using Bale.Core.Api;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace Bale.Core.Connection;

/// <summary>A typed Silo API client bound to one server and one client identity.</summary>
public sealed class SiloConnection : IDisposable
{
    private readonly HttpClient _http;

    /// <param name="server">Base address from <see cref="ServerAddress.TryParse"/>.</param>
    /// <param name="identity">This install's identity headers.</param>
    /// <param name="credentials">Returns the current session credentials, or null when signed out.</param>
    /// <param name="transport">Override the network handler; tests pass a stub here.</param>
    public SiloConnection(Uri server, ClientIdentity identity, Func<CancellationToken, Task<RequestCredentials?>>? credentials = null, HttpMessageHandler? transport = null)
    {
        Server = server;
        var siloHandler = new SiloRequestHandler(identity, credentials, transport ?? new HttpClientHandler());
        _http = KiotaClientFactory.Create(siloHandler);
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: _http)
        {
            BaseUrl = ServerAddress.ToBaseUrl(server),
        };
        Api = new SiloApiClient(adapter);
    }

    public Uri Server { get; }

    public SiloApiClient Api { get; }

    public void Dispose() => _http.Dispose();
}
