using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackOptionsTabsTests
{
    [Fact]
    public void TabsKeepTheTvOrderAndHideEmptyOnes()
    {
        Assert.Equal(
            [PlaybackOptionsTab.Info, PlaybackOptionsTab.Stats, PlaybackOptionsTab.Video, PlaybackOptionsTab.Audio,
                PlaybackOptionsTab.Subtitles, PlaybackOptionsTab.Chapters],
            PlaybackOptionsTabs.Visible(hasStats: true, audioTracks: 2, subtitleTracks: 3, chapters: 7));
        Assert.Equal([PlaybackOptionsTab.Info, PlaybackOptionsTab.Video],
            PlaybackOptionsTabs.Visible(hasStats: false, audioTracks: 0, subtitleTracks: 0, chapters: 0));
        // One audio track still shows the tab (visibleHudTabs: audioTracks.isNotEmpty()).
        Assert.Equal([PlaybackOptionsTab.Info, PlaybackOptionsTab.Video, PlaybackOptionsTab.Audio],
            PlaybackOptionsTabs.Visible(hasStats: false, audioTracks: 1, subtitleTracks: 0, chapters: 0));
    }

    [Fact]
    public void TrackRowsDropAutoAndGiveOffNoAttributes()
    {
        var rows = PlaybackOptionsTabs.TrackRows(
        [
            new PlaybackSelectorOption("Auto", "Use your subtitle preferences", false),
            new PlaybackSelectorOption("Off", "No subtitles", false, TrackIndex: -1),
            new PlaybackSelectorOption("English", "SRT · External", true, TrackIndex: 4),
            new PlaybackSelectorOption("French", " ", false, TrackIndex: 2),
        ]);
        Assert.Equal(
        [
            new PlaybackTrackRow("Off", null, false, -1),
            new PlaybackTrackRow("English", "SRT · External", true, 4),
            new PlaybackTrackRow("French", null, false, 2),
        ], rows);
        Assert.Equal(2, PlaybackOptionsTabs.TrackCount(rows));
        Assert.Equal(0, PlaybackOptionsTabs.TrackCount(PlaybackOptionsTabs.TrackRows(
            [new PlaybackSelectorOption("Off", "No subtitles", true, TrackIndex: -1)])));
    }

    [Fact]
    public void DownFromTheTabsLandsOnTheCheckedRow()
    {
        Assert.Equal(2, PlaybackOptionsTabs.EntryRow([false, false, true]));
        Assert.Equal(0, PlaybackOptionsTabs.EntryRow([false, false]));
        Assert.Equal(-1, PlaybackOptionsTabs.EntryRow([]));
    }

    [Fact]
    public void TabsMoveAlongTheBarAndStopAtItsEnds()
    {
        var tabs = PlaybackOptionsTabs.Visible(hasStats: true, audioTracks: 1, subtitleTracks: 0, chapters: 0);
        Assert.Equal(PlaybackOptionsTab.Stats, PlaybackOptionsTabs.NextTab(tabs, PlaybackOptionsTab.Info, forward: true));
        Assert.Equal(PlaybackOptionsTab.Video, PlaybackOptionsTabs.NextTab(tabs, PlaybackOptionsTab.Audio, forward: false));
        Assert.Null(PlaybackOptionsTabs.NextTab(tabs, PlaybackOptionsTab.Info, forward: false));
        Assert.Null(PlaybackOptionsTabs.NextTab(tabs, PlaybackOptionsTab.Audio, forward: true));
        Assert.Null(PlaybackOptionsTabs.NextTab(tabs, PlaybackOptionsTab.Chapters, forward: true));
    }

    [Fact]
    public void RowsMoveVerticallyAndStayPutAtTheBottom()
    {
        Assert.Equal(1, PlaybackOptionsTabs.MoveFromRow(0, 3, down: true));
        Assert.Equal(1, PlaybackOptionsTabs.MoveFromRow(2, 3, down: false));
        // Up from the first row goes to the tab bar; Down from the last stays put.
        Assert.Equal(-1, PlaybackOptionsTabs.MoveFromRow(0, 3, down: false));
        Assert.Null(PlaybackOptionsTabs.MoveFromRow(2, 3, down: true));
        Assert.Null(PlaybackOptionsTabs.MoveFromRow(0, 0, down: true));
    }
}
