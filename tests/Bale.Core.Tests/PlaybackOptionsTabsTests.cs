using System.Xml.Linq;
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

    [Fact]
    public void PanelShowsTheTabsInTvOrderAndOwnsItsDpad()
    {
        var page = XDocument.Parse(Read("PlayerPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => page.Descendants().Single(element => element.Attribute(x + "Name")?.Value == name);
        var tabs = Named("OptionsTabs").Elements().ToList();
        Assert.Equal(["Info", "Stats", "Video", "Audio", "Subtitles", "Chapters"], tabs.Select(tab => tab.Attribute("Content")?.Value));
        Assert.All(tabs, tab =>
        {
            Assert.Equal("{StaticResource SkylineChipStyle}", tab.Attribute("Style")?.Value);
            Assert.Equal("OnOptionsTabFocus", tab.Attribute("GotFocus")?.Value);
        });
        var panel = Named("OptionsPanel");
        Assert.Equal("736", panel.Attribute("Width")?.Value);
        Assert.Equal("OnOptionsKeyDown", panel.Attribute("PreviewKeyDown")?.Value);
        foreach (var pane in new[] { "InfoPane", "StatsPane", "VideoPane", "AudioPane", "SubtitlesPane", "ChaptersPane" })
            Assert.Contains(Named(pane), panel.Descendants());

        var code = Read("PlayerPage.Options.cs.txt");
        Assert.Contains("PlaybackOptionsTabs.Visible(_optionsStatsRows.Count > 0", code);
        Assert.Contains("RefreshHudStats(attempt, sampleBandwidth: false).OfficialHudRows()", code);
        // Picks take the control bar's path (local audio switch or replan) and leave the panel open.
        Assert.Contains("ChangeTracksAsync(trackIndex, null) : ChangeTracksAsync(null, trackIndex)", code);
        Assert.Contains("PlaybackOptionsTabs.MoveFromRow(index, rows.Count, down)", code);
        Assert.Contains("(Style)Application.Current.Resources[\"SkylinePanelRowStyle\"]", code);
        Assert.Contains("UpdateOptionsStats();", Read("PlayerPage.cs.txt"));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
