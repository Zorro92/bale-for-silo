using System.Text.RegularExpressions;
using Bale.Core.Native;
using Bale.Core.Api.Models;

namespace Bale.Core.Detail;

/// <summary>Inputs to the official subtitle Auto preview; absence of this context means resolution is unknown.</summary>
public sealed record SubtitleAutoContext(string? PreferredLanguage, string? Mode = null, bool ShowForced = false,
    string? AudioLanguage = null, WatchSubtitleSignature? TrackSignature = null);

public static partial class PlaybackFormatting
{
    /// <summary>
    /// AutoSubtitleResolver.kt's catalog-only preview, including remembered series choices. Every branch ranks
    /// only tracks the server can show (Android 461240c6d): an unplayable track is never picked, not even as the
    /// only track or the series choice.
    /// </summary>
    internal static int? AutoResolvedSubtitleOrdinal(IReadOnlyList<VersionSubtitleTrack> tracks, SubtitleAutoContext context)
    {
        if (tracks.Count == 0) return null;
        var mode = NonEmpty(context.Mode)?.ToLowerInvariant() ?? "auto";
        var audioLanguage = SubtitleLanguageKey(context.AudioLanguage);
        if (mode == "off")
        {
            return context.ShowForced && audioLanguage is not null ? BestForced(audioLanguage) : null;
        }

        var signature = context.TrackSignature;
        if (signature is not null)
        {
            for (var i = 0; i < tracks.Count; i++)
            {
                if (!IsUnplayableSubtitle(tracks[i]) && MatchesSignature(tracks[i], signature)) return i;
            }
        }

        if (context.PreferredLanguage is not null && string.IsNullOrWhiteSpace(context.PreferredLanguage)) return null;
        var targetLanguage = SubtitleLanguageKey(context.PreferredLanguage);
        if (targetLanguage is null) return mode == "always" ? Best(null, signature) : null;
        if (mode == "auto" && audioLanguage == targetLanguage)
        {
            return context.ShowForced ? BestForced(targetLanguage) : null;
        }

        return Best(targetLanguage, signature) ?? (context.ShowForced ? BestForced(null) : null);

        int? Best(string? language, WatchSubtitleSignature? remembered) =>
            Order(Enumerable.Range(0, tracks.Count).Where(i => language is null || SubtitleLanguageKey(tracks[i].Language) == language), remembered).FirstOrDefault();

        int? BestForced(string? language) =>
            Order(Enumerable.Range(0, tracks.Count).Where(i => tracks[i].Forced == true
                && (language is null || SubtitleLanguageKey(tracks[i].Language) == language)), null).FirstOrDefault();

        IEnumerable<int?> Order(IEnumerable<int> candidates, WatchSubtitleSignature? remembered) => candidates
            .Where(i => !IsUnplayableSubtitle(tracks[i]))
            .OrderByDescending(i => remembered is null ? 0 : SignatureScore(tracks[i], remembered))
            .ThenBy(i => NeedsBurnIn(tracks[i]))
            .ThenBy(i => tracks[i].Forced == true)
            .ThenBy(i => IsHearingImpaired(tracks[i]))
            .ThenBy(i => tracks[i].External == true)
            .Select(i => (int?)i);
    }

