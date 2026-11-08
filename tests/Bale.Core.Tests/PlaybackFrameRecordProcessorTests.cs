using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackFrameRecordProcessorTests
{
    [Fact]
    public void SamplesAreProcessedInDeliveryOrderAndReadsUseTheSameClock()
    {
        var processor = new PlaybackFrameRecordProcessor();
        Assert.Null(processor.Read(TimeSpan.Zero, 1000));
        processor.Process(Sample(1000, 200, 200));
        processor.Process(Sample(1040, 240, 250));
        processor.Process(Sample(1080, 280, 280));

        var snapshot = processor.Read(Ms(280), 1090)!;
        Assert.Equal(3, snapshot.TotalFramesDelivered);
        Assert.Equal(1, snapshot.LateFrames);
        Assert.Equal(1, snapshot.RecentLateFrames);
        Assert.Equal(0.04, snapshot.DeliveredAheadSeconds);
        var trace = processor.DescribeLateFrameTrace();
        Assert.StartsWith("samples=3 late=1", trace);
        Assert.Contains("t=40.0 pts=240.0", trace);
        Assert.Equal(0, processor.Read(Ms(280), 11040)!.RecentLateFrames);
        Assert.Equal(1, processor.Read(Ms(280), 11040)!.LateFrames);
    }

    [Fact]
    public void StartingResetsTraceAndTimelineButPreservesMountTotals()
    {
        var processor = new PlaybackFrameRecordProcessor();
        processor.Process(Sample(1000, 100, 120));
        processor.Process(Sample(1040, 140, 160));
        processor.Process(new(2, 0, 1080));
        Assert.StartsWith("samples=0 late=0", processor.DescribeLateFrameTrace());
        var reset = processor.Read(Ms(200), 1080)!;
        Assert.Null(reset.DeliveredAheadSeconds);
        Assert.Equal(2, reset.TotalFramesDelivered);
        Assert.Equal(1, reset.LateFrames);

        processor.Process(Sample(1120, 180, 200));
        Assert.Equal(1, processor.Read(Ms(200), 1120)!.LateFrames);
        Assert.StartsWith("samples=1 late=0", processor.DescribeLateFrameTrace());
        processor.Process(Sample(1160, 220, 240));
        Assert.Equal(2, processor.Read(Ms(240), 1160)!.LateFrames);
        Assert.StartsWith("samples=2 late=1", processor.DescribeLateFrameTrace());
    }

    [Fact]
    public void SeekingMarkersSuppressSamplesAndResetBothEdgesOfTheTrace()
    {
        var processor = new PlaybackFrameRecordProcessor();
        processor.Process(Sample(1000, 100, 120));
        processor.Process(Sample(1040, 140, 160));
        processor.Process(new(3, 0, 1050));
        processor.Process(Sample(1080, 180, 200));
        processor.Process(new(2, 0, 1090));
        processor.Process(Sample(1120, 220, 240));
        Assert.StartsWith("samples=0 late=0", processor.DescribeLateFrameTrace());
        Assert.Equal(2, processor.Read(Ms(240), 1120)!.TotalFramesDelivered);
        Assert.Null(processor.Read(Ms(240), 1120)!.DeliveredAheadSeconds);

        processor.Process(new(4, 0, 1130));
        processor.Process(Sample(1160, 260, 280));
        Assert.StartsWith("samples=1 late=0", processor.DescribeLateFrameTrace());
        Assert.Equal(1, processor.Read(Ms(280), 1160)!.LateFrames);
        processor.Process(Sample(1200, 300, 320));
        Assert.Equal(4, processor.Read(Ms(320), 1200)!.TotalFramesDelivered);
        Assert.Equal(2, processor.Read(Ms(320), 1200)!.LateFrames);
        Assert.StartsWith("samples=2 late=1", processor.DescribeLateFrameTrace());
    }

    [Theory]
    [InlineData(0u, 0)] // Not playing.
    [InlineData(1u, 1)] // Playing, continuous.
    [InlineData(3u, 0)] // Playing, discontinuous.
    public void PlayingAndDiscontinuousFlagsReachTheMeter(uint flags, long late)
    {
        var processor = new PlaybackFrameRecordProcessor();
        processor.Process(Sample(1000, 100, 120));
        processor.Process(Sample(1040, 140, 160) with { Flags = flags });
        Assert.Equal(late, processor.Read(Ms(160), 1040)!.LateFrames);
        Assert.Equal(2, processor.Read(Ms(160), 1040)!.TotalFramesDelivered);
    }

    [Theory]
    [InlineData(999.9, 1000)]
    [InlineData(1000, 1050)] // Inclusive +50 ms race allowance.
    [InlineData(1000.1, 1050.1)]
    [InlineData(1100, 1100)]
    [InlineData(5000, 1100)]
    public void SnapshotLookupSelectsTheLatestWithinTheTolerance(double delivery, double expected)
    {
        var processor = new PlaybackFrameRecordProcessor();
        foreach (var now in new[] { 1000, 1050, 1050.1, 1100 })
            processor.AddSnapshot(Environment(now));
        Assert.Equal(expected, processor.SnapshotFor(delivery).NowMilliseconds);
    }

    [Fact]
    public void SnapshotLookupFallsBackToOldestAndHandlesWrappedHistory()
    {
        var processor = new PlaybackFrameRecordProcessor(snapshotCapacity: 3);
        Assert.Equal(default, processor.SnapshotFor(1000));
        processor.AddSnapshot(Environment(1000));
        processor.AddSnapshot(Environment(1025));
        processor.AddSnapshot(Environment(1050));
        processor.AddSnapshot(Environment(1075));
        Assert.Equal(1025, processor.SnapshotFor(0).NowMilliseconds);
        Assert.Equal(1025, processor.SnapshotFor(980).NowMilliseconds);
        Assert.Equal(1050, processor.SnapshotFor(1000).NowMilliseconds);
        Assert.Equal(1075, processor.SnapshotFor(1025).NowMilliseconds);
    }

    [Fact]
    public void GcPauseJumpBetweenSampleSnapshotsClassifiesTheLateSampleAsGc()
    {
        var processor = new PlaybackFrameRecordProcessor();
        // The second snapshot can only match the second sample, even when both are drained together.
        processor.AddSnapshot(Environment(1000));
        processor.AddSnapshot(Environment(1060, pause: 7, gen0: 1));
        processor.Process(Sample(1000, 0, 10));
        processor.Process(Sample(1040, 40, 50));
        var trace = processor.DescribeLateFrameTrace();
        Assert.StartsWith("samples=2 late=1 stream=0 gc=1 gap=0 other=0", trace);
        Assert.Contains("gc=7.0 g0=1 g1=0 g2=0 class=gc", trace);
    }

    [Fact]
    public void StreamWaitJumpKeepsTheExistingPrecedenceOverGc()
    {
        var processor = new PlaybackFrameRecordProcessor();
        processor.AddSnapshot(Environment(1000) with { DemandWaits = 0, DemandWaitTime = TimeSpan.Zero });
        processor.AddSnapshot(Environment(1060, pause: 10, gen0: 1) with { DemandWaits = 1, DemandWaitTime = Ms(5) });
        processor.Process(Sample(1000, 0, 10));
        processor.Process(Sample(1040, 40, 50));
        Assert.StartsWith("samples=2 late=1 stream=1 gc=0", processor.DescribeLateFrameTrace());
        Assert.Contains("dw=1/5.0", processor.DescribeLateFrameTrace());
        Assert.Contains("class=stream", processor.DescribeLateFrameTrace());
    }

    [Fact]
    public void MissingSnapshotsDoNotInventStreamWaitsOrGcPauses()
    {
        var processor = new PlaybackFrameRecordProcessor();
        processor.Process(Sample(1000, 0, 10));
        processor.Process(Sample(1040, 40, 50));
        Assert.Contains("dw=- gc=0.0 g0=0 g1=0 g2=0 class=other", processor.DescribeLateFrameTrace());
    }

    [Fact]
    public void TraceResetPreservesTheMeterAndSnapshotHistory()
    {
        var processor = new PlaybackFrameRecordProcessor();
        processor.AddSnapshot(Environment(1000, pause: 7, gen0: 1));
        processor.Process(Sample(1000, 0, 10));
        processor.Process(Sample(1040, 40, 50));
        processor.ResetLateFrameTrace();
        Assert.StartsWith("samples=0 late=0", processor.DescribeLateFrameTrace());
        Assert.Equal(1, processor.Read(Ms(50), 1040)!.LateFrames);
        Assert.Equal(2, processor.Read(Ms(50), 1040)!.TotalFramesDelivered);
        Assert.Equal(7, processor.SnapshotFor(1040).GcPauseTotal.TotalMilliseconds);
        processor.Process(Sample(1080, 80, 90));
        Assert.StartsWith("samples=1 late=0", processor.DescribeLateFrameTrace());
        Assert.Equal(2, processor.Read(Ms(90), 1080)!.LateFrames);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SnapshotRingRequiresPositiveCapacity(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackFrameRecordProcessor(snapshotCapacity: capacity));

    private static PlaybackFrameRecord Sample(double now, double pts, double position) =>
        new(1, 1, now, Ms(pts).Ticks, Ms(40).Ticks, Ms(position).Ticks);

    private static PlaybackFrameEnvironmentSnapshot Environment(double now, double pause = 0, int gen0 = 0) =>
        new(now, Ms(pause), gen0, 0, 0);

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);
}
