using Bale.Core.Native;
using Bale.Core.Api.Models;

namespace Bale.Core.Detail;

/// <summary>Inputs to the official subtitle Auto preview; absence of this context means resolution is unknown.</summary>
public sealed record SubtitleAutoContext(string? PreferredLanguage, string? Mode = null, bool ShowForced = false,
    string? AudioLanguage = null, WatchSubtitleSignature? TrackSignature = null);

public static partial class PlaybackFormatting
{
    /// <summary>AutoSubtitleResolver.kt's catalog-only preview, including remembered series choices.</summary>
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
                if (MatchesSignature(tracks[i], signature)) return i;
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
            .OrderByDescending(i => remembered is null ? 0 : SignatureScore(tracks[i], remembered))
            .ThenBy(i => NeedsBurnIn(tracks[i]))
            .ThenBy(i => tracks[i].Forced == true)
            .ThenBy(i => IsHearingImpaired(tracks[i]))
            .ThenBy(i => tracks[i].External == true)
            .Select(i => (int?)i);
    }

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
