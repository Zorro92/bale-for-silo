using System.Globalization;
using System.Text.RegularExpressions;
using Bale.Core.Api.Models;

namespace Bale.Core.Detail;

public static partial class PlaybackFormatting
{
    /// <summary>Audio selections are ordinals into audio_tracks, not stream indexes (TvPlaybackFormatting.kt).</summary>
    public static string AudioValueLabel(FileVersion? version, int? selectedAudioTrackIndex = null,
        int? automaticAudioTrackOrdinal = null)
    {
        var ordinal = ResolvedAudioOrdinal(version, selectedAudioTrackIndex, automaticAudioTrackOrdinal);
        if (ordinal is null || version?.AudioTracks is not { } tracks)
        {
            return "Unknown";
        }

        var track = tracks[ordinal.Value];
        var tokens = new[] { LanguageDisplayName(track.Language), NormalizedAudioCodec(track.Codec), CompactAudioLayout(track) }.OfType<string>().ToList();
        var summary = tokens.Count > 0 ? string.Join(" · ", tokens)
            : LanguageDisplayName(track.Language) ?? UsefulAudioTitle(track.Title) ?? $"Track {ordinal + 1}";
        return selectedAudioTrackIndex is null ? "Auto: " + summary : summary;
    }

    public static string? ResolvedAudioLanguage(FileVersion? version, int? selectedAudioTrackIndex = null,
        int? automaticAudioTrackOrdinal = null) =>
        ResolvedAudioOrdinal(version, selectedAudioTrackIndex, automaticAudioTrackOrdinal) is { } ordinal
            ? version!.AudioTracks![ordinal].Language : null;

    private static int? ResolvedAudioOrdinal(FileVersion? version, int? selected, int? automatic)
    {
        if (version?.AudioTracks is not { Count: > 0 } tracks)
        {
            return null;
        }

        if (selected is >= 0 && selected < tracks.Count) return selected;
        if (automatic is >= 0 && automatic < tracks.Count) return automatic;
        if (version.EffectiveAudioTrackIndex is >= 0 and var effective && effective < tracks.Count) return (int)effective;
        var defaultIndex = tracks.FindIndex(track => track.Default == true);
        return defaultIndex >= 0 ? defaultIndex : 0;
    }

    /// <summary>
    /// Null = Auto, -1 = Off; other subtitle selections use the combined external-first index space,
    /// never the catalog position or the raw stream index (TvPlaybackFormatting.kt).
    /// </summary>
    public static string SubtitleValueLabel(FileVersion? version, int? selectedSubtitleTrackIndex = null,
        SubtitleAutoContext? autoContext = null)
    {
        var tracks = version?.SubtitleTracks;
        if (selectedSubtitleTrackIndex is null)
        {
            if (autoContext is not null)
            {
                var resolved = AutoResolvedSubtitleOrdinal(tracks ?? [], autoContext);
                return "Auto - " + (resolved is { } ordinal ? SubtitleSummary(tracks![ordinal], ordinal) : "None");
            }

            // Unknown resolution inputs must not guess from the catalog's Default flag.
            return tracks is { Count: 1 } ? SubtitleSummary(tracks[0], 0) : "Auto";
        }

        if (selectedSubtitleTrackIndex == -1) return "Off";
        if (tracks is null) return "On";
        var selected = CombinedSubtitleSelectionIndexes(tracks).IndexOf(selectedSubtitleTrackIndex.Value);
        return selected < 0 ? "On" : SubtitleSummary(tracks[selected], selected);
    }

    /// <summary>
    /// The Auto half of TvPlaybackFormatting.subtitleLaunchSelection: the preview's combined index, -1 when
    /// the preview is "None", null when there is no Auto context to preview with.
    /// </summary>
    public static int? SubtitleLaunchIndex(FileVersion? version, SubtitleAutoContext? autoContext)
    {
        if (autoContext is null) return null;
        var tracks = version?.SubtitleTracks ?? [];
        return AutoResolvedSubtitleOrdinal(tracks, autoContext) is { } ordinal
            ? CombinedSubtitleSelectionIndexes(tracks)[ordinal] : -1;
    }

    public static List<int> CombinedSubtitleSelectionIndexes(IReadOnlyList<VersionSubtitleTrack> tracks)
    {
        var externalCount = tracks.Count(track => track.External == true);
        var externalsSeen = 0;
        var embeddedSeen = 0;
        return tracks.Select(track => track.External == true ? externalsSeen++ : externalCount + embeddedSeen++).ToList();
    }

    private static string SubtitleSummary(VersionSubtitleTrack track, int ordinal)
    {
        var name = LanguageDisplayName(track.Language) ?? MeaningfulSubtitleTitle(track) ?? $"Track {ordinal + 1}";
        if (LabelIndicatesHearingImpaired(track.Title) && !LabelIndicatesHearingImpaired(name)) name += " (SDH)";
        if ((track.Forced == true || Contains(track.Title, "forced")) && !Contains(name, "forced")) name += " (Forced)";
        return NormalizedSubtitleCodec(track.Codec) is { } codec ? $"{name} · {codec}" : name;
    }

