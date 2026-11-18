using System.Globalization;
using Bale.Core.Api.Models;

namespace Bale.Core.Detail;

public enum PlaybackSelectorKind { Version, Audio, Subtitles, Edition }

/// <summary>
/// Menu identity is independent of display order (TvPlaybackSelectorRow.kt). A disabled option is listed dimmed,
/// out of focus and Select (TvAnchoredSelectorMenu.kt <c>enabled</c>).
/// </summary>
public sealed record PlaybackSelectorOption(string Title, string Detail, bool IsSelected,
    string? FileId = null, int? TrackIndex = null, string? EditionId = null, bool IsEnabled = true);

public static partial class PlaybackFormatting
{
    /// <summary>
    /// Version/Audio/Subtitles remain focusable even when non-interactive; Edition appears only
    /// with distinct edition choices. Version counts edition-scoped files, not the global Auto row.
    /// Matches TvPlaybackSelectorRow.kt's TvPlaybackActionSelectors, not its older interactivity comment.
    /// </summary>
    public static bool SelectorIsInteractive(PlaybackSelectorKind kind, ItemDetail? detail, PlaybackSelection selection)
    {
        if (detail is null || SelectDisplayVersion(detail, selection) is not { } version) return false;
        if (kind is PlaybackSelectorKind.Edition or PlaybackSelectorKind.Version)
        {
            var editions = PlaybackEditions.For(detail.Versions, detail.PlaybackVariants);
            return kind == PlaybackSelectorKind.Edition ? PlaybackEditions.HasChoices(editions)
                : PlaybackEditions.ScopedVersions(detail.Versions, editions, version).Count > 1;
        }

        return kind != PlaybackSelectorKind.Audio || version.AudioTracks is { Count: > 0 };
    }

    public static IReadOnlyList<PlaybackSelectorOption> SelectorOptions(PlaybackSelectorKind kind,
        ItemDetail detail, PlaybackSelection selection)
    {
        var version = SelectDisplayVersion(detail, selection);
        return kind switch
        {
            PlaybackSelectorKind.Edition => EditionOptions(detail, version),
            PlaybackSelectorKind.Version => VersionOptions(detail, selection.FileId, version),
            PlaybackSelectorKind.Audio => new[]
            {
                new PlaybackSelectorOption("Auto", "Use your Playback audio preference", selection.AudioIndex is null),
            }.Concat((version?.AudioTracks ?? []).Select((track, ordinal) => new PlaybackSelectorOption(
                AudioTitle(track, ordinal), AudioDetail(track, ordinal, version!), selection.AudioIndex == ordinal, TrackIndex: ordinal))).ToList(),
            _ => SubtitleOptions(version, selection.SubtitleIndex, detail.EffectiveSubtitleLanguage),
        };
    }

    private static IReadOnlyList<PlaybackSelectorOption> EditionOptions(ItemDetail detail, FileVersion? version)
    {
        var editions = PlaybackEditions.For(detail.Versions, detail.PlaybackVariants);
        var current = PlaybackEditions.Current(editions, version);
        // TvPlaybackSelectorRow.kt: each row's detail is the file Version Auto picks in that edition.
        return editions.Select(edition => new PlaybackSelectorOption(edition.Label,
            SelectDisplayVersion(detail, selectedFileId: null, editionId: edition.Id) is { } auto ? VersionShortLabel(auto) : string.Empty,
            edition.Id == current?.Id, EditionId: edition.Id)).ToList();
    }

    private static IReadOnlyList<PlaybackSelectorOption> VersionOptions(ItemDetail detail, string? selectedFileId, FileVersion? version)
    {
        var editions = PlaybackEditions.For(detail.Versions, detail.PlaybackVariants);
        // TvPlaybackSelectorRow.kt: Auto picks inside the current edition, so it is always offered.
        var options = new List<PlaybackSelectorOption> { new("Auto", "Best match for this device", selectedFileId is null) };
        options.AddRange(PlaybackEditions.ScopedVersions(detail.Versions, editions, version).Select(file => new PlaybackSelectorOption(
            VersionShortLabel(file), VersionDetailLabel(file), file.FileId == selectedFileId, FileId: file.FileId)));
        return options;
    }

    public static string VersionShortLabel(FileVersion version)
    {
        var audioOrdinal = ResolvedAudioOrdinal(version, null, null);
        var audioCodec = audioOrdinal is { } ordinal ? version.AudioTracks![ordinal].Codec : null;
        return JoinTokens([
            DisplayResolution(version.Resolution),
            NormalizedVideoCodec(NonEmpty(version.CodecVideo) ?? version.VideoTracks?.Select(track => NonEmpty(track.Codec)).FirstOrDefault(codec => codec is not null)),
            IsDolbyVision(version) ? "DV" : IsHdr(version) ? "HDR" : null,
            NormalizedAudioCodec(audioCodec ?? version.CodecAudio),
        ]) is { Length: > 0 } label ? label : "Auto";
    }

    public static string VersionDetailLabel(FileVersion version) => JoinTokens([
        NormalizedVideoCodec(version.CodecVideo), NonEmpty(version.Container)?.ToUpperInvariant(), FileSizeLabel(version.FileSize),
    ]);

    private static string? NormalizedVideoCodec(string? codec) => NonEmpty(codec)?.ToLowerInvariant() switch
    {
        null => null,
        var value when value.Contains("hevc") || value.Contains("h265") => "HEVC",
        var value when value.Contains("av1") => "AV1",
        var value when value.Contains("avc") || value.Contains("h264") => "H.264",
        var value => value.ToUpperInvariant(),
    };

