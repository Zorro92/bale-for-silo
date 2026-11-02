using System.Text.Json;
using Bale.Core.Connection;

namespace Bale.Core.Auth;

/// <summary>ServerBranding.kt: optional public branding, with same-origin assets resolved under the server path.</summary>
public sealed record ServerBranding(string? ServerName = null, Uri? MarkUrl = null)
{
    public static Uri? ResolveAsset(Uri server, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var raw = value.Trim();
        var root = new Uri(ServerAddress.ToBaseUrl(server) + "/");
        // ServerBranding.kt removes a leading slash: a reverse proxy's base path still owns assets.
        if (!Uri.TryCreate(root, raw.StartsWith('/') ? raw[1..] : raw, out var asset)
            || asset.Scheme is not ("http" or "https")
            || !string.Equals(asset.Scheme, root.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(asset.Host, root.Host, StringComparison.OrdinalIgnoreCase)
            || asset.Port != root.Port || asset.UserInfo.Length != 0)
        {
            return null;
        }
        return asset;
    }

    /// <summary>
    /// ServerBranding.kt / BrandingApi.kt: public branding needs no bearer, including after expiry.
    /// Its six-second timeout and optional result must never gate entering the sign-in screen.
    /// </summary>
    public static async Task<ServerBranding?> FetchAsync(Uri server, CancellationToken cancellationToken = default,
        HttpMessageHandler? transport = null)
    {
        using var http = new HttpClient(transport ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(6) };
        try
        {
            using var response = await http.GetAsync(ServerAddress.ToBaseUrl(server) + "/api/v2/theme/branding", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            return new ServerBranding(ReadText(json.RootElement, "server_name"),
                ResolveAsset(server, ReadText(json.RootElement, "mark_url")));
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

    private static string? ReadText(JsonElement json, string key) =>
        json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;
}
