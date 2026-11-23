using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugDecoderLogTests
{
    [Fact]
    public void ForkDiagnosticsKeepEachSourceSeparateAndPreserveTheCloseDiscardCount()
    {
        DebugDecoderLog.Reset();
        Assert.Equal("unavailable / 60 fps", DebugDecoderLog.Snapshot().DecodeRateText(60));
        DebugDecoderLog.Observe("Bale rav1g: delivery samples=90 rate=45.10/s latency avg=3.0 max=6.0 ms");
        DebugDecoderLog.Observe("Bale decode-ahead timing: read=1.2 decode=3.4 starved=6\n");
        var live = DebugDecoderLog.Snapshot();
        Assert.Equal("45.1 / 60 fps", live.DecodeRateText(60));
        Assert.Null(live.Discarded);
        Assert.Equal(6UL, live.Starved);
        DebugDecoderLog.Observe("Bale rav1g: destroyed (frames=90 discarded=7 images created=8 peak in use=8)");
        Assert.Equal(7UL, DebugDecoderLog.Snapshot().Discarded);
        DebugDecoderLog.Observe("Bale rav1g: delivery samples=12 rate=24.00/s");
        // Weighted by samples: 102 frames over 90/45.1 + 12/24 s.
        Assert.Equal("40.9 / 23.976 fps", DebugDecoderLog.Snapshot().DecodeRateText(23.976));
        Assert.Equal(7UL, DebugDecoderLog.Snapshot().Discarded);
        DebugDecoderLog.Reset();
        Assert.Equal(default, DebugDecoderLog.Snapshot());
    }

    [Fact]
    public void ThePartialTeardownWindowDoesNotReplaceThePlaybackRate()
    {
        DebugDecoderLog.Reset();
        DebugDecoderLog.Observe("Bale rav1g: delivery samples=240 rate=60.00/s");
        DebugDecoderLog.Observe("Bale rav1g: delivery samples=240 rate=60.00/s");
        DebugDecoderLog.Observe("Bale rav1g: delivery samples=117 rate=34.61/s");
        // 597 frames over 8 s + 3.38 s = 52.5 fps, not the last window's 34.6.
        Assert.Equal("52.5 / 60 fps", DebugDecoderLog.Snapshot().DecodeRateText(60));
        DebugDecoderLog.Reset();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("untrusted rate=123 discarded=7 starved=2")]
    [InlineData("Bale rav1g: delivery rate=NaN/s")]
    [InlineData("Bale rav1g: delivery rate=-3/s")]
    [InlineData("Bale rav1g: delivery rate=3evil/s")]
    [InlineData("Bale rav1g: delivery bitrate=300/s")]
    [InlineData("Bale rav1g: destroyed (discarded=-5)")]
    [InlineData("Bale decode-ahead timing: starved=18446744073709551616")]
    public void MissingOrMalformedDiagnosticsStayUnavailable(string? text)
    {
        DebugDecoderLog.Reset();
        DebugDecoderLog.Observe(text);
        Assert.Equal(default, DebugDecoderLog.Snapshot());
    }
}
