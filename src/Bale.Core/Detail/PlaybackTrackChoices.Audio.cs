using Bale.Core.Api.Models;

namespace Bale.Core.Detail;

/// <summary>A null choice means reset to Auto; a null overlay means use the server's answer.</summary>
public sealed record PendingSeriesAudioChoice(AudioPreferenceUpdate? Choice);

public static partial class PlaybackTrackChoices
{
    /// <summary>
    /// shared/src/commonMain/kotlin/org/siloserver/silo/model/playback/SeriesAudioChoice.kt's resolveOn:
    /// signature, remembered ordinal while its language matches, then closest language in catalog order.
    /// </summary>
    public static int? ResolveAudioOrdinal(AudioPreferenceUpdate choice, IReadOnlyList<AudioTrack> tracks)
    {
        var all = Enumerable.Range(0, tracks.Count).ToList();
        var decodable = all.Where(i => Clean(tracks[i].Codec) is { } codec
            && !codec.Equals("unknown", StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = decodable.Count > 0 ? decodable : all;
        if (choice.TrackSignature is { } signature)
        {
            var matched = pool.Where(i => MatchesAudioSignature(tracks[i], signature))
                .OrderBy(i => AudioLanguageRank(tracks[i].Language, signature.Language)).Select(i => (int?)i).FirstOrDefault();
            if (matched is not null) return matched;
        }

        if (choice.AudioLanguage is not { } language) return null;
        if (choice.AudioTrackIndex is >= 0 and var ordinal && ordinal < tracks.Count
            && pool.Contains((int)ordinal) && AudioLanguageRank(tracks[(int)ordinal].Language, language) >= 0) return (int)ordinal;
        return pool.Where(i => AudioLanguageRank(tracks[i].Language, language) >= 0)
            .OrderBy(i => AudioLanguageRank(tracks[i].Language, language)).Select(i => (int?)i).FirstOrDefault();
    }

    private static bool MatchesAudioSignature(AudioTrack track, AudioTrackSignature signature)
    {
        var language = PlaybackFormatting.SubtitleLanguageKey(track.Language);
        if (language != PlaybackFormatting.SubtitleLanguageKey(signature.Language)) return false;
        var hasOtherFields = signature.Title is not null || signature.EmbeddedTitle is not null
            || signature.Codec is not null || signature.Layout is not null || signature.Channels is not null;
        if (!hasOtherFields && (language is null || signature.Language is null)) return false;
        return SameText(track.Title, signature.Title) && SameText(track.EmbeddedTitle, signature.EmbeddedTitle)
            && SameText(track.Codec, signature.Codec) && SameText(track.Layout, signature.Layout)
            && (track.Channels ?? 0) == (signature.Channels ?? 0);
    }

    private static int AudioLanguageRank(string? candidate, string? preferred)
    {
        var a = ComparableLanguageTag(candidate);
        var b = ComparableLanguageTag(preferred);
        if (a is null || b is null) return -1;
        if (a == b) return 0;
        if (a.Split('-')[0] != b.Split('-')[0]) return -1;
        return a.Contains('-') ? 2 : 1;
    }

    private static string? ComparableLanguageTag(string? value)
    {
        var tag = Clean(value)?.ToLowerInvariant().Replace('_', '-');
        if (tag is null || PlaybackFormatting.SubtitleLanguageKey(tag.Split('-')[0]) is not { } primary) return null;
        var separator = tag.IndexOf('-');
        return separator < 0 ? primary : primary + tag[separator..];
    }

    private static bool SameText(string? a, string? b) =>
        string.Equals(Clean(a) ?? string.Empty, Clean(b) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
