using Bale.Core.Detail;

namespace Bale.Core.Playback;

/// <summary>The Options panel's tabs in the TV apps' order (TvPlayerHud.kt HudTab, TVPlayerInfoHUD.swift Tab).</summary>
public enum PlaybackOptionsTab { Info, Stats, Video, Audio, Subtitles, Chapters }

/// <summary>A track row in the Audio or Subtitles tab: name, attributes, whether it plays now, and the pick it sends.</summary>
public sealed record PlaybackTrackRow(string Name, string? Attributes, bool IsSelected, int TrackIndex);

/// <summary>
/// Which Options tabs show, the Audio / Subtitles track rows, and D-pad moves inside the panel.
/// </summary>
public static class PlaybackOptionsTabs
{
    /// <summary>
    /// TvPlayerHud.kt visibleHudTabs / TVPlayerInfoHUD.swift availableTabs: Info and Video always; Stats while it
    /// has rows; Audio with any audio track; Subtitles with any subtitle track (tvOS: Android keeps it for its
    /// search / AI rows, which Bale does not have); Chapters with chapters. Empty tabs are hidden, not disabled.
    /// </summary>
    public static IReadOnlyList<PlaybackOptionsTab> Visible(bool hasStats, int audioTracks, int subtitleTracks, int chapters)
    {
        var tabs = new List<PlaybackOptionsTab> { PlaybackOptionsTab.Info };
        if (hasStats) tabs.Add(PlaybackOptionsTab.Stats);
        tabs.Add(PlaybackOptionsTab.Video);
        if (audioTracks > 0) tabs.Add(PlaybackOptionsTab.Audio);
        if (subtitleTracks > 0) tabs.Add(PlaybackOptionsTab.Subtitles);
        if (chapters > 0) tabs.Add(PlaybackOptionsTab.Chapters);
        return tabs;
    }

    /// <summary>
    /// The player's track list (PlayerPage TrackOptions: catalog tracks, plus Off for subtitles) as pane rows.
    /// Off carries no attributes (TVPlayerInfoHUD.swift SubtitlesPane); Auto rows never appear in a running player.
    /// </summary>
    public static IReadOnlyList<PlaybackTrackRow> TrackRows(IReadOnlyList<PlaybackSelectorOption> options) => options
        .Where(option => option.TrackIndex is not null)
        .Select(option => new PlaybackTrackRow(option.Title,
            option.TrackIndex < 0 || string.IsNullOrWhiteSpace(option.Detail) ? null : option.Detail,
            option.IsSelected, option.TrackIndex!.Value))
        .ToList();

    /// <summary>The number of real tracks in <paramref name="rows"/> (Off is not a track).</summary>
    public static int TrackCount(IReadOnlyList<PlaybackTrackRow> rows) => rows.Count(row => row.TrackIndex >= 0);

    /// <summary>
    /// The row Down from the tab bar lands on: the checked track (TvPlayerHud.kt HudSubtitlesPane entryRow), else
    /// the first row; -1 without rows.
    /// </summary>
    public static int EntryRow(IReadOnlyList<bool> selected)
    {
        for (var i = 0; i < selected.Count; i++)
        {
            if (selected[i]) return i;
        }

        return selected.Count > 0 ? 0 : -1;
    }

    /// <summary>The tab Left/Right moves to along the bar, stopping at its ends (no wrap); null to stay put.</summary>
    public static PlaybackOptionsTab? NextTab(IReadOnlyList<PlaybackOptionsTab> visible, PlaybackOptionsTab current, bool forward)
    {
        var index = -1;
        for (var i = 0; i < visible.Count; i++)
        {
            if (visible[i] == current) index = i;
        }

        var next = index + (forward ? 1 : -1);
        return index >= 0 && next >= 0 && next < visible.Count ? visible[next] : null;
    }

    /// <summary>
    /// Up/Down from a pane row (<paramref name="row"/> 0..count-1): the next row, the tab bar (-1) Up from the
    /// first row, or null to stay put Down from the last. Panes are one column, so Left/Right never leave a row
    /// (owner rule: vertical moves never turn sideways).
    /// </summary>
    public static int? MoveFromRow(int row, int count, bool down)
    {
        if (row < 0 || row >= count) return null;
        if (!down) return row - 1;
        return row + 1 < count ? row + 1 : null;
    }
}
