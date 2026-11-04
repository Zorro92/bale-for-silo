using System.Globalization;

namespace Bale.Core.Playback;

/// <summary>
/// StreamRequest.swift's header-authenticated API-local media boundary. Bale never negotiates
/// authorized_media_origins_v1, so even same-origin absolute plan URLs are refused.
/// </summary>
public static class PlaybackMediaUrl
{
    public static Uri? Resolve(string? raw, Uri server)
    {
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw) || !IsServer(server) || !raw.StartsWith('/')
            || raw.StartsWith("//", StringComparison.Ordinal) || raw.Contains("://", StringComparison.Ordinal)
            || raw.Contains('#')) return null;

        var queryStart = raw.IndexOf('?');
        var path = queryStart < 0 ? raw : raw[..queryStart];
        if (!IsMediaPath(path, "") || !IsSafePath(path)
            || (queryStart >= 0 && !IsPlanQuery(raw[(queryStart + 1)..]))) return null;

        return Uri.TryCreate(server.ToString().TrimEnd('/') + raw, UriKind.Absolute, out var uri)
            && IsAllowedChild(uri, server) ? uri : null;
    }

    /// <summary>HLS children keep the same origin and route boundary, but may carry server-minted queries.</summary>
    public static bool IsAllowedChild(Uri uri, Uri server) =>
        IsServer(server) && uri.IsAbsoluteUri && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && string.Equals(uri.Scheme, server.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, server.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == server.Port
        && IsSafePath(OriginalPath(uri))
        && IsMediaPath(uri.AbsolutePath, server.AbsolutePath.TrimEnd('/'));

    private static bool IsServer(Uri server) =>
        server.IsAbsoluteUri && (server.Scheme == Uri.UriSchemeHttp || server.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(server.Host) && string.IsNullOrEmpty(server.UserInfo)
        && string.IsNullOrEmpty(server.Query) && string.IsNullOrEmpty(server.Fragment)
        && IsSafePath(OriginalPath(server));

    private static bool IsMediaPath(string path, string prefix) =>
        path.StartsWith(prefix + "/api/v2/stream/", StringComparison.Ordinal)
        || path.StartsWith(prefix + "/api/v2/playback/transcode/", StringComparison.Ordinal);

    private static bool IsSafePath(string path)
    {
        if (path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%5c", StringComparison.OrdinalIgnoreCase) || path.Contains('\\')) return false;
        return path.Split('/').All(segment => Uri.UnescapeDataString(segment) is not ("." or ".."));
    }

    // Uri normalizes dot segments and backslashes. Inspect the original path as well, before that
    // normalization can disguise a server-supplied traversal as an allowed media route.
    private static string OriginalPath(Uri uri)
    {
        var raw = uri.OriginalString;
        var authorityStart = raw.IndexOf("://", StringComparison.Ordinal) + 3;
        var pathStart = raw.IndexOfAny(['/', '\\', '?', '#'], authorityStart);
        if (pathStart < 0 || raw[pathStart] is '?' or '#') return "/";
        var pathEnd = raw.IndexOfAny(['?', '#'], pathStart);
        return pathEnd < 0 ? raw[pathStart..] : raw[pathStart..pathEnd];
    }

    // StreamRequest.swift allowed only seek=. The server now also stamps a session-scoped stream token
    // (st=) on transcode URLs (CastPlaybackPreparer.kt relies on it; the Android player passes the URL
    // through unchanged). Accept at most one of each, nothing else. The token is a credential: never log it.
    private static bool IsPlanQuery(string query)
    {
        bool seenSeek = false, seenToken = false;
        foreach (var pair in query.Split('&'))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) return false;
            var key = Uri.UnescapeDataString(pair[..equals]);
            var value = Uri.UnescapeDataString(pair[(equals + 1)..]);
            if (key == "seek" && !seenSeek)
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    || !double.IsFinite(seconds) || seconds < 0) return false;
                seenSeek = true;
            }
            else if (key == "st" && !seenToken)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                seenToken = true;
            }
            else return false;
        }
        return true;
    }
}
