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

    [Fact]
    public void SoftwareObserverIsPassiveDebugOnlyAndDetachedBeforePlayerDisposal()
    {
        var observer = Read("DebugFrameDelivery.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", observer);
        Assert.Contains("source.GetMediaStreamSource()", observer);
        Assert.Contains("args.Request.StreamDescriptor is not VideoStreamDescriptor", observer);
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
