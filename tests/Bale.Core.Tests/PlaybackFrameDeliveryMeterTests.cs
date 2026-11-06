using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackFrameDeliveryMeterTests
{
    [Fact]
    public void NoSamplesMeansUnavailableNotZero()
    {
        var meter = new PlaybackFrameDeliveryMeter();
        Assert.Null(meter.Read(TimeSpan.Zero, 0));
        Observe(meter, 1, 0, playing: false);
        Assert.Equal(0, meter.Read(TimeSpan.Zero, 0)!.LateFrames);
    }

    [Fact]
    public void CountsOnlyFramesDeliveredAfterTheirPresentationTime()
    {
        var meter = new PlaybackFrameDeliveryMeter();
        Observe(meter, 0, 0); // First frame is preroll.
        Observe(meter, 1, 1); // At the deadline, not late.
        Observe(meter, 2, 1); // Ahead.
        Observe(meter, 3, 3.001, now: 100);
        Observe(meter, 4, 4.2, now: 200);
        var snapshot = meter.Read(TimeSpan.FromSeconds(4.2), 200)!;
        Assert.Equal(2, snapshot.LateFrames);
        Assert.Equal(2, snapshot.RecentLateFrames);
        Assert.Equal(0.2, snapshot.RecentLateFramesPerSecond);
        Assert.Equal(0, snapshot.DeliveredAheadSeconds);
    }

    [Fact]
    public void RecentWindowExpiresEvenWhenNoNewSamplesArrive()
    {
        var meter = new PlaybackFrameDeliveryMeter();
        Observe(meter, 0, 0);
        Observe(meter, 1, 2, now: 100);
        Observe(meter, 2, 3, now: 200);
        Assert.Equal(2, meter.Read(TimeSpan.Zero, 10_099)!.RecentLateFrames);
        Assert.Equal(1, meter.Read(TimeSpan.Zero, 10_100)!.RecentLateFrames);
        var snapshot = meter.Read(TimeSpan.Zero, 10_200)!;
        Assert.Equal(0, snapshot.RecentLateFramesPerSecond);
        Assert.Equal(2, snapshot.LateFrames);
    }

    [Fact]
    public void SeeksAndDiscontinuitiesDoNotLookLikeLateDecoderDelivery()
    {
        var meter = new PlaybackFrameDeliveryMeter();
        Observe(meter, 0, 0);
        Observe(meter, 1, 2);
        meter.SetSeeking(true);
        Observe(meter, 2, 300);
        Assert.Null(meter.Read(TimeSpan.Zero, 0)!.DeliveredAheadSeconds);
        meter.SetSeeking(false);
        Observe(meter, 299, 300); // Preroll after SeekCompleted.
        Observe(meter, 300, 301, discontinuous: true);
        meter.InvalidateTimeline();
        Observe(meter, 400, 401); // MSS Starting also invalidates the timeline.
        Assert.Equal(1, meter.Read(TimeSpan.Zero, 0)!.LateFrames);
        Observe(meter, 401, 402);
        Assert.Equal(2, meter.Read(TimeSpan.Zero, 0)!.LateFrames);
    }

    [Fact]
    public void PausedAndBufferingDeliveryAndTheirFirstPlayingFrameAreExcluded()
    {
        var meter = new PlaybackFrameDeliveryMeter();
        Observe(meter, 0, 10, playing: false);
        Observe(meter, 1, 10, playing: false);
        Observe(meter, 2, 10);
        Observe(meter, 10, 10);
        Assert.Equal(0, meter.Read(TimeSpan.Zero, 0)!.LateFrames);
    }

    [Fact]
    public void DeliveredAheadIsASampleHorizonNotAnInventedQueueCount()
    {
        var meter = new PlaybackFrameDeliveryMeter();
        Observe(meter, 10, 9.5);
        Assert.Equal(0.54, meter.Read(TimeSpan.FromSeconds(9.5), 0)!.DeliveredAheadSeconds!.Value, precision: 6);
        Assert.Equal(0.29, meter.Read(TimeSpan.FromSeconds(9.75), 0)!.DeliveredAheadSeconds!.Value, precision: 6);
        Assert.Equal(0, meter.Read(TimeSpan.FromSeconds(11), 0)!.DeliveredAheadSeconds);
    }

    [Fact]
    public void DisplayRefreshCadenceDoesNotCreateLateFrames()
    {
        var meter = new PlaybackFrameDeliveryMeter();
        for (var frame = 0; frame < 240; frame++)
        {
            var pts = frame / (24000.0 / 1001);
            Observe(meter, pts, Math.Max(0, pts - 0.1), now: (long)(frame * 1001.0 / 24));
        }
        Assert.Equal(0, meter.Read(TimeSpan.FromSeconds(10), 10_000)!.LateFrames);
    }

    [Theory]
    [InlineData(1, 0.44, 0.44)]
    [InlineData(2, 0.14, 0.29)]
    [InlineData(3, 0.14, 0.24)]
    public void OptionalAheadStatisticsUseEveryEligibleDeliveryAndAnExactMedian(int count, double minimum, double median)
    {
        var meter = new PlaybackFrameDeliveryMeter(collectAheadStatistics: true);
        Observe(meter, 0, 0); // Preroll counts as delivered but not toward the distribution.
        double[] horizons = [0.4, 0.1, 0.2];
        for (var index = 0; index < count; index++) Observe(meter, 1 + horizons[index], 1);
        var snapshot = meter.Read(TimeSpan.FromSeconds(50), 0)!;
        Assert.Equal(count + 1, snapshot.TotalFramesDelivered);
        Assert.Equal(minimum, snapshot.MinDeliveredAheadSeconds!.Value, precision: 6);
        Assert.Equal(median, snapshot.MedianDeliveredAheadSeconds!.Value, precision: 6);
        Assert.Equal(0, snapshot.DeliveredAheadSeconds); // Read-time clock does not resample the distribution.
        Assert.Equal(snapshot, meter.Read(TimeSpan.FromSeconds(50), 0));
    }

    [Fact]
    public void AheadStatisticsShareLateFrameExclusionsAndFloorLateSampleEndsAtZero()
    {
        var meter = new PlaybackFrameDeliveryMeter(collectAheadStatistics: true);
        Observe(meter, 0, 10, playing: false);
        Observe(meter, 1, 10); // First playing frame.
        Observe(meter, 10, 9.5); // 0.54 s ahead.
        Observe(meter, 11, 12); // Late; horizon floored to zero.
        Observe(meter, 12, 30, discontinuous: true);
        meter.SetSeeking(true);
        Observe(meter, 13, 300); // Not counted as delivered during a seek.
        meter.SetSeeking(false);
        Observe(meter, 30, 31); // Post-seek preroll.
        Observe(meter, 31, 40, playing: false);
        Observe(meter, 32, 40); // First playing frame after pause/buffering.
        meter.InvalidateTimeline();
        Observe(meter, 40, 50); // Post-Starting preroll.
        var snapshot = meter.Read(TimeSpan.Zero, 0)!;
        Assert.Equal(9, snapshot.TotalFramesDelivered);
        Assert.Equal(1, snapshot.LateFrames);
        Assert.Equal(0, snapshot.MinDeliveredAheadSeconds);
        Assert.Equal(0.27, snapshot.MedianDeliveredAheadSeconds!.Value, precision: 6);
    }

    [Fact]
    public void DefaultPlayerMeterRetainsNoAheadHistoryAndPrerollAloneHasNoDistribution()
    {
        var meter = new PlaybackFrameDeliveryMeter();
        Observe(meter, 0, 0);
        Observe(meter, 1, 0.5);
        var snapshot = meter.Read(TimeSpan.Zero, 0)!;
        Assert.Equal(2, snapshot.TotalFramesDelivered);
        Assert.Null(snapshot.MinDeliveredAheadSeconds);
        Assert.Null(snapshot.MedianDeliveredAheadSeconds);
        var probe = new PlaybackFrameDeliveryMeter(collectAheadStatistics: true);
        Assert.Null(probe.Read(TimeSpan.Zero, 0));
        Observe(probe, 0, 0);
        Assert.Null(probe.Read(TimeSpan.Zero, 0)!.MinDeliveredAheadSeconds);
        Assert.Null(probe.Read(TimeSpan.Zero, 0)!.MedianDeliveredAheadSeconds);
    }

    [Fact]
    public void SoftwareObserverIsPassiveDebugOnlyAndDetachedBeforePlayerDisposal()
    {
        var observer = Read("DebugFrameDelivery.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", observer);
        Assert.Contains("source.GetMediaStreamSource()", observer);
        // Native AOT returns the descriptor as a plain IInspectable wrapper: a C# type test is
        // always false there (0.1.243 console log), so the video check must QueryInterface.
        Assert.DoesNotContain("is not VideoStreamDescriptor", observer);
        Assert.Contains("!IsVideo(args.Request.StreamDescriptor)", observer);
        Assert.Contains("WinRT.CastExtensions.As<VideoStreamDescriptor>(descriptor)", observer);
        Assert.Contains(".EncodingProperties.Subtype", observer);
        Assert.Contains("internal string? OutputPixelFormat", observer);
        Assert.Contains("bool collectAheadStatistics = false", observer);
        Assert.Contains("args.Request.Sample is not { } sample", observer);
        Assert.Contains("_source.SampleRequested -= OnSampleRequested", observer);
        Assert.DoesNotContain("args.Request.Sample =", observer);
        Assert.DoesNotContain("sample.Buffer", observer);
        Assert.DoesNotContain("ex.Message", observer);
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("if (media.Route.SoftwareVideo && media.FfmpegSource is { } softwareSource)", media);
        Assert.True(media.IndexOf("media.FrameDelivery?.Dispose()", StringComparison.Ordinal)
            < media.IndexOf("player.Dispose", StringComparison.Ordinal));
        var stats = Read("PlayerPage.Stats.cs.txt");
        Assert.Contains("(\"Dropped frames\", \"unavailable\")", stats);
        Assert.Contains("frames.RecentLateFramesPerSecond", stats);
        Assert.Contains("frames?.DeliveredAheadSeconds", stats);
        Assert.Contains("player.DebugStatsRows", Read("DebugServer.cs.txt"));
    }

    private static void Observe(PlaybackFrameDeliveryMeter meter, double pts, double position,
        bool playing = true, bool discontinuous = false, long now = 0) =>
        meter.Observe(TimeSpan.FromSeconds(pts), TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(position),
            playing, discontinuous, now);

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
