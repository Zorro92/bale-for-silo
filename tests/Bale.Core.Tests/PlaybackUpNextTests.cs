using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackUpNextTests
{
    [Fact]
    public void NextIsTheNextNumberInTheSeasonElseTheNextNumberedSeason()
    {
        EpisodeCard[] season = [Episode(1, 3), Episode(1, 1), Episode(1, 2)];
        Assert.Equal(2, PlaybackUpNext.NextInSeason(season, 1)!.EpisodeNumber);
        Assert.Null(PlaybackUpNext.NextInSeason(season, 3));
        Assert.Equal(1, PlaybackUpNext.First(season)!.EpisodeNumber);
        SeasonChoice[] seasons = [Season(0, special: true), Season(1), Season(2), Season(4)];
        Assert.Equal(2, PlaybackUpNext.NextSeason(seasons, 1));
        Assert.Equal(4, PlaybackUpNext.NextSeason(seasons, 2));
        Assert.Null(PlaybackUpNext.NextSeason(seasons, 4));
        // The last special continues into S1E1; a regular season never continues into Specials.
        Assert.Equal(1, PlaybackUpNext.NextSeason(seasons, 0));
        Assert.Null(PlaybackUpNext.NextSeason([Season(0, special: true)], 0));
    }

    // NextUpPromptTest.kt (Android e580e1811): a 48:12 episode whose credits marker starts at 46:31; the marker must
    // not move the prompt (silo-android#910).
    private const double EpisodeLength = 2892.0;

    [Fact]
    public void ThePromptOpensWhenPlaybackCrossesTheChosenSecondsBeforeTheEnd()
    {
        Assert.True(NextUpPrompt.CrossesPromptPoint(2861.6, 2862.1, EpisodeLength, promptSeconds: 30));
        Assert.True(NextUpPrompt.CrossesPromptPoint(2881.5, 2882.0, EpisodeLength, promptSeconds: 10));
        Assert.False(NextUpPrompt.CrossesPromptPoint(2791.0, 2792.0, EpisodeLength, promptSeconds: 30));
    }

    [Fact]
    public void PlaybackAlreadyPastThePointDoesNotReopenThePrompt() =>
        Assert.False(NextUpPrompt.CrossesPromptPoint(2870.0, 2870.5, EpisodeLength, promptSeconds: 30));

    [Fact]
    public void AtEndAndUnknownDurationNeverOpenBeforeTheStreamEnds()
    {
        Assert.False(NextUpPrompt.CrossesPromptPoint(2891.0, 2892.0, EpisodeLength, promptSeconds: 0));
        Assert.False(NextUpPrompt.CrossesPromptPoint(10.0, 11.0, 0.0, promptSeconds: 30));
        Assert.False(NextUpPrompt.CrossesPromptPoint(10.0, 11.0, null, promptSeconds: 30));
        Assert.False(NextUpPrompt.CrossesPromptPoint(10.0, 11.0, double.NaN, promptSeconds: 30));
        Assert.False(NextUpPrompt.CrossesPromptPoint(2891.0, 2892.0, EpisodeLength, promptSeconds: -5));
    }

    [Fact]
    public void AStoredLeadIsClampedToTheContractRange()
    {
        Assert.Equal(0, NextUpPrompt.Clamp(-1));
        Assert.Equal(45, NextUpPrompt.Clamp(45));
        Assert.Equal(NextUpPrompt.MaxSeconds, NextUpPrompt.Clamp(600));
    }

    [Fact]
    public void CrossingThePromptPointRaisesACountdownThatWaitsForTheEnd()
    {
        var upNext = new PlaybackUpNextState();
        Assert.False(upNext.OnPosition(500, 550, 600, 560, 30, seeking: false));
        // The credits marker no longer raises the card; the point 30 s before the end does.
        Assert.False(upNext.OnPosition(559, 561, 600, 560, 30, seeking: false));
        Assert.False(upNext.Visible);
        Assert.True(upNext.OnPosition(569, 571, 600, 560, 30, seeking: false));
        Assert.Equal(29, upNext.Countdown);
        Assert.False(upNext.Tick(0, 590, 600));
        Assert.Equal(10, upNext.Countdown);
        Assert.False(upNext.OnPosition(571, 580, 600, 560, 30, false));
        Assert.Equal(UpNextEnd.Advance, upNext.OnEnded(1000));
    }

    [Fact]
    public void AnEarlyCreditsMarkerDoesNotMoveThePrompt()
    {
        var upNext = new PlaybackUpNextState();
        Assert.False(upNext.OnPosition(2790.5, 2791.5, EpisodeLength, 2791, 30, seeking: false));
        Assert.False(upNext.Visible);
        Assert.True(upNext.OnPosition(2861.6, 2862.1, EpisodeLength, 2791, 30, seeking: false));
        Assert.Equal(30, upNext.Countdown);
    }

    [Fact]
    public void AtEndWaitsForTheStreamWithItsTenSecondCountdown()
    {
        var upNext = new PlaybackUpNextState();
        Assert.False(upNext.OnPosition(599, 599.5, 600, 560, 0, seeking: false));
        Assert.False(upNext.Visible);
        Assert.Equal(UpNextEnd.Show, upNext.OnEnded(1000));
        Assert.Equal(PlaybackUpNext.CountdownSeconds, upNext.Countdown);
    }

    [Fact]
    public void AResumeOrSeekPastThePromptPointDoesNotRaiseIt()
    {
        var upNext = new PlaybackUpNextState();
        Assert.False(upNext.OnPosition(575, 576, 600, 560, 30, false));
        Assert.False(upNext.OnPosition(100, 575, 600, 560, 30, seeking: true));
        Assert.False(upNext.Visible);
    }

    [Fact]
    public void AtTheEndAWallClockCountsTenSeconds()
    {
        var upNext = new PlaybackUpNextState();
        Assert.Equal(UpNextEnd.Show, upNext.OnEnded(1000));
        Assert.True(upNext.Visible);
        Assert.Equal(10, upNext.Countdown);
        Assert.False(upNext.Tick(5000, 0, 600));
        Assert.Equal(6, upNext.Countdown);
        Assert.True(upNext.Tick(11_000, 0, 600));
    }

    [Fact]
    public void DismissedOrAutoPlayOffNeverAdvancesByItself()
    {
        var off = new PlaybackUpNextState(autoPlay: false);
        Assert.True(off.OnPosition(569, 571, 600, 560, 30, false));
        Assert.Null(off.Countdown);
        Assert.Equal(UpNextEnd.Show, off.OnEnded(0));
        Assert.False(off.Tick(20_000, 600, 600));

        var dismissed = new PlaybackUpNextState();
        dismissed.OnPosition(569, 571, 600, 560, 30, false);
        dismissed.Dismiss();
        Assert.False(dismissed.Visible);
        Assert.False(dismissed.Tick(0, 600, 600));
        Assert.Equal(UpNextEnd.Leave, dismissed.OnEnded(0));
    }

    [Fact]
    public void TogglingAutoPlayStopsOrStartsTheCountdownOnAShowingCard()
    {
        var early = new PlaybackUpNextState();
        Assert.True(early.OnPosition(569, 571, 600, 560, 30, false));
        Assert.Equal(29, early.Countdown);
        early.SetAutoPlay(false, 0, 575, 600);
        Assert.Null(early.Countdown);
        Assert.False(early.Tick(0, 599, 600));
        early.SetAutoPlay(true, 0, 580, 600);
        Assert.Equal(20, early.Countdown);

        var ended = new PlaybackUpNextState(autoPlay: false);
        Assert.Equal(UpNextEnd.Show, ended.OnEnded(1_000));
        Assert.Null(ended.Countdown);
        ended.SetAutoPlay(true, 5_000, 600, 600);
        Assert.Equal(PlaybackUpNext.CountdownSeconds, ended.Countdown);
        Assert.False(ended.Tick(14_000, 600, 600));
        Assert.True(ended.Tick(15_000, 600, 600));

        var hidden = new PlaybackUpNextState(autoPlay: false);
        hidden.SetAutoPlay(true, 0, 0, 600);
        Assert.True(hidden.AutoPlay);
        Assert.Null(hidden.Countdown);
    }

    [Fact]
    public void AskingForTheCardEarlyShowsItWithoutACountdownUntilTheEnd()
    {
        var upNext = new PlaybackUpNextState();
        Assert.True(upNext.CanShowNow);
        Assert.True(upNext.ShowNow());
        Assert.False(upNext.CanShowNow);
        Assert.False(upNext.ShowNow());
        Assert.Null(upNext.Countdown);
        // Android e580e1811: the prompt point leaves a hand-opened card alone, without a countdown.
        Assert.False(upNext.OnPosition(569, 571, 600, 560, 30, false));
        Assert.Null(upNext.Countdown);
        Assert.False(upNext.Tick(0, 580, 600));
        upNext.SetAutoPlay(false, 0, 580, 600);
        upNext.SetAutoPlay(true, 0, 580, 600);
        Assert.Null(upNext.Countdown);
        Assert.Equal(UpNextEnd.Show, upNext.OnEnded(1_000));
        Assert.Equal(PlaybackUpNext.CountdownSeconds, upNext.Countdown);

        var dismissed = new PlaybackUpNextState();
        dismissed.OnPosition(569, 571, 600, 560, 30, false);
        dismissed.Dismiss();
        Assert.True(dismissed.ShowNow());
        Assert.Equal(UpNextEnd.Show, dismissed.OnEnded(0));
    }

    [Fact]
    public void TheEpisodeIsFinishedAtItsCreditsOrEnd()
    {
        // Android e727c9af5: an Up Next opened by hand first still counts a later credits crossing.
        var state = new PlaybackUpNextState();
        Assert.True(state.ShowNow());
        state.OnPosition(100, 200, 1000, 900, 30, seeking: false);
        Assert.False(state.Finished);
        state.OnPosition(800, 950, 1000, 900, 30, seeking: true);
        Assert.False(state.Finished);
        state.OnPosition(950, 951, 1000, 900, 30, seeking: false);
        Assert.True(state.Finished);

        var noCredits = new PlaybackUpNextState();
        noCredits.OnPosition(100, 200, 1000, null, 30, seeking: false);
        Assert.False(noCredits.Finished);
        noCredits.OnEnded(0);
        Assert.True(noCredits.Finished);

        // Android e580e1811: the prompt can open while the story still plays; only the credits or the end finish it.
        var early = new PlaybackUpNextState();
        Assert.True(early.OnPosition(969, 971, 1000, 990, 30, seeking: false));
        Assert.False(early.Finished);
        early.OnPosition(989, 991, 1000, 990, 30, seeking: false);
        Assert.True(early.Finished);
    }

    [Fact]
    public void TheSeriesPageLandsOnTheNextEpisodeOnlyAfterAFinish()
    {
        var next = Episode(2, 1);
        Assert.Equal(new SeriesPlaybackReturn("show", next.ContentId, 2), SeriesPlaybackReturn.For("show", "e", 1, finished: true, next));
        Assert.Equal(new SeriesPlaybackReturn("show", "e", 1), SeriesPlaybackReturn.For("show", "e", 1, finished: false, next));
        Assert.Equal(new SeriesPlaybackReturn("show", "e", 1), SeriesPlaybackReturn.For("show", "e", 1, finished: true, null));
        Assert.Null(SeriesPlaybackReturn.For(null, "e", 1, finished: true, next));
    }

    private static EpisodeCard Episode(long season, long episode) =>
        new($"e{season}-{episode}", season, episode, $"Episode {episode}", null, null, null, null, null, false);

    private static SeasonChoice Season(long number, bool special = false) =>
        new(number, number == 0 ? "Specials" : $"Season {number}", 5) { IsSpecials = special };
}
