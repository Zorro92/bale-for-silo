using System.Text;
using System.Text.Json;
using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>
/// What the player needs to draw a plan's subtitle track itself: the sidecar and its font bundle (both
/// header-authenticated API URLs), the overlay's text format, and the key under which fetched bytes may
/// be reused by a remount of the same track.
/// </summary>
public sealed record PlaybackSubtitleSource(Uri Uri, uint Format, string Mode, double TimingOriginSeconds, Uri? FontBundle)
{
    /// <summary>bale_sub_overlay_load_text format IDs.</summary>
    public const uint Ass = 0, Srt = 1, WebVtt = 2;

    /// <summary>Fixed name for logs; never server text.</summary>
    public string FormatName => Format switch { Ass => "ass", Srt => "srt", _ => "vtt" };

    /// <summary>Session-scoped URLs: a seek reanchor or recovery mount of the same track keeps the key.</summary>
    public string CacheKey => Uri.AbsoluteUri + "\n" + FontBundle?.AbsoluteUri;
}

/// <summary>
/// silo-server SubtitleDecisionV3: "render" and "convert" both hand the client a text artifact to draw;
/// "off" and "burn_in" need nothing. Bale claims no in-container subtitles, so an embedded decision is
/// not something it can honor.
/// </summary>
public static class PlaybackSubtitleResolver
{
    public static PlaybackSubtitleSource? Resolve(PlaybackPlan? plan, Uri server)
    {
        if (plan?.Subtitle is not { Embedded: null, Artifact: { } artifact } subtitle) return null;
        var mode = subtitle.Mode switch { "render" => "render", "convert" => "convert", _ => null };
        if (mode is null || Format(artifact.Format, artifact.MimeType) is not { } format
            || PlaybackMediaUrl.ResolveSubtitle(artifact.Url, server) is not { } uri) return null;

        Uri? fonts = null;
        // Only embedded ASS/SSA carries attachments (subtitle_inventory_v3.go SubtitleFontBundleURLV3).
        // An unusable bundle URL only loses the fonts: libass falls back to system fonts.
        if (format == PlaybackSubtitleSource.Ass && !string.IsNullOrEmpty(subtitle.TrackId)
            && subtitle.Inventory?.FirstOrDefault(item => item?.TrackId == subtitle.TrackId) is { FontBundleUrl: { Length: > 0 } bundle }
            && PlaybackMediaUrl.ResolveSubtitle(bundle, server) is { } resolved
            && resolved.AbsolutePath.EndsWith("/fonts", StringComparison.Ordinal))
            fonts = resolved;

        var origin = artifact.TimingOriginSeconds is { } seconds && double.IsFinite(seconds) ? seconds : 0;
        return new(uri, format, mode, origin, fonts);
    }

    /// <summary>The declared format wins; the MIME type decides when it is absent or unknown.</summary>
    internal static uint? Format(string? format, string? mimeType) =>
        Named(format) ?? Named(mimeType?.Split(';')[0]);

    private static uint? Named(string? name) => name?.Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "ass" or "ssa" or "text/x-ssa" or "text/x-ass" => PlaybackSubtitleSource.Ass,
        "srt" or "subrip" or "application/x-subrip" => PlaybackSubtitleSource.Srt,
        "vtt" or "webvtt" or "text/vtt" => PlaybackSubtitleSource.WebVtt,
        _ => null,
    };
}

public static class PlaybackSubtitleText
{
    /// <summary>Far above any real script; a guard against a runaway response, not a tuning value.</summary>
    public const int MaxBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The overlay parses UTF-8. The server's conversions are UTF-8 already; a stored file served as is
    /// (subrip_sidecar_v1) may still open with a byte-order mark, so strip UTF-8's and transcode UTF-16.
    /// </summary>
    public static byte[] ToUtf8(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return data[3..];
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(data, 2, data.Length - 2));
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.UTF8.GetBytes(Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2));
        return data;
    }
}

public sealed record SubtitleFont(string Name, byte[] Data);

/// <summary>
/// GET /api/v2/stream/{session}/subtitles/{track}/fonts: {"items":[{"name","data"}]} or, from the frozen
/// v1 shape, a bare array; data is base64. The server stops at 32 fonts / 32 MiB; the same caps apply
/// here so a misbehaving response cannot exhaust the console's memory.
/// </summary>
public static class SubtitleFontBundle
{
    public const int MaxFonts = 32;
    public const int MaxTotalBytes = 32 * 1024 * 1024;
    // 32 MiB as base64 plus names and JSON syntax.
    public const int MaxResponseBytes = 46 * 1024 * 1024;

    /// <summary>Entries with missing, empty or invalid data are skipped; malformed JSON throws.</summary>
    public static IReadOnlyList<SubtitleFont> Parse(ReadOnlySpan<byte> json)
    {
        var fonts = new List<SubtitleFont>();
        long total = 0;
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read()) return fonts;
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var found = false;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var items = reader.ValueTextEquals("items"u8);
                    reader.Read();
                    if (items && !found && reader.TokenType == JsonTokenType.StartArray)
                    {
                        found = true;
                        ReadItems(ref reader, fonts, ref total);
                    }
                    else reader.Skip();
                }
            }
            else if (reader.TokenType == JsonTokenType.StartArray) ReadItems(ref reader, fonts, ref total);
        }
        catch (JsonException)
        {
            // The reader's message can quote the payload; keep it out of any log.
            throw new InvalidDataException("The subtitle font bundle is not valid JSON.");
        }
        return fonts;
    }

    private static void ReadItems(ref Utf8JsonReader reader, List<SubtitleFont> fonts, ref long total)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }
            string? name = null;
            byte[]? data = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isName = reader.ValueTextEquals("name"u8);
                var isData = !isName && reader.ValueTextEquals("data"u8);
                reader.Read();
                if (isName && reader.TokenType == JsonTokenType.String) name = reader.GetString();
                // Past the caps, later entries are still walked (to validate the JSON) but not decoded.
                else if (isData && reader.TokenType == JsonTokenType.String && fonts.Count < MaxFonts
                    && reader.TryGetBytesFromBase64(out var decoded)) data = decoded;
                else reader.Skip();
            }
            if (data is not { Length: > 0 } || fonts.Count >= MaxFonts || total + data.Length > MaxTotalBytes) continue;
            total += data.Length;
            fonts.Add(new(name ?? string.Empty, data));
        }
    }
}
