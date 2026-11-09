using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class SoftwareDecodeTuningTests
{
    [Theory]
    [InlineData("original_http", "av1", "aac", false, true, 1)]
    [InlineData("progressive", "av1", "opus", false, true, 1)]
    [InlineData("server_remux_progressive", "av1", "dts", false, true, 1)]
    [InlineData("original_http", "hevc", "dts", false, true, null)]
    [InlineData("original_http", "h264", "truehd", false, true, null)]
    [InlineData("original_http", "hevc", "aac", false, true, null)]
    [InlineData("original_http", "av1", "aac", true, true, null)]
    [InlineData("original_http", "av1", "aac", false, false, null)]
    public void UploadDefaultsToVariantOneOnlyForSoftwareVideoRoutes(string delivery, string video, string audio,
        bool isHls, bool ffmpegEnabled, int? expected)
    {
        var route = PlaybackRoutePolicy.Decide(PlaybackRoutePolicyTests.Plan(delivery, video, audio), isHls, ffmpegEnabled);
        Assert.Equal(expected is not null, route.SoftwareVideo);
        Assert.Equal(expected, SoftwareDecodeTuning.D3d11Upload(route));
        Assert.Equal(2, SoftwareDecodeTuning.DefaultFilmGrainMode);
        Assert.Equal(expected is not null ? (int?)2 : null, SoftwareDecodeTuning.FilmGrainMode(route));
        Assert.Equal(expected is not null, SoftwareDecodeTuning.ZeroCopy(route));
    }

    [Theory]
    [InlineData(true, null, 1000)]
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
    public void SoftwareVideoUsesReleaseDefaultsAndOnlyTrimsQueuesAbove1080p(long? width, long? height, int? delay) =>
        Assert.Equal(new SoftwareDecodeTuning(delay is null ? 16 : 8, 4, delay), SoftwareDecodeTuning.ForVideo(true, width, height));

    [Theory]
    [InlineData(1920L, 1080L, null, 1000)]
    [InlineData(null, null, null, 1000)]
    [InlineData(3840L, 2160L, null, 500)]
    [InlineData(3840L, 2076L, null, 500)]
    [InlineData(1921L, null, null, 500)]
    [InlineData(null, 1081L, null, 500)]
    [InlineData(3840L, 2160L, 1000, 1000)]
    [InlineData(3840L, 2160L, 0, 0)]
    public void SoftwareBufferTimeHalvesAbove1080pUnlessOverridden(long? width, long? height, int? overrideMs, int expected) =>
        Assert.Equal(expected, SoftwareDecodeTuning.BufferTimeMs(true, overrideMs, width, height));

    [Fact]
    public void SystemVideoBufferStaysUnsetAt4K() =>
        Assert.Null(SoftwareDecodeTuning.BufferTimeMs(false, null, 3840, 2160));

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
