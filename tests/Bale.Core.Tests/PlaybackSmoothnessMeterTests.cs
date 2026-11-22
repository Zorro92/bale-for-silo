using System.Globalization;
using Bale.Core.Playback;
using static Bale.Core.Playback.PlaybackSmoothnessEvent;

namespace Bale.Core.Tests;

public class PlaybackSmoothnessMeterTests
{
    [Fact]
    public void InitialStartAndPrerollNeverCountAsStalls()
    {
        var meter = new PlaybackSmoothnessMeter();
        meter.Observe(Waiting, 0);
        meter.Observe(BufferingStarted, 1);
        meter.Observe(BufferingEnded, 10);
        meter.Observe(Playing, 10);
        var result = meter.Read(20, 60);
        Assert.Equal(0, result.Stalls);
        Assert.Equal(0, result.MissedFrames);
        Assert.Equal(10, result.WallSeconds);
        Assert.Equal("0 (0.0%)", result.MissedText);
    }

    [Theory]
    [InlineData(60, 78)]
    [InlineData(23.976, 31.1688)]
    public void MissedFramesCombineDropsAndStallEquivalentsOverPlayingPlusStalledTime(double fps, double missed)
    {
        var meter = new PlaybackSmoothnessMeter();
        meter.Observe(Playing, 0);
        meter.ObserveDropped(0);
        meter.Observe(Waiting, 1);
        meter.Observe(BufferingStarted, 1.1); // overlapping notification is the same stall
        meter.ObserveDropped(0); // a waiting engine can drop zero frames
        meter.Observe(BufferingEnded, 2);
        meter.Observe(Playing, 2.05);
        meter.Observe(Waiting, 3);
        meter.Observe(Playing, 3.3);
        var stalled = meter.Read(4, fps);
        Assert.Equal(2, stalled.Stalls);
        Assert.Equal(1.3, stalled.StalledSeconds, 8);
        Assert.Equal(missed, stalled.MissedFrames!.Value, 8);
        Assert.Equal(32.5, stalled.MissedPercent!.Value, 8);
        meter.ObserveDropped(6);
        var result = meter.Read(4, fps);
        Assert.Equal(missed + 6, result.MissedFrames!.Value, 8);
        Assert.Equal((missed + 6) / (4 * fps) * 100, result.MissedPercent!.Value, 8);
        Assert.Equal(6UL, result.DroppedFrames);
    }

    [Fact]
    public void RebufferingCountsAsNetworkAndEachStallIsTaggedOnce()
    {
        var completed = new List<PlaybackStall>();
        var meter = new PlaybackSmoothnessMeter();
        meter.StallCompleted += completed.Add;
        meter.Observe(Playing, 0);
        meter.Observe(BufferingStarted, 1, network: true);
        meter.Observe(Waiting, 1.1);
        meter.Observe(BufferingStarted, 1.2);
        meter.Observe(BufferingEnded, 1.5);
        meter.Observe(BufferingEnded, 1.6);
        meter.Observe(Playing, 1.7);
        meter.Observe(Waiting, 2);
        meter.NoteNetworkWait(true); // an HTTP completion during this stall supplies evidence
        meter.Observe(Playing, 2.2);
        var result = meter.Read(3, 60);
        Assert.Equal(2, result.Stalls);
        Assert.Equal(2, result.NetworkStalls);
        Assert.Equal(0.7, result.NetworkStalledSeconds, 8);
        Assert.Equal(2, completed.Count);
        Assert.All(completed, stall => Assert.True(stall.Network));
        Assert.Equal(0.5, result.LongestStallSeconds, 8);
    }

