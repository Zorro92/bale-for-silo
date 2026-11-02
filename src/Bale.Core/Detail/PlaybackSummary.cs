namespace Bale.Core.Detail;

/// <summary>The three values in TvDetailPlaybackSelectionSummary; null renders a skeleton, not an empty label.</summary>
public sealed record PlaybackSummaryValues(string? Version, string? Audio, string? Subtitles)
{
    public static PlaybackSummaryValues Empty { get; } = new(null, null, null);

    public static PlaybackSummaryValues For(ItemDetail? playbackDetail, string? selectedFileId = null,
        int? selectedAudioTrackIndex = null, int? selectedSubtitleTrackIndex = null, string? preferredQuality = null,
        bool automaticAudioResolutionKnown = false, int? automaticAudioTrackOrdinal = null,
        SubtitleAutoContext? subtitleAutoContext = null)
    {
        if (playbackDetail is null) return Empty;
        selectedFileId = playbackDetail.Versions.Any(version => version.FileId == selectedFileId) ? selectedFileId : null;
        var version = PlaybackFormatting.SelectDisplayVersion(playbackDetail.Versions, selectedFileId, playbackDetail.LastFileId, preferredQuality);
        if (version is null) return Empty;
        var compact = PlaybackFormatting.VersionCompactLabel(version);
        var versionValue = selectedFileId is null && compact != "Auto" ? "Auto · " + compact : compact;
        // TvItemDetailViewModel.kt's automaticAudioOrdinal gives a server-resolved series choice precedence.
        if (version.EffectiveAudioTrackIndex is >= 0 and var effective && effective < (version.AudioTracks?.Count ?? 0))
        {
            automaticAudioTrackOrdinal = (int)effective;
        }

        var audioValue = selectedAudioTrackIndex is null && !automaticAudioResolutionKnown ? "Auto"
            : PlaybackFormatting.AudioValueLabel(version, selectedAudioTrackIndex, automaticAudioTrackOrdinal).Replace("Auto: ", "Auto · ");
        SubtitleAutoContext? context = null;
        if (selectedAudioTrackIndex is not null || automaticAudioResolutionKnown)
        {
            context = new SubtitleAutoContext(
                NonBlank(playbackDetail.EffectiveSubtitleLanguage) ?? subtitleAutoContext?.PreferredLanguage,
                NonBlank(playbackDetail.EffectiveSubtitleMode) ?? subtitleAutoContext?.Mode,
                playbackDetail.EffectiveShowForcedSubtitles ?? subtitleAutoContext?.ShowForced ?? false,
                PlaybackFormatting.ResolvedAudioLanguage(version, selectedAudioTrackIndex, automaticAudioTrackOrdinal),
                playbackDetail.EffectiveSubtitleTrackSignature ?? subtitleAutoContext?.TrackSignature);
        }

        var subtitleValue = PlaybackFormatting.SubtitleValueLabel(version, selectedSubtitleTrackIndex, context).Replace("Auto - ", "Auto · ");
        return new PlaybackSummaryValues(versionValue, audioValue, subtitleValue);
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Presentation retention from TvItemDetailScreen.kt. Series/seasons use only the identity-checked
/// Play episode's detail; the last settled values remain visible while that target or its rail loads.
/// </summary>
public sealed class PlaybackSummaryState
{
    private string? _contentId;
    private PlaybackSummaryValues? _previous;

    public PlaybackSummaryValues Update(ItemDetail detail, string? nextUpTargetId = null,
        ItemDetail? nextUpPlaybackDetail = null, bool transitioning = false,
        string? selectedFileId = null, int? selectedAudioTrackIndex = null, int? selectedSubtitleTrackIndex = null)
    {
        if (_contentId != detail.ContentId)
        {
            _contentId = detail.ContentId;
            _previous = null;
        }

        var playbackDetail = detail.PlaysEpisodes
            ? nextUpTargetId is not null && nextUpPlaybackDetail?.ContentId == nextUpTargetId ? nextUpPlaybackDetail : null
            : detail;
        var resolved = PlaybackSummaryValues.For(playbackDetail, selectedFileId, selectedAudioTrackIndex, selectedSubtitleTrackIndex);
        if (detail.PlaysEpisodes && transitioning) return _previous ?? resolved;
        _previous = resolved;
        return resolved;
    }
}
