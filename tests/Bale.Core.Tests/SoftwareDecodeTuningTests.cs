using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class SoftwareDecodeTuningTests
{
    [Theory]
    [InlineData(true, null, 2000)]
    [InlineData(false, null, null)]
    [InlineData(true, 0, 0)]
    [InlineData(false, 0, 0)]
    [InlineData(true, 1500, 1500)]
    [InlineData(false, 1500, 1500)]
    [InlineData(true, 5000, 5000)]
    [InlineData(false, 5000, 5000)]
    public void BufferTimeUsesSoftwareVideoDefaultUnlessExplicitlyOverridden(bool softwareVideo, int? overrideMs, int? expected) =>
        Assert.Equal(expected, SoftwareDecodeTuning.BufferTimeMs(softwareVideo, overrideMs));

    [Theory]
    [InlineData(1280L, 720L, null)]
    [InlineData(1920L, 1080L, null)]
    [InlineData(3840L, 2160L, 8)]
    [InlineData(1921L, 1080L, 8)]
    [InlineData(1920L, 1081L, 8)]
    [InlineData(2560L, 1080L, 8)]
    [InlineData(null, null, null)]
    [InlineData(1920L, null, null)]
    [InlineData(null, 1080L, null)]
    [InlineData(3840L, null, 8)]
    [InlineData(null, 2160L, 8)]
    public void SoftwareVideoUsesReleaseDefaultsAndOnlyLimitsDelayAbove1080p(long? width, long? height, int? delay) =>
        Assert.Equal(new SoftwareDecodeTuning(16, 4, delay), SoftwareDecodeTuning.ForVideo(true, width, height));

    [Theory]
    [InlineData("hevc", "aac", false)]
    [InlineData("hevc", "dts", true)]
    [InlineData("h264", "truehd", true)]
    public void NativeAndFfmpegSystemVideoRoutesSetNoForkOptions(string video, string audio, bool useFfmpeg)
    {
        var plan = PlaybackRoutePolicyTests.Plan("original_http", video, audio);
        plan.Source!.Width = 3840;
        plan.Source.Height = 2160;
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.Equal(useFfmpeg, route.UseFfmpeg);
        Assert.False(route.SoftwareVideo);
        Assert.Equal(new SoftwareDecodeTuning(), SoftwareDecodeTuning.ForVideo(route.SoftwareVideo, route.VideoWidth, route.VideoHeight));
    }
}