    /// <summary>
    /// DetailPlaybackFormatting.swift subtitleIndexAfterVersionPick (Apple 417508d9; owner, 2026-11-18, a deliberate
    /// deviation from Android's onVersionSelected, which clears the pick): the subtitle to keep when a Version or
    /// Edition pick moves the page from <paramref name="previous"/> to <paramref name="next"/>. Auto (null) and Off
    /// (-1) carry over, and so does any pick when the file stays. A track index names a stream of one file, so a
    /// picked track becomes the next file's track in the same language with the same forced and SDH flags, the same
    /// full tag (zh-Hant) before the same primary language, preferring the same title; with none, Auto. A track
    /// tagged "und" or untagged identifies no track. Every stored Bale pick is the viewer's own (Auto is never
    /// seeded with an index), so Apple's reseed-from-preference branch for unpicked seeds has no counterpart here.
    /// </summary>
    public static int? SubtitleIndexAfterVersionPick(int? index, FileVersion? previous, FileVersion? next)
    {
        if (index is not >= 0 || previous?.FileId == next?.FileId) return index;
        var before = previous?.SubtitleTracks ?? [];
        var ordinal = CombinedSubtitleSelectionIndexes(before).IndexOf(index.Value);
        if (ordinal < 0) return null;
        var picked = before[ordinal];
        // "und" names no language, so it identifies no track (TrackSelectionPersistence.swift taggedLanguage).
        if (SubtitleLanguageKey(picked.Language) is not { } language) return null;

        var tracks = next?.SubtitleTracks ?? [];
        var combined = CombinedSubtitleSelectionIndexes(tracks);
        var sameLanguage = Enumerable.Range(0, tracks.Count).Where(i => !IsUnplayableSubtitle(tracks[i])
            && SubtitleLanguageKey(tracks[i].Language) == language
            && CarriedForced(tracks[i]) == CarriedForced(picked)
            && CarriedHearingImpaired(tracks[i]) == CarriedHearingImpaired(picked)).ToList();
        // The same tag (zh-Hant) before the same primary language (zh-Hans), as the player's resolver ranks them.
        var sameTag = sameLanguage.Where(i => LanguageTag(tracks[i].Language) == LanguageTag(picked.Language)).ToList();
        var matches = sameTag.Count > 0 ? sameTag : sameLanguage;
        // "Latin American" and "Spanish (Latin American)" name the same track.
        if (CarriedTitle(picked) is { } wanted
            && (Titled(title => title.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                ?? Titled(title => title.Contains(wanted, StringComparison.OrdinalIgnoreCase) || wanted.Contains(title, StringComparison.OrdinalIgnoreCase)))
            is { } match)
        {
            return combined[match];
        }

        return matches.Count > 0 ? combined[matches[0]] : null;

        int? Titled(Func<string, bool> test)
        {
            foreach (var i in matches)
            {
                if (CarriedTitle(tracks[i]) is { } title && test(title)) return i;
            }

            return null;
        }

        static string? LanguageTag(string? code) => code?.Trim().ToLowerInvariant().Replace('_', '-');
    }

    // DetailPlaybackFormatting.swift isForced / isHearingImpaired / nonEmpty(title) ?? nonEmpty(embeddedTitle).
    private static bool CarriedForced(VersionSubtitleTrack track) =>
        track.Forced == true || (track.Title ?? track.EmbeddedTitle)?.Contains("forced", StringComparison.OrdinalIgnoreCase) == true;

    private static bool CarriedHearingImpaired(VersionSubtitleTrack track)
    {
        if (track.HearingImpaired == true) return true;
        // SubtitleAutoResolver.titleIndicatesHearingImpaired.
        var title = (track.Title ?? track.EmbeddedTitle)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(title)) return false;
        if (title.Contains("hearing impaired", StringComparison.Ordinal) || title.Contains("closed caption", StringComparison.Ordinal)) return true;
        var tokens = Regex.Split(title, @"[^\p{L}\p{N}]+");
        return tokens.Contains("cc") || tokens.Contains("sdh");
    }

    private static string? CarriedTitle(VersionSubtitleTrack track) => NonEmpty(track.Title) ?? NonEmpty(track.EmbeddedTitle);

    private static bool NeedsBurnIn(VersionSubtitleTrack track)
    {
        var token = string.Concat((track.Codec ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (token.Contains("pgs")) return track.External == true;
        return token.Contains("vobsub") || token.Contains("dvdsubtitle") || token.Contains("dvbsub");
    }

    // The resolver honors the explicit flag too; the rendered badge uses the title only, as in TvPlaybackFormatting.kt.
    private static bool IsHearingImpaired(VersionSubtitleTrack track) => track.HearingImpaired == true || LabelIndicatesHearingImpaired(track.Title);

    private static bool MatchesSignature(VersionSubtitleTrack track, WatchSubtitleSignature signature) =>
        Normalize(signature.Source) == (track.External == true ? "external" : "embedded")
        && (Normalize(signature.Language) == Normalize(track.Language)
            || SubtitleLanguageKey(signature.Language) is { } language && language == SubtitleLanguageKey(track.Language))
        && Normalize(signature.Codec) == Normalize(track.Codec)
        && (Normalize(signature.Label).Length == 0 || Normalize(signature.Label) == Normalize(track.Title))
        && (signature.Forced == true) == (track.Forced == true)
        && (signature.HearingImpaired == true) == IsHearingImpaired(track);

    private static int SignatureScore(VersionSubtitleTrack track, WatchSubtitleSignature signature) =>
        (Normalize(signature.Source) == (track.External == true ? "external" : "embedded") ? 4 : 0)
        + ((signature.Forced == true) == (track.Forced == true) ? 2 : 0)
        + ((signature.HearingImpaired == true) == IsHearingImpaired(track) ? 2 : 0)
        + (Normalize(signature.Codec) == Normalize(track.Codec) ? 1 : 0)
        + (Normalize(signature.Label) == Normalize(track.Title) ? 1 : 0);

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>
    /// The comparable primary language: lower-cased, region dropped, ISO 639-2 folded to 639-1; null
    /// for missing, blank or "und". Implemented in Rust (native/bale-core/src/language.rs, with the
    /// SubtitleLanguage.kt alias table).
    /// </summary>
    internal static string? SubtitleLanguageKey(string? value) =>
        BaleCoreNative.Call("language.subtitle_language_key", value, BaleCoreJson.Default.String, BaleCoreJson.Default.String);
}
