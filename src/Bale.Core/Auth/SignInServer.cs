using System.Text.Json;
using Bale.Core.Connection;

namespace Bale.Core.Auth;

/// <summary>SignInServer.kt: the public server name, then the saved name, then its host and port.</summary>
public sealed record SignInServer(Uri Server, string? SavedName = null)
{
    public string Name => string.IsNullOrWhiteSpace(SavedName) ? Address : SavedName.Trim();

    public string Address => Server.Authority;

    public bool IsSecure => Server.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// ServerBranding.kt / BrandingApi.kt: public branding needs no bearer, including after expiry.
    /// Its six-second timeout and optional result must never gate entering the sign-in screen.
    /// </summary>
    public static async Task<string?> FetchNameAsync(Uri server, CancellationToken cancellationToken = default,
        HttpMessageHandler? transport = null)
    {
        using var http = new HttpClient(transport ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(6) };
        try
        {
            using var response = await http.GetAsync(ServerAddress.ToBaseUrl(server) + "/api/v2/theme/branding", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("server_name", out var name) && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString()) ? name.GetString()!.Trim() : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            return null;
        }
    }
}
