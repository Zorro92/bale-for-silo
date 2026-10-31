using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Bale.Core.Connection;

/// <summary>ApiV2Probe.kt / probeFresh: public, uncached discovery, not an authenticated API or health request.</summary>
internal sealed class ServerDiscoveryProbe : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _address;

    public ServerDiscoveryProbe(Uri server, HttpMessageHandler? transport = null)
    {
        _address = new Uri(ServerAddress.ToBaseUrl(server) + "/api/v2/system/info");
        _http = new HttpClient(transport ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(_address, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return false;
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var info = json.RootElement;
            // A 200 from a proxy, or a different contract, must not dismiss the offline pill.
            // Match all required SystemInfo/SystemInfoLinks fields in ApiV2Models.kt without reflection.
            return info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("api_major", out var major) && major.ValueKind == JsonValueKind.Number
                && major.TryGetInt32(out var version) && version == 2
                && HasString(info, "server_version") && HasString(info, "contract_digest")
                && info.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object
                && HasString(links, "openapi") && HasString(links, "capabilities");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            return false;
        }
    }

    private static bool HasString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String;

    public void Dispose() => _http.Dispose();
}