    private static string? MeaningfulSubtitleTitle(VersionSubtitleTrack track)
    {
        var title = NonEmpty(track.Title);
        if (title is null) return null;
        var lowered = title.ToLowerInvariant();
        if (lowered is "subtitle" or "subtitles" or "forced" or "sdh" or "cc" or "hearing impaired"
            || lowered == LanguageDisplayName(track.Language)?.ToLowerInvariant()
            || lowered == NonEmpty(track.Language)?.ToLowerInvariant()
            || lowered == NormalizedSubtitleCodec(track.Codec)?.ToLowerInvariant()
            || lowered == track.Codec?.ToLowerInvariant()
            || title.Length > 28 || title.Contains('[')
            || new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".sup", ".idx" }.Any(suffix => lowered.EndsWith(suffix, StringComparison.Ordinal)))
        {
            return null;
        }

        return DisplayTitle(title);
    }

    private static string? NormalizedAudioCodec(string? codec) => NonEmpty(codec)?.ToLowerInvariant() switch
    {
        null => null,
        var value when value.Contains("eac3") || value.Contains("e-ac-3") || value.Contains("ec-3") => "EAC3",
        var value when value.Contains("ac3") || value.Contains("ac-3") => "AC3",
        var value when value.Contains("aac") => "AAC",
        var value when value.Contains("mp3") => "MP3",
        var value when value.Contains("truehd") => "TrueHD",
        var value when value.Contains("dts") => "DTS",
        var value when value.Contains("flac") => "FLAC",
        var value => value.ToUpperInvariant(),
    };

    private static string? NormalizedSubtitleCodec(string? codec) => NonEmpty(codec)?.ToLowerInvariant() switch
    {
        null => null,
        var value when value == "srt" || value.Contains("subrip") => "SRT",
        var value when value.Contains("ass") => "ASS",
        var value when value.Contains("ssa") => "SSA",
        var value when value == "vtt" || value.Contains("webvtt") => "WebVTT",
        var value when value.Contains("pgs") || value.Contains("hdmv") => "PGS",
        var value when value.Contains("dvd") || value.Contains("vobsub") => "VobSub",
        var value when value.Contains("mov_text") || value.Contains("tx3g") => "TX3G",
        var value => value.ToUpperInvariant(),
    };

    private static string? CompactAudioLayout(AudioTrack track)
    {
        if (NonEmpty(track.Layout) is { } layout)
        {
            return layout.ToLowerInvariant() switch
            {
                var value when value.Contains("atmos") => "Atmos",
                var value when value.Contains("7.1") => "7.1",
                var value when value.Contains("5.1") => "5.1",
                var value when value.Contains("stereo") => "Stereo",
                _ => layout,
            };
        }

        return track.Channels switch
        {
            null => null,
            1 => "Mono",
            2 => "Stereo",
            6 => "5.1",
            8 => "7.1",
            var channels => $"{channels}ch",
        };
    }

    private static string? UsefulAudioTitle(string? value)
    {
        var title = NonEmpty(value);
        return title is null || new[] { "atsc", "a/52", "ac-3", "e-ac-3", "eac3", "truehd", "dts", "aac", "flac" }.Any(term => Contains(title, term))
            ? null : DisplayTitle(title);
    }

    private static string DisplayTitle(string value) => value.Trim().ToLowerInvariant() switch
    {
        "sdh" => "SDH",
        "cc" => "CC",
        "srt" or "subrip" => "SRT",
        "webvtt" or "vtt" => "WebVTT",
        _ => value.Trim(),
    };

    private static string? LanguageDisplayName(string? value)
    {
        var trimmed = NonEmpty(value);
        if (trimmed is null) return null;
        var primary = trimmed.ToLowerInvariant().Replace('_', '-').Split('-')[0];
        if (primary.Length > 3) return CapitalizeWords(trimmed);
        var code = LanguageAliases.GetValueOrDefault(primary, primary);
        try
        {
            var display = CultureInfo.GetCultureInfo(code).EnglishName;
            return display.Length > 0 && !display.Equals(code, StringComparison.OrdinalIgnoreCase)
                ? CapitalizeWords(display) : trimmed.ToUpperInvariant();
        }
        catch (CultureNotFoundException)
        {
            return trimmed.ToUpperInvariant();
        }
    }

    private static string CapitalizeWords(string value) => string.Join(" ", value.Split(' ').Select(word =>
        word.Length > 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word));

    private static bool Contains(string? value, string token) => value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;

    private static bool LabelIndicatesHearingImpaired(string? title) => title is not null
        && (Contains(title, "closed caption") || Contains(title, "hearing impaired") || Contains(title, "hearing-impaired")
            || HearingImpairedToken().IsMatch(title));

    private static readonly Dictionary<string, string> LanguageAliases = new()
    {
        ["ara"] = "ar", ["chi"] = "zh", ["cze"] = "cs", ["dan"] = "da", ["deu"] = "de",
        ["dut"] = "nl", ["eng"] = "en", ["fin"] = "fi", ["fra"] = "fr", ["fre"] = "fr",
        ["ger"] = "de", ["hin"] = "hi", ["ita"] = "it", ["jpn"] = "ja", ["kor"] = "ko",
        ["nld"] = "nl", ["nor"] = "no", ["pol"] = "pl", ["por"] = "pt", ["rus"] = "ru",
        ["spa"] = "es", ["swe"] = "sv", ["zho"] = "zh",
    };

    [GeneratedRegex(@"(^|[^a-z0-9])(cc|sdh)([^a-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HearingImpairedToken();
}
