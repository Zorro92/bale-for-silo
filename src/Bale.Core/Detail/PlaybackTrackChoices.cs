using System.Globalization;
using System.Text.RegularExpressions;
using Bale.Core.Api.Models;
using Bale.Core.Playback;

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
        if (selectionIndex == -1) return SubtitleOff();

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

    /// <summary>SeriesSubtitleChoice.Off: subtitles off for the series or movie, the web and Apple spelling.</summary>
    public static SubtitlePreferenceUpdate SubtitleOff() =>
        new() { SubtitleLanguage = string.Empty, SubtitleTrackIndex = -1, SubtitleMode = "off" };

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
        await WriteTrackChoiceAsync(async token =>
        {
            if (kind == PlaybackSelectorKind.Audio)
            {
                if (trackIndex is { } ordinal)
                    await _api.Api.V2.AudioPrefs[ownerId].PutAsync(PlaybackTrackChoices.Audio(version, ordinal), cancellationToken: token).ConfigureAwait(false);
                else
                    await _api.Api.V2.AudioPrefs[ownerId].DeleteAsync(cancellationToken: token).ConfigureAwait(false);
            }
            else
            {
                if (trackIndex is { } index)
                    await _api.Api.V2.SubtitlePrefs[ownerId].PutAsync(PlaybackTrackChoices.Subtitle(version, index), cancellationToken: token).ConfigureAwait(false);
                else
                    await _api.Api.V2.SubtitlePrefs[ownerId].DeleteAsync(cancellationToken: token).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A track picked in the player, once a plan plays it (<see cref="PlayerTrackPreferences"/>;
    /// TvPlayerViewModel.kt saveSeriesAudio / saveSeriesSubtitle): the same PUT a Detail pick sends, in the same line, so
    /// neither overtakes the other.
    /// </summary>
    public Task SaveTrackPreferenceAsync(PlayerTrackPreferenceWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (write.Audio is { } audio)
            return WriteTrackChoiceAsync(token => _api.Api.V2.AudioPrefs[write.OwnerId].PutAsync(audio, cancellationToken: token), cancellationToken);
        if (write.Subtitle is not { } subtitle) throw new ArgumentException("The write has no body.", nameof(write));
        return WriteTrackChoiceAsync(token => _api.Api.V2.SubtitlePrefs[write.OwnerId].PutAsync(subtitle, cancellationToken: token), cancellationToken);
    }

    /// <summary>SeriesTrackChoiceWriter.kt LOAD_FLUSH_TIMEOUT_MS: how long a read waits for the writes on their way.</summary>
    public static TimeSpan TrackChoiceSettleTimeout { get; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Waits, at most <paramref name="timeout"/>, for the track preference writes already on their way (tvOS
    /// SiloAPI.watchDetail settlePendingWrites; Android's 1.5 s flush before a load, SeriesTrackChoiceWriter.kt reconcile),
    /// so a detail read or a start after it gets the server's answer to them. A write still out after that is not waited for.
    /// </summary>
    public async Task WhenTrackChoicesSavedAsync(TimeSpan timeout)
    {
        if (await _trackChoiceWrites.WaitAsync(timeout).ConfigureAwait(false)) _trackChoiceWrites.Release();
    }

    /// <summary>One track preference write, in the line every write takes; the detail cache it made stale goes with it.</summary>
    private async Task WriteTrackChoiceAsync(Func<CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        await _trackChoiceWrites.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await write(cancellationToken).ConfigureAwait(false);
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
