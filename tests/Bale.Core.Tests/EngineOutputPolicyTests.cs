using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>Owner's bit-depth rule: work at source depth, deliver at display depth (AGENTS.md).</summary>
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
        IReadOnlyList<FileVersion>? versions = null, int? displayBitsPerPixel = 30, string ceiling = "display",
        bool stageNv12Dithered = false) =>
        EngineOutputPolicy.Decide(plan, route, versions, stageP010, displayBitsPerPixel, ceiling, stageNv12Dithered);

    private const string DitheredReason = "console stage writes dithered NV12 (SDR 8-bit display)";

    private static (PlaybackPlan Plan, PlaybackRouteDecision Route) Stage(bool toneMap, string codec = "hevc")
    {
        var plan = toneMap ? Plan("original_http", "hevc", 10, "hdr10", 3840, 2160) : Plan("original_http", "hevc", 8, "sdr", 3840, 2160);
        var route = toneMap
            ? PlaybackRoutePolicy.Decide(plan, false, sdrOutput: true)
            : PlaybackRoutePolicy.Decide(plan, false, display: (1920, 1080), sdrDownscale: true);
        Assert.Equal(toneMap, route.ConsoleToneMap);
        Assert.Equal(!toneMap, route.ConsoleDownscale);
        if (codec == "hevc") return (plan, route);
        plan.Source!.VideoCodec = codec;
        return (plan, route with { VideoCodec = codec });
    }

    [Theory]
    [InlineData(true, 24)]
    [InlineData(true, 29)]
    [InlineData(true, null)]
    [InlineData(false, 24)]
    [InlineData(false, null)]
    public void An8BitSdrDisplayAsksTheStageForDitheredNv12(bool toneMap, int? bpp)
    {
        var (plan, route) = Stage(toneMap);
        var output = Engine(plan, route, displayBitsPerPixel: bpp, stageNv12Dithered: true);
        Assert.Equal(("bgra", "nv12", DitheredReason, 8, "SDR 8-bit display"),
            (output.Target, output.Frames, output.Reason, output.StageOutputDepth, output.StageReason));
        Assert.Equal(0u, output.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A10BitDisplayKeepsP010FromTheStage(bool toneMap)
    {
        var (plan, route) = Stage(toneMap);
        var output = Engine(plan, route, displayBitsPerPixel: 30, stageNv12Dithered: true);
        Assert.Equal(("rgb10", "p010", 10, "SDR 10-bit display"),
            (output.Target, output.Frames, output.StageOutputDepth, output.StageReason));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Av1NeverAsksForAn8BitStage(bool toneMap)
    {
        var (plan, route) = Stage(toneMap, "av1");
        var output = Engine(plan, route, displayBitsPerPixel: 24, stageNv12Dithered: true);
        Assert.Equal((10, "AV1 keeps 10-bit output (until rav1g is stable)"), (output.StageOutputDepth, output.StageReason));
        Assert.Equal("p010", output.Frames);
    }

    [Fact]
    public void HdrPassthroughNeverAsksForAn8BitStage()
    {
        // A downscale route over HDR video is not tone-mapped: passthrough keeps 10 bits.
        var (plan, route) = Stage(false);
        plan.Source!.DynamicRange = "hdr10";
        plan.Source.BitDepth = 10;
        var output = Engine(plan, route, displayBitsPerPixel: 24, stageNv12Dithered: true);
        Assert.Equal(("rgb10", "p010", 10, "HDR passthrough"),
            (output.Target, output.Frames, output.StageOutputDepth, output.StageReason));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithoutTheDitherBitOrInSourceModeTheStageKeepsP010(bool toneMap)
    {
        var (plan, route) = Stage(toneMap);
        var old = Engine(plan, route, displayBitsPerPixel: 24);
        Assert.Equal(("bgra", "p010", "SDR 8-bit display: 8-bit output", 10, "bale_gpu without dithered NV12"),
            (old.Target, old.Frames, old.Reason, old.StageOutputDepth, old.StageReason));
        var source = Engine(plan, route, displayBitsPerPixel: 24, ceiling: "source", stageNv12Dithered: true);
        Assert.Equal(("rgb10", "p010", 10, "debug output-depth source keeps P010"),
            (source.Target, source.Frames, source.StageOutputDepth, source.StageReason));
    }

    [Fact]
    public void RoutesWithoutAStageReportNoStageDepth()
    {
        var plan = Plan("original_http", "hevc", 10);
        var output = Engine(plan, PlaybackRoutePolicy.Decide(plan, false), displayBitsPerPixel: 24, stageNv12Dithered: true);
        Assert.Equal((0, "no console stage"), (output.StageOutputDepth, output.StageReason));
        Assert.Equal((0, "no console stage"), (new EngineOutputDecision("bgra", "unknown", "no plan").StageOutputDepth,
            new EngineOutputDecision("bgra", "unknown", "no plan").StageReason));
    }

    [Theory]
    [InlineData("sdr", false, 24, "display", "bgra")]
    [InlineData("sdr", false, 29, "display", "bgra")]
    [InlineData("sdr", false, 30, "display", "rgb10")]
    [InlineData("sdr", false, 36, "display", "rgb10")]
    [InlineData("sdr", false, null, "display", "bgra")]
    [InlineData("hdr10", false, 24, "display", "rgb10")]
    [InlineData("hlg", false, 24, "display", "rgb10")]
    [InlineData("hdr10", false, null, "display", "rgb10")]
    [InlineData("hdr10", true, 24, "display", "bgra")]
    [InlineData("hdr10", true, 30, "display", "rgb10")]
    [InlineData("hlg", true, 24, "display", "bgra")]
    [InlineData("hlg", true, 30, "display", "rgb10")]
    [InlineData("sdr", false, 24, "source", "rgb10")]
    [InlineData("sdr", false, null, "source", "rgb10")]
    [InlineData("hdr10", true, 24, "source", "rgb10")]
    public void P010TargetFollowsTheDisplayUnlessHdrPassesThroughOrSourceModeWins(
        string range, bool toneMapped, int? bpp, string ceiling, string target)
    {
        var plan = Plan("original_http", "hevc", 10, range);
        var route = PlaybackRoutePolicy.Decide(plan, false, sdrOutput: toneMapped);
        Assert.Equal(toneMapped, route.ConsoleToneMap);
        var output = Engine(plan, route, displayBitsPerPixel: bpp, ceiling: ceiling);
        Assert.Equal((target, "p010"), (output.Target, output.Frames));
        if (ceiling == "display" && (range == "sdr" || toneMapped))
            Assert.Equal(bpp >= 30 ? "SDR 10-bit display: 10-bit output" : "SDR 8-bit display: 8-bit output", output.Reason);
    }

    [Fact]
    public void DefaultInputsTreatAnUnknownSdrDisplayAs8Bit()
    {
        var plan = Plan("original_http", "hevc", 10);
        var output = EngineOutputPolicy.Decide(plan, PlaybackRoutePolicy.Decide(plan, false));
        Assert.Equal(("bgra", "p010", "SDR 8-bit display: 8-bit output"), (output.Target, output.Frames, output.Reason));
    }

    [Theory]
    [InlineData("av1")]
    [InlineData(" AV1 ")]
    public void Av1TemporarilyKeeps10BitOutputOnAn8BitDisplay(string codec)
    {
        var plan = Plan("original_http", codec, 10);
        var output = Engine(plan, PlaybackRoutePolicy.Decide(plan, false), displayBitsPerPixel: 24);
        Assert.Equal(("rgb10", "p010", "AV1 keeps 10-bit output (until rav1g is stable)"),
            (output.Target, output.Frames, output.Reason));
    }

    [Theory]
    [InlineData("hevc", 8, "display", "nv12")]
    [InlineData("av1", 8, "display", "nv12")]
    [InlineData("av1", 8, "source", "nv12")]
    [InlineData("hevc", null, "display", "unknown")]
    [InlineData("hevc", null, "source", "unknown")]
    public void ADeepDisplayOrSourceModeNeverRaisesNv12OrUnknownFrames(string codec, int? bits, string ceiling, string frames)
    {
        var plan = Plan("original_http", codec, bits);
        var output = Engine(plan, PlaybackRoutePolicy.Decide(plan, false), displayBitsPerPixel: 30, ceiling: ceiling);
        Assert.Equal(("bgra", frames), (output.Target, output.Frames));
    }

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
    // dav1d CPU output on D3D11 and rav1g both deliver NV12 for 8-bit AV1.
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
        var shallow = Engine(plan, route, displayBitsPerPixel: 24);
        Assert.Equal(("bgra", "p010"), (shallow.Target, shallow.Frames));
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

    [Fact]
    public void EveryEngineOpenPassesThePolicyTarget()
    {
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("var displayBitsPerPixel = DisplayHdrOutput.CurrentBitsPerPixel();", media);
        Assert.Contains("EngineOutputPolicy.Decide(plan, media.Route, attempt.Request.Detail.Versions, ConsoleToneMapRuntime.SupportsP010,", media);
        Assert.Contains("DisplayHdrOutput.CurrentBitsPerPixel(), ceiling, stageNv12Dithered)", media);
        // FFmpeg mounts decide once at configuration: the stage depth key, the guard and the engine share it.
        Assert.Contains("media.EngineOutput = DecideEngineOutput(attempt, media, ConsoleToneMapRuntime.SupportsDitheredNv12);", media);
        Assert.Contains("media.StageOutput8Bit = FfmpegSource.ApplyStageOutputDepth(config, media.Route, media.EngineOutput);", media);
        Assert.Contains("nv12Requested: media.StageOutput8Bit", media);
        Assert.True(media.IndexOf("FfmpegSource.ApplyStageOutputDepth(", StringComparison.Ordinal)
            < media.IndexOf("FFmpegMediaSource.CreateFromInputStreamAsync", StringComparison.Ordinal));
        Assert.Contains("frames={output.Frames} display={display} reason={output.Reason}", media);
        Assert.Contains("target: target);", media);
        var engine = Read("EngineBackend.cs.txt");
        Assert.Contains("bale_gpu_engine_session_open_target(", engine);
        Assert.DoesNotContain("bale_gpu_engine_session_open(", engine);
        Assert.Contains("internal const uint ExpectedAbi = 15;", engine);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