    [Fact]
    public void UserPauseAndResumePrerollAreExcludedIncludingLateWaitingAndPlayingEvents()
    {
        var meter = new PlaybackSmoothnessMeter();
        meter.Observe(Playing, 0);
        meter.Observe(Waiting, 1);
        meter.Observe(Pause, 1.2); // preserve only the legitimate stall before the user's pause
        meter.Observe(BufferingStarted, 2);
        meter.Observe(Playing, 3); // late callback while still user-paused
        meter.Observe(BufferingEnded, 4);
        meter.Observe(Play, 11);
        meter.Observe(Waiting, 11.1); // resume preroll before Playing
        meter.Observe(Playing, 12);
        meter.Observe(Waiting, 13);
        var result = meter.Read(13.5, 60);
        Assert.Equal(2.7, result.ActiveSeconds, 8);
        Assert.Equal(13.5, result.WallSeconds, 8);
        Assert.Equal(0.7, result.StalledSeconds, 8);
        Assert.Equal(2, result.Stalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeekAndItsPrerollAreExcludedAndTheTargetIsNotWatchedMedia(bool paused)
    {
        var meter = new PlaybackSmoothnessMeter();
        meter.Observe(Playing, 0);
        meter.ObservePosition(50);
        meter.ObservePosition(51);
        if (paused) meter.Observe(Pause, 1);
        meter.Observe(Seeking, 1);
        meter.Observe(Waiting, 2);
        meter.ObservePosition(500);
        meter.Observe(BufferingStarted, 3);
        meter.Observe(Seeked, 4);
        meter.ObservePosition(500);
        meter.Observe(Waiting, 5); // seeked does not mean seek preroll has finished
        meter.Observe(BufferingEnded, 6);
        if (paused) meter.Observe(Playing, 6); // still paused, ignored
        meter.Observe(Play, 7);
        meter.Observe(Playing, 7);
        meter.ObservePosition(500);
        meter.ObservePosition(501);
        var result = meter.Read(8, 23.976);
        Assert.Equal(0, result.Stalls);
        Assert.Equal(2, result.MediaSeconds);
        Assert.Equal(2, result.ActiveSeconds);
        Assert.Equal(8, result.WallSeconds);
    }

    [Fact]
    public void ASeekCutsOffAnExistingStallAndOverlappingSeekNotificationsDoNotResumeCounting()
    {
        var meter = new PlaybackSmoothnessMeter();
        meter.Observe(Playing, 0);
        meter.Observe(Waiting, 1);
        meter.Observe(Seeking, 1.3);
        meter.Observe(Seeking, 1.4);
        meter.Observe(Playing, 2);
        meter.Observe(Waiting, 3);
        meter.Observe(Seeked, 4);
        meter.Observe(Playing, 5);
        var result = meter.Read(6, 60);
        Assert.Equal(0.3, result.StalledSeconds, 8);
        Assert.Equal(1, result.Stalls);
        Assert.Equal(2.3, result.ActiveSeconds, 8);
    }

    [Fact]
    public void LiveStallsAndRollingWindowClipTheDurationAtTheTenSecondBoundary()
    {
        var meter = new PlaybackSmoothnessMeter();
        meter.Observe(Playing, 0);
        meter.Observe(Waiting, 1);
        meter.Observe(Playing, 4);
        meter.Observe(Waiting, 11, network: true);
        var result = meter.Read(13, 60);
        Assert.Equal(2, result.Stalls);
        Assert.Equal(5, result.StalledSeconds);
        Assert.Equal(1, result.RecentStalls); // starts in the last ten seconds
        Assert.Equal(3, result.RecentStalledSeconds); // includes 3..4 from the earlier stall
        Assert.Equal(3, result.LongestStallSeconds);
        result = meter.Read(22, 60);
        Assert.Equal(0, result.RecentStalls);
        Assert.Equal(10, result.RecentStalledSeconds);
        Assert.Equal(11, result.LongestStallSeconds);
        Assert.Equal(11, result.NetworkStalledSeconds);
    }

    [Fact]
    public void CounterResetsDoNotLoseEarlierDropsOrCountSeekAndPauseDelivery()
    {
        var meter = new PlaybackSmoothnessMeter();
        meter.ObserveDropped(5); // preroll
        meter.Observe(Playing, 0);
        meter.ObserveDropped(7); // 2 new drops
        meter.Observe(Pause, 1);
        meter.ObserveDropped(0);
        meter.Observe(Play, 2);
        meter.Observe(Playing, 2);
        meter.ObserveDropped(3);
        meter.Observe(Seeking, 3);
        meter.ObserveDropped(1); // while seeking
        meter.ObserveDropped(0);
        meter.Observe(Seeked, 4);
        meter.Observe(Playing, 4);
        meter.ObserveDropped(4);
        meter.ObserveDropped(1); // observed counter reset between polls
        Assert.Equal(10UL, meter.Read(5, 60).DroppedFrames);
    }

    [Fact]
    public void EndFreezesAnOpenStallAndTotalsSurviveAReplacementMountButNewPlaybackStartsAtZero()
    {
        var meter = new PlaybackSmoothnessMeter();
        meter.Observe(Playing, 0);
        meter.ObservePosition(20);
        meter.ObservePosition(21);
        meter.ObserveDropped(3);
        meter.Observe(Waiting, 1);
        meter.Observe(Ended, 1.5);
        var ended = meter.Read(100, 60);
        Assert.Equal(1.5, ended.WallSeconds);
        Assert.Equal(0.5, ended.StalledSeconds);
        meter.BeginMount(100);
        meter.Observe(Waiting, 101);
        meter.ObserveDropped(0);
        meter.Observe(Playing, 102);
        meter.ObservePosition(500);
        meter.ObservePosition(501);
        meter.ObserveDropped(1);
        var result = meter.Read(103, 60);
        Assert.Equal(1, result.Stalls);
        Assert.Equal(4UL, result.DroppedFrames);
        Assert.Equal(2.5, result.ActiveSeconds);
        Assert.Equal(103, result.WallSeconds);
        Assert.Equal(2, result.MediaSeconds);
        Assert.Equal(0, new PlaybackSmoothnessMeter().Read(103, 60).MissedFrames);
    }

    [Fact]
    public void ThirtySecondsOfMediaOverFortyOneWallSecondsRevealsStallsDespiteZeroDrops()
    {
        var meter = new PlaybackSmoothnessMeter();
        meter.Observe(Playing, 0);
        meter.ObservePosition(0);
        for (var i = 0; i < 30; i++)
        {
            var start = i * 41.0 / 30;
            meter.ObservePosition(i + 1);
            meter.Observe(Waiting, start + 1);
            meter.Observe(Playing, (i + 1) * 41.0 / 30);
        }
        meter.Observe(Ended, 41);
        var result = meter.Read(45, 60);
        Assert.Equal(0UL, result.DroppedFrames);
        Assert.Equal(660, result.MissedFrames!.Value, 8);
        Assert.Equal(11.0 / 41 * 100, result.MissedPercent!.Value, 8);
        Assert.Equal("660 (26.8%)", result.MissedText);
        Assert.Equal("41.0 s for 30.0 s of media", result.PlaybackTimeText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnknownOrInvalidStreamRateNeverInventsFrameEquivalents(double fps)
    {
        var meter = new PlaybackSmoothnessMeter();
        meter.Observe(Playing, 0);
        meter.Observe(Waiting, 1);
        Assert.Null(meter.Read(2, fps).MissedFrames);
        Assert.Null(meter.Read(2, null).MissedPercent);
        Assert.Equal("unavailable", meter.Read(2, fps).MissedText);
    }

    [Fact]
    public void PercentageIsBoundedAndFormattingUsesInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var meter = new PlaybackSmoothnessMeter();
            meter.Observe(Playing, 0);
            meter.ObserveDropped(120);
            Assert.Equal("120 (100.0%)", meter.Read(1, 60).MissedText);
            Assert.Equal(0, meter.Read(0, 60).MissedPercent);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
