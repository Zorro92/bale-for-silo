using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>Owner's bit-depth rule: the engine's swap chain follows the frames of every route (AGENTS.md).</summary>
public sealed class EngineOutputPolicyTests
{
    private static PlaybackPlan Plan(string delivery, string video, int? depth, string range = "sdr", int width = 1920, int height = 1080)
    {
        var plan = PlaybackRoutePolicyTests.Plan(delivery, video, "aac");
        plan.EffectiveMediaFileId = "file";
        plan.Source!.MediaFileId = "file";
        plan.Source.BitDepth = depth;
        plan.Source.DynamicRange = range;
        plan.Source.Width = width;
        plan.Source.Height = height;
        return plan;
    }

    private static EngineOutputDecision Engine(PlaybackPlan plan, PlaybackRouteDecision route, bool stageP010 = true,
        IReadOnlyList<FileVersion>? versions = null) => EngineOutputPolicy.Decide(plan, route, versions, stageP010);

    [Theory]
    [InlineData("h264", 8, "bgra")]
    [InlineData("hevc", 8, "bgra")]
    [InlineData("hevc", 10, "rgb10")]
    [InlineData("vp9", 10, "rgb10")]
    public void SystemDecoderThroughFfmpegFollowsTheSourceDepth(string video, int depth, string target)
    {
        var plan = Plan("original_http", video, depth);
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.True(route.UseFfmpeg);
        Assert.False(route.SoftwareVideo);
        Assert.Equal(target, Engine(plan, route).Target);
    }

    [Fact]
    public void WindowsOwnSourceFollowsTheSourceDepth()
    {
        var plan = Plan("original_http", "hevc", 10);
        var route = PlaybackRoutePolicy.Decide(plan, false, ffmpegEnabled: false);
        Assert.False(route.UseFfmpeg);
        var output = Engine(plan, route);
        Assert.Equal(("rgb10", "p010", 2u), (output.Target, output.Frames, output.Code));
        plan.Source!.BitDepth = 8;
        Assert.Equal(("bgra", "nv12", 0u), (Engine(plan, route).Target, Engine(plan, route).Frames, Engine(plan, route).Code));
    }

    [Theory]
    [InlineData("av1", 10, "rgb10")]
    // dav1d zero-copy keeps 8-bit AV1 as shifted samples in its P010 pool (no 10-bit processing); rav1g takes NV12.
    [InlineData("av1", 8, "bgra")]
    [InlineData("mpeg4", 8, "bgra")]
    public void SoftwareDecodeFollowsTheSourceDepth(string video, int depth, string target)
    {
        var plan = Plan("original_http", video, depth);
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.True(route.SoftwareVideo);
        Assert.Equal(target, Engine(plan, route).Target);
    }

    [Fact]
    public void ConsoleToneMapWritesP010()
    {
        var plan = Plan("original_http", "hevc", 10, "hdr10", 3840, 2160);
        var route = PlaybackRoutePolicy.Decide(plan, false, sdrOutput: true);
        Assert.True(route.ConsoleToneMap);
        Assert.Equal("rgb10", Engine(plan, route).Target);
        // A bale_gpu without P010 output writes NV12: the output follows it.
        Assert.Equal("bgra", Engine(plan, route, stageP010: false).Target);
    }

    [Fact]
    public void SdrDownscaleWritesP010EvenFor8BitSources()
    {
        var plan = Plan("original_http", "hevc", 8, "sdr", 3840, 2160);
        var route = PlaybackRoutePolicy.Decide(plan, false, display: (1920, 1080), sdrDownscale: true);
        Assert.True(route.ConsoleDownscale);
        Assert.Equal(("rgb10", "p010"), (Engine(plan, route).Target, Engine(plan, route).Frames));
        Assert.Equal("bgra", Engine(plan, route, stageP010: false).Target);
    }

    [Fact]
    public void HdrPassthroughOnAnHdrDisplayGetsRgb10()
    {
        var plan = Plan("original_http", "hevc", null, "hdr10", 3840, 2160);
        var route = PlaybackRoutePolicy.Decide(plan, false, sdrOutput: false);
        Assert.False(route.ConsoleToneMap);
        Assert.Equal("rgb10", Engine(plan, route).Target);
    }

    [Theory]
    [InlineData("server_remux_progressive", false)]
    [InlineData("server_remux_hls", true)]
    public void RemuxesKeepTheSourceDepth(string delivery, bool isHls)
    {
        var plan = Plan(delivery, "hevc", 10);
        plan.EffectiveRecipe = new EffectiveRecipeV3 { VideoCodec = "hevc", AudioCodec = "aac" };
        var route = PlaybackRoutePolicy.Decide(plan, isHls, hlsThroughFfmpeg: isHls);
        Assert.True(route.UseFfmpeg);
        Assert.Equal("rgb10", Engine(plan, route).Target);
    }

    [Fact]
    public void ServerTranscodesAre8Bit()
    {
        var plan = Plan("server_transcode_hls", "hevc", 10, "hdr10");
        plan.EffectiveRecipe = new EffectiveRecipeV3 { VideoCodec = "h264", AudioCodec = "aac", DynamicRange = "sdr" };
        var route = PlaybackRoutePolicy.Decide(plan, true, hlsThroughFfmpeg: true);
        Assert.Equal(("bgra", "nv12"), (Engine(plan, route).Target, Engine(plan, route).Frames));
    }

    [Fact]
    public void UnknownDepthKeepsBgraUnlessTheCatalogKnowsIt()
    {
        var plan = Plan("original_http", "hevc", null);
        var route = PlaybackRoutePolicy.Decide(plan, false);
        var output = Engine(plan, route);
        Assert.Equal(("bgra", "unknown", "unknown depth"), (output.Target, output.Frames, output.Reason));
        FileVersion[] versions = [new() { FileId = "file", VideoTracks = [new VideoTrack { PixelFormat = "yuv420p10le" }] }];
        Assert.Equal("rgb10", Engine(plan, route, versions: versions).Target);
    }
}
