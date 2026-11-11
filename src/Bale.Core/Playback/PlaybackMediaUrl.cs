using System.Globalization;

namespace Bale.Core.Playback;

/// <summary>
/// StreamRequest.swift's header-authenticated API-local media boundary. Bale never negotiates
/// authorized_media_origins_v1, so even same-origin absolute plan URLs are refused.
/// </summary>
public static class PlaybackMediaUrl
{
    public static Uri? Resolve(string? raw, Uri server) => Resolve(raw, server, subtitle: false);

    /// <summary>
    /// A plan's subtitle sidecar or font bundle: the same header-authenticated boundary, narrowed to
    /// silo-server subtitle_inventory_v3.go's /stream/{session}/subtitles/{track}[.ext|/fonts] routes
    /// and the query keys SubtitleStreamURLV3 / DownloadedSubtitleStreamURLV3 stamp on them.
    /// </summary>
    public static Uri? ResolveSubtitle(string? raw, Uri server) => Resolve(raw, server, subtitle: true);

    private static Uri? Resolve(string? raw, Uri server, bool subtitle)
    {
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw) || !IsServer(server) || !raw.StartsWith('/')
            || raw.StartsWith("//", StringComparison.Ordinal) || raw.Contains("://", StringComparison.Ordinal)
            || raw.Contains('#')) return null;

        var queryStart = raw.IndexOf('?');
        var path = queryStart < 0 ? raw : raw[..queryStart];
        if (!IsMediaPath(path, "") || !IsSafePath(path)) return null;
        if (subtitle)
        {
            if (!IsSubtitlePath(path) || (queryStart >= 0 && !IsSubtitleQuery(raw[(queryStart + 1)..]))) return null;
        }
        else if (queryStart >= 0 && !IsPlanQuery(raw[(queryStart + 1)..])) return null;

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

    // /api/v2/stream/{session}/subtitles/{track}[.ext] or .../{track}/fonts, nothing deeper.
    private static bool IsSubtitlePath(string path)
    {
        const string prefix = "/api/v2/stream/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var segments = path[prefix.Length..].Split('/');
        return segments.Length is 3 or 4 && segments[0].Length > 0 && segments[1] == "subtitles"
            && segments[2].Length > 0 && (segments.Length == 3 || segments[3] == "fonts");
    }

    // file_id, downloaded_subtitle_id and embedded_stream_index are numeric IDs, original=1 marks raw
    // SubRip, external_subtitle_key is a sidecar path's SHA-256 hex (silo-server subtitles.ExternalPathKey).
    // Once each.
    private static bool IsSubtitleQuery(string query)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&'))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) return false;
            var key = pair[..equals];
            var value = pair[(equals + 1)..];
            if (!seen.Add(key)) return false;
            switch (key)
            {
                case "file_id" or "downloaded_subtitle_id" or "embedded_stream_index":
                    if (value.Length is 0 or > 18 || !value.All(char.IsAsciiDigit)) return false;
                    break;
                case "position" or "duration":
                    if (value.Length is 0 or > 32
                        || !double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
                        || !double.IsFinite(seconds) || seconds < 0
                        || (key == "duration" && (seconds <= 0 || seconds > 3600))) return false;
                    break;
                case "windowed":
                case "original":
                    if (value != "1") return false;
                    break;
                case "external_subtitle_key":
                    if (value.Length is 0 or > 128 || !value.All(char.IsAsciiHexDigit)) return false;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

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
