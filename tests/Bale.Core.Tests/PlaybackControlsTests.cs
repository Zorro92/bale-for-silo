using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackControlsTests
{
    [Fact]
    public void ControlsHideAfterFiveSecondsWithoutInputButNotWhileScrubbing()
    {
        var controls = new PlaybackControlsState();
        Assert.False(controls.ShouldHide(10_000, false));
        controls.Show(1_000);
        Assert.False(controls.ShouldHide(5_999, false));
        controls.Input(4_000);
        Assert.False(controls.ShouldHide(8_999, false));
        Assert.True(controls.ShouldHide(9_000, false));
        Assert.False(controls.ShouldHide(20_000, scrubbing: true));
        controls.Hide();
        controls.Input(30_000);
        Assert.False(controls.Visible);
    }

    [Fact]
    public void ScrubPreviewMovesInStepsWithinTheTitleAndCommitsOnce()
    {
        var scrub = new PlaybackScrub();
        Assert.Equal(110, scrub.Nudge(100, 1000, forward: true));
        Assert.Equal(120, scrub.Nudge(105, 1000, forward: true));
        Assert.Equal(110, scrub.Nudge(105, 1000, forward: false));
        Assert.Equal(110, scrub.Commit());
        Assert.Null(scrub.Commit());
        Assert.Equal(0, scrub.Nudge(4, null, forward: false));
        scrub.Cancel();
        Assert.False(scrub.Active);
        Assert.Equal(1000, scrub.Nudge(995, 1000, forward: true));
    }

    [Fact]
    public void EyebrowAndTitleFollowTheOfficialPlayer()
    {
        var episode = Detail("episode", "Severance", "Hello, Ms. Cobel") with { SeasonNumber = 2, EpisodeNumber = 4 };
        Assert.Equal("SEVERANCE · S2:E4", PlaybackControlsText.Eyebrow(episode));
        Assert.Equal("Hello, Ms. Cobel", PlaybackControlsText.Title(episode));
        var movie = Detail("movie", "Film", null) with { Year = 2026 };
        Assert.Equal("MOVIE · 2026", PlaybackControlsText.Eyebrow(movie));
        Assert.Equal("Film", PlaybackControlsText.Title(movie));
        Assert.Null(PlaybackControlsText.Eyebrow(Detail("other", "X", null)));
    }

    [Fact]
    public void AHeldTriggerScansSteadilyAfterTheDelayAndATapDoesNot()
    {
        var scan = new PlaybackHoldScan(delayMs: 1000, rate: 60);
        scan.Press(forward: true, nowMs: 0);
        Assert.Null(scan.Tick(900, 130, 1000));
        Assert.Equal(130, scan.Tick(1000, 130, 1000));
        Assert.Equal(136, scan.Tick(1100, 999, 1000));
        Assert.Equal(160, scan.Tick(1500, 999, 1000));
        Assert.Equal(160, scan.Release());
        Assert.False(scan.Held);

        scan.Press(forward: false, nowMs: 5000);
        Assert.Null(scan.Tick(5900, 50, 1000));
        Assert.Null(scan.Release());

        scan.Press(forward: false, nowMs: 0);
        scan.Tick(1000, 20, 1000);
        Assert.Equal(0, scan.Tick(2000, 20, 1000));
        scan.Press(forward: true, nowMs: 0);
        scan.Tick(1000, 990, 1000);
        Assert.Equal(1000, scan.Tick(2000, 990, 1000));
    }

    [Fact]
    public void TheDefaultHoldTellsATapFromAHoldWithinAQuarterSecond()
    {
        var scan = new PlaybackHoldScan();
        scan.Press(forward: true, nowMs: 0);
        Assert.Null(scan.Tick(200, 100, 1000));
        Assert.Equal(100, scan.Tick(250, 100, 1000));
        Assert.Equal(160, scan.Tick(1250, 999, 1000));
    }

    private static ItemDetail Detail(string type, string title, string? subtitle) =>
        new("id", type, title, subtitle, null, null, null, null, [], null, [], [], null, [], null, "id", null, null, new(false, false, false), []);
}
