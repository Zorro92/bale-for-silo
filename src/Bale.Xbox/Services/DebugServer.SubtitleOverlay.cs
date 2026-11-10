#if DEBUG_SERVER
using System.Net.Http;
using System.Text;

namespace Bale.Xbox.Services;

/// <summary>
/// Drives the player's own subtitle renderer (Services/SubtitleOverlay.cs) by hand: load an ASS/SSA file,
/// with optional fonts, over whatever is playing, clear it, and read the overlay's counters. The real
/// player does not fetch or claim subtitles yet. URLs, response headers and exception messages are never
/// echoed or logged: fixed messages, HTTP status codes and HRESULTs only.
/// </summary>
internal sealed partial class DebugServer
{
    private const int SubtitleOverlayMaxScriptBytes = 8 * 1024 * 1024;
    // CJK fonts are far larger than any script.
    private const int SubtitleOverlayMaxFontBytes = 32 * 1024 * 1024;
    private const int SubtitleOverlayMaxFonts = 16;
    private const int SubtitleOverlayDownloadSeconds = 15;
    private const string SubtitleOverlayUsage =
        "usage: GET /suboverlay | POST /suboverlay?action=load&url=<http(s) url>[&font=<http(s) url>...] | POST /suboverlay?action=clear\n";

    private static Task<(string Status, string Body)> SubtitleOverlayStatsAsync() => OnSubtitleOverlayUiAsync(() =>
        CurrentPage(out _) is PlayerPage player
            ? ("200 OK", player.DebugSubtitleStats)
            : ("409 Conflict", "no active player page\n"));

    private static async Task<(string Status, string Body)> SubtitleOverlayAsync(string rawQuery)
    {
        var pairs = SubtitleOverlayQuery(rawQuery);
        var actions = pairs.Where(pair => pair.Key == "action").Select(pair => pair.Value).ToArray();
        var urls = pairs.Where(pair => pair.Key == "url").Select(pair => pair.Value).ToArray();
        var fontUrls = pairs.Where(pair => pair.Key == "font").Select(pair => pair.Value).ToArray();
        if (actions.Length != 1 || pairs.Any(pair => pair.Key is not ("action" or "url" or "font")))
            return ("400 Bad Request", SubtitleOverlayUsage);

        if (actions[0] == "clear")
        {
            if (pairs.Count != 1) return ("400 Bad Request", SubtitleOverlayUsage);
            Log.Info("Debug suboverlay: clear");
            return await OnSubtitleOverlayUiAsync(() => CurrentPage(out _) is PlayerPage player
                ? player.DebugClearSubtitles()
                : ("409 Conflict", "no active player page\n"));
        }

        if (actions[0] != "load" || urls.Length != 1 || fontUrls.Length > SubtitleOverlayMaxFonts
            || !TryParseSubtitleOverlayUrl(urls[0], out var scriptUri))
            return ("400 Bad Request", SubtitleOverlayUsage);
        var fontUris = new List<Uri>();
        foreach (var fontUrl in fontUrls)
        {
            if (!TryParseSubtitleOverlayUrl(fontUrl, out var fontUri)) return ("400 Bad Request", SubtitleOverlayUsage);
            fontUris.Add(fontUri);
        }

        // Fail before downloading anything when there is nothing to draw over.
        var ready = await OnSubtitleOverlayUiAsync(() => CurrentPage(out _) is PlayerPage
            ? ("200 OK", string.Empty) : ("409 Conflict", "no active player page\n"));
        if (ready.Status != "200 OK") return ready;

        Log.Info($"Debug suboverlay: load fonts={fontUris.Count}");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(SubtitleOverlayDownloadSeconds) };
        var (script, scriptFailure) = await DownloadSubtitleOverlayAsync(client, scriptUri, SubtitleOverlayMaxScriptBytes);
        if (script is null) return ("502 Bad Gateway", $"subtitle download failed: {scriptFailure}\n");
        var fonts = new List<(string Name, byte[] Data)>();
        for (var i = 0; i < fontUris.Count; i++)
        {
            var (font, fontFailure) = await DownloadSubtitleOverlayAsync(client, fontUris[i], SubtitleOverlayMaxFontBytes);
            if (font is null) return ("502 Bad Gateway", $"font {i + 1} download failed: {fontFailure}\n");
            fonts.Add((SubtitleOverlayFontName(fontUris[i]), font));
        }

        string text;
        try
        {
            // Honors and strips a UTF-8/UTF-16 byte-order mark; the overlay takes UTF-8.
            using var reader = new StreamReader(new MemoryStream(script), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return ("400 Bad Request", $"subtitle text could not be decoded hr=0x{unchecked((uint)ex.HResult):X8}\n");
        }

        var result = await OnSubtitleOverlayUiAsync(() => CurrentPage(out _) is PlayerPage player
            ? player.DebugLoadSubtitles(text, fonts)
            : ("409 Conflict", "no active player page\n"));
        Log.Info($"Debug suboverlay: load bytes={script.Length} result={result.Status.Split(' ')[0]}");
        return result;
    }

    private static bool TryParseSubtitleOverlayUrl(string value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https") || parsed.UserInfo.Length != 0) return false;
        uri = parsed;
        return true;
    }

    /// <summary>The file name is only libass's label for the font; it is never logged or echoed.</summary>
    private static string SubtitleOverlayFontName(Uri uri)
    {
        try { return Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)); }
        catch { return string.Empty; }
    }

    private static async Task<(byte[]? Data, string Failure)> DownloadSubtitleOverlayAsync(HttpClient client, Uri uri, int maxBytes)
    {
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return (null, $"HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > maxBytes) return (null, $"larger than {maxBytes} bytes");
            // HttpClient.Timeout only covers the headers with ResponseHeadersRead; bound the body too.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(SubtitleOverlayDownloadSeconds));
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > maxBytes) return (null, $"larger than {maxBytes} bytes");
                buffer.Write(chunk, 0, read);
            }
            return buffer.Length == 0 ? (null, "empty response") : (buffer.ToArray(), string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (null, $"timed out after {SubtitleOverlayDownloadSeconds} s");
        }
        catch (Exception ex)
        {
            // Type and HRESULT only: the message can contain the URL.
            return (null, $"{ex.GetType().Name} hr=0x{unchecked((uint)ex.HResult):X8}");
        }
    }

    /// <summary>Like ParseQuery, but keeps repeated keys (font=) in order.</summary>
    private static List<KeyValuePair<string, string>> SubtitleOverlayQuery(string query)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var pair in query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split(new[] { '=' }, 2);
            pairs.Add(new(Uri.UnescapeDataString(split[0]),
                split.Length > 1 ? Uri.UnescapeDataString(split[1].Replace('+', ' ')) : string.Empty));
        }
        return pairs;
    }

    private static async Task<(string Status, string Body)> OnSubtitleOverlayUiAsync(Func<(string Status, string Body)> action)
    {
        try
        {
            // The general debug dispatcher formats exception messages; this one propagates them unformatted.
            return await OnFfmpegUiAsync(action);
        }
        catch (Exception ex)
        {
            var failure = $"debug suboverlay failed hr=0x{unchecked((uint)ex.HResult):X8}\n";
            Log.Info(failure.TrimEnd());
            return ("500 Internal Server Error", failure);
        }
    }
}
#endif
