using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackThroughputMeterTests
{
    [Fact]
    public void EstimateUsesDownloadTimeNotTheOneSecondSamplingInterval()
    {
        var meter = new PlaybackThroughputMeter();
        Assert.Equal(80_000_000, meter.Sample(1_000_000, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void WindowWeightsSamplesByTheirDownloadedBytesAndTime()
    {
        var meter = new PlaybackThroughputMeter();
        Assert.Equal(8_000_000, meter.Sample(1_000_000, TimeSpan.FromSeconds(1)));
        Assert.Equal(16_000_000, meter.Sample(3_000_000, TimeSpan.FromSeconds(1.5)));
        for (var i = 0; i < 3; i++)
            Assert.Equal(16_000_000, meter.Sample(3_000_000, TimeSpan.FromSeconds(1.5)));
        Assert.Equal(32_000_000, meter.Sample(3_000_000, TimeSpan.FromSeconds(1.5)));
        Assert.Null(meter.Sample(3_000_000, TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public void IdlePlaybackExpiresTheEstimateAfterFiveSamples()
    {
        var meter = new PlaybackThroughputMeter();
        meter.Sample(1_000_000, TimeSpan.FromSeconds(1));
        for (var i = 0; i < 4; i++)
            Assert.Equal(8_000_000, meter.Sample(1_000_000, TimeSpan.FromSeconds(1)));
        Assert.Null(meter.Sample(1_000_000, TimeSpan.FromSeconds(1)));
        Assert.Equal(16_000_000, meter.Sample(2_000_000, TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public void NoBytesOrNoDownloadTimeCannotEstimateThroughput()
    {
        Assert.Null(new PlaybackThroughputMeter().Sample(0, TimeSpan.Zero));
        Assert.Null(new PlaybackThroughputMeter().Sample(1000, TimeSpan.Zero));
        Assert.Null(new PlaybackThroughputMeter().Sample(0, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ReplacementCountersResetTheWindow()
    {
        var meter = new PlaybackThroughputMeter();
        meter.Sample(1_000_000, TimeSpan.FromSeconds(1));
        Assert.Equal(8000, meter.Sample(1000, TimeSpan.FromSeconds(1)));
        Assert.Equal(16000, meter.Sample(1000, TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public void ExtremeCounterValuesCannotOverflowTheEstimate()
    {
        Assert.Equal(long.MaxValue, new PlaybackThroughputMeter().Sample(long.MaxValue, TimeSpan.FromTicks(1)));
    }
}