    private static string? FileSizeLabel(long? bytes) => bytes switch
    {
        null or <= 0 => null,
        >= 1_000_000_000 => (bytes.Value / 1_000_000_000d).ToString("F2", CultureInfo.InvariantCulture) + " GB",
        _ => (bytes.Value / 1_000_000d).ToString("F1", CultureInfo.InvariantCulture) + " MB",
    };

    private static string AudioTitle(AudioTrack track, int ordinal) =>
        LanguageDisplayName(track.Language) ?? UsefulAudioTitle(track.Title) ?? $"Track {ordinal + 1}";

    private static string AudioDetail(AudioTrack track, int ordinal, FileVersion version) => JoinTokens([
        UsefulAudioTitle(track.Title) is { } title && title != AudioTitle(track, ordinal) ? title : null,
        NormalizedAudioCodec(track.Codec), CompactAudioLayout(track), track.Default == true ? "Default" : null,
        version.EffectiveAudioTrackIndex == ordinal ? "Preferred" : null,
    ]);

    private static IReadOnlyList<PlaybackSelectorOption> SubtitleOptions(FileVersion? version, int? selected, string? preferredLanguage)
    {
        var tracks = version?.SubtitleTracks ?? [];
        var combined = CombinedSubtitleSelectionIndexes(tracks);
        var preferred = SubtitleLanguageKey(preferredLanguage);
        // TvPlaybackFormatting.kt: preferred language, alphabetical language groups (unknown last),
        // text format, full/forced/SDH, Default, then stable catalog order. Never sort the identities.
        var ordinals = Enumerable.Range(0, tracks.Count)
            .OrderBy(i => SubtitleLanguageKey(tracks[i].Language) is null ? 2 : SubtitleLanguageKey(tracks[i].Language) == preferred ? 0 : 1)
            .ThenBy(i => LanguageDisplayName(SubtitleLanguageKey(tracks[i].Language)), StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => SubtitleLanguageKey(tracks[i].Language), StringComparer.Ordinal)
            .ThenBy(i => SubtitleFormatRank(tracks[i].Codec))
            .ThenBy(i => LabelIndicatesHearingImpaired(tracks[i].Title) ? 2 : tracks[i].Forced == true ? 1 : 0)
            .ThenBy(i => tracks[i].Default == true ? 0 : 1).ThenBy(i => i);
        return new[]
        {
            new PlaybackSelectorOption("Auto", "Use your subtitle preferences", selected is null),
            new PlaybackSelectorOption("Off", "Start without subtitles", selected == -1, TrackIndex: -1),
        }.Concat(ordinals.Select(i => new PlaybackSelectorOption(
            LanguageDisplayName(tracks[i].Language) ?? MeaningfulSubtitleTitle(tracks[i]) ?? $"Track {i + 1}",
            IsUnplayableSubtitle(tracks[i]) ? UnsupportedSubtitleFormat : SubtitleDetail(tracks[i]),
            selected == combined[i], TrackIndex: combined[i], IsEnabled: !IsUnplayableSubtitle(tracks[i])))).ToList();
    }

    /// <summary>strings.xml subtitle_track_unsupported_format (Android 461240c6d).</summary>
    public const string UnsupportedSubtitleFormat = "Unsupported format";

    /// <summary>
    /// PlaybackSubtitleChoices.kt isUnplayableSubtitle (Android 461240c6d): an embedded track with no codec is one
    /// the server's probe did not recognise (a Matroska S_TEXT/WEBVTT track, for one), so no route can send or
    /// burn it in and a start that names it is refused. Only embedded tracks: a downloaded one can arrive without
    /// a codec and still play. Listed disabled, never auto-picked; the row stays so combined indexes keep their places.
    /// </summary>
    public static bool IsUnplayableSubtitle(VersionSubtitleTrack track) => track.External != true && string.IsNullOrWhiteSpace(track.Codec);

    private static bool IsForcedSubtitle(VersionSubtitleTrack track) => track.Forced == true || Contains(track.Title, "forced");

    private static string SubtitleDetail(VersionSubtitleTrack track) => JoinTokens([
        MeaningfulSubtitleTitle(track), NormalizedSubtitleCodec(track.Codec), IsForcedSubtitle(track) ? "Forced" : null,
        LabelIndicatesHearingImpaired(track.Title) ? "SDH" : null, track.Default == true ? "Default" : null,
        track.External == true ? "External" : null,
    ]);

    private static int SubtitleFormatRank(string? codec) => NonEmpty(codec)?.ToLowerInvariant() switch
    {
        var value when value == "srt" || value?.Contains("subrip") == true => 0,
        var value when value?.Contains("ass") == true => 1,
        var value when value?.Contains("ssa") == true => 2,
        var value when value == "vtt" || value?.Contains("webvtt") == true => 3,
        var value when value?.Contains("mov_text") == true || value?.Contains("movtext") == true || value?.Contains("tx3g") == true => 4,
        var value when value?.Contains("pgs") == true || value?.Contains("hdmv") == true => 5,
        var value when value?.Contains("dvd") == true || value?.Contains("vobsub") == true || value?.Contains("dvb") == true => 6,
        _ => 7,
    };

    private static string JoinTokens(IEnumerable<string?> tokens) => string.Join(" · ", tokens.Where(token => !string.IsNullOrWhiteSpace(token)));
}
