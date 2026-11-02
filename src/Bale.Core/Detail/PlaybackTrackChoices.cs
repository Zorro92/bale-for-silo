using System.Globalization;
using System.Text.RegularExpressions;
using Bale.Core.Api.Models;

namespace Bale.Core.Detail;

/// <summary>SeriesAudioChoice.kt / SeriesSubtitleChoice.kt: server choices use catalog signatures, not rendered labels.</summary>
public static partial class PlaybackTrackChoices
{
    public static string OwnerId(ItemDetail detail) =>
        !string.IsNullOrWhiteSpace(detail.SeriesId) ? detail.SeriesId : detail.ContentId;

    public static AudioPreferenceUpdate Audio(FileVersion version, int ordinal)
    {
        var track = version.AudioTracks![ordinal];
        var signature = new AudioTrackSignature
        {
            Language = Clean(track.Language), Title = Clean(track.Title), EmbeddedTitle = Clean(track.EmbeddedTitle),
            Codec = Clean(track.Codec), Layout = Clean(track.Layout), Channels = track.Channels is > 0 ? track.Channels : null,
            // Default is intentionally absent: it is not part of Android's signature match.
        };
        return new AudioPreferenceUpdate
        {
            AudioTrackIndex = ordinal,
            AudioLanguage = ServerLanguageTag(track.Language),
            // SeriesAudioChoice.kt omits an empty signature rather than pinning an empty match.
            TrackSignature = signature.Language is null && signature.Title is null && signature.EmbeddedTitle is null
                && signature.Codec is null && signature.Layout is null && signature.Channels is null ? null : signature,
        };
    }

    public static SubtitlePreferenceUpdate Subtitle(FileVersion version, int selectionIndex)
    {
        if (selectionIndex == -1)
        {
            return new SubtitlePreferenceUpdate { SubtitleLanguage = string.Empty, SubtitleTrackIndex = -1, SubtitleMode = "off" };
        }

        var tracks = version.SubtitleTracks!;
        var ordinal = PlaybackFormatting.CombinedSubtitleSelectionIndexes(tracks).IndexOf(selectionIndex);
        var track = tracks[ordinal];
        return new SubtitlePreferenceUpdate
        {
            SubtitleLanguage = ServerLanguageTag(track.Language) ?? string.Empty,
            SubtitleTrackIndex = selectionIndex,
            SubtitleMode = "always",
            TrackSignature = new SubtitleTrackSignature
            {
                Source = track.External == true ? "external" : "embedded", Language = Clean(track.Language),
                Codec = Clean(track.Codec), Label = Clean(track.Title), Forced = track.Forced == true,
                HearingImpaired = track.HearingImpaired == true || HearingImpairedTitle().IsMatch(track.Title ?? string.Empty),
            },
            // Do not pin ShowForcedSubtitles: omitted preserves the profile/series setting.
        };
    }

    private static string? ServerLanguageTag(string? language)
    {
        var value = Clean(language);
        if (value is null || value.Equals("und", StringComparison.OrdinalIgnoreCase)) return null;
        if (LanguageTag().IsMatch(value)) return value;
        // Older catalog scans used English names. Match Android's legacy LanguageOptions migration.
        return CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(culture => culture.EnglishName.Equals(value, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [GeneratedRegex("^[A-Za-z]{2,3}(?:[-_][0-9A-Za-z]{1,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTag();

    [GeneratedRegex(@"(^|[^a-z0-9])(cc|sdh)([^a-z0-9]|$)|closed caption|hearing[ -]impaired", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HearingImpairedTitle();
}

public sealed partial class DetailFeed
{
    private readonly SemaphoreSlim _trackChoiceWrites = new(1, 1);

    /// <summary>
    /// Explicit detail picks persist across devices even without a player (TvItemDetailViewModel.kt).
    /// Auto deletes the series/movie choice. Version/Edition selections never write track preferences.
    /// Serialize writes so rapid picks cannot leave an older choice on the server.
    /// </summary>
    public async Task SaveTrackChoiceAsync(ItemDetail detail, FileVersion version, PlaybackSelectorKind kind,
        int? trackIndex, CancellationToken cancellationToken = default)
    {
        if (kind is not (PlaybackSelectorKind.Audio or PlaybackSelectorKind.Subtitles) || detail.Type is not ("movie" or "episode")) return;
        var ownerId = PlaybackTrackChoices.OwnerId(detail);
        await _trackChoiceWrites.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (kind == PlaybackSelectorKind.Audio)
            {
                if (trackIndex is { } ordinal)
                    await _api.Api.V2.AudioPrefs[ownerId].PutAsync(PlaybackTrackChoices.Audio(version, ordinal), cancellationToken: cancellationToken).ConfigureAwait(false);
                else
                    await _api.Api.V2.AudioPrefs[ownerId].DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (trackIndex is { } index)
                    await _api.Api.V2.SubtitlePrefs[ownerId].PutAsync(PlaybackTrackChoices.Subtitle(version, index), cancellationToken: cancellationToken).ConfigureAwait(false);
                else
                    await _api.Api.V2.SubtitlePrefs[ownerId].DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Other episode details can contain this series' old effective preference, even after
            // an uncertain write. Do not let the preload cache resurrect it on the next episode.
            lock (_cacheLock) _cache.Clear();
            _trackChoiceWrites.Release();
        }
    }
}
