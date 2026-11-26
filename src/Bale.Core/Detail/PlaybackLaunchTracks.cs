namespace Bale.Core.Detail;

/// <summary>
/// The tracks a start on Auto sends when no Detail page chose them (Up Next's next episode). It uses Detail's rule
/// (DetailPage.Player.cs, TvItemDetailScreen.kt subtitleLaunchSelection): the subtitle Auto would show, from the
/// series' saved choice first and the profile's subtitle settings after. A V3 start without a subtitle index means Off,
/// so leaving it unresolved dropped a saved subtitle preference on every Up Next advance. Audio stays omitted: the
/// server resolves an omitted audio track from the saved preference.
/// </summary>
public static class PlaybackLaunchTracks
{
    public static PlaybackSelection ForAuto(ItemDetail detail, string? preferredAudioLanguage,
        ClientAudioCapabilities? capabilities, SubtitleAutoContext? profileSubtitles)
    {
        var selection = PlaybackSelection.Auto;
        var version = PlaybackFormatting.SelectDisplayVersion(detail, selection);
        var automaticAudio = PlaybackAudioSelection.AutomaticAudioOrdinal(version, preferredAudioLanguage, capabilities);
        return selection with
        {
            SubtitleIndex = PlaybackSummaryValues.SubtitleLaunchIndex(detail, automaticAudioResolutionKnown: capabilities is not null,
                automaticAudioTrackOrdinal: automaticAudio, subtitleAutoContext: profileSubtitles),
        };
    }
}
