using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using FFmpegInteropX;

namespace Bale.Core.Tests;

[Collection("Player seek")]
public sealed class WideGamutSdrPlaybackTests
{
    private static FileVersion Version(string codec = "hevc", long? depth = 10, string? primaries = "bt2020",
        string? matrix = "bt2020nc") => new()
    {
        FileId = "played", CodecVideo = codec,
        VideoTracks = [new() { Codec = codec, BitDepth = depth, ColorPrimaries = primaries,
            ColorSpace = matrix, ColorTransfer = "bt2020-10" }],
    };

    private static PlaybackPlan Plan(string codec = "hevc", long? depth = 10) => new()
    {
        Delivery = "original_http", RequestedMediaFileId = "picked", EffectiveMediaFileId = "played",
        Source = new() { MediaFileId = "played", VideoCodec = codec, BitDepth = depth,
            DynamicRange = "sdr", Container = "mp4", AudioCodec = "aac" },
    };

    [Theory]
    [InlineData("hevc", 10, "bt2020", "bt2020nc", true, true)]
    [InlineData("h265", 10, "9", "9", true, true)]
    [InlineData("vp9", 10, "BT2020", "BT2020NC", true, true)]
    [InlineData("hevc", 10, "bt2020", null, true, true)]
    [InlineData("hevc", 10, null, "bt2020nc", true, true)]
    [InlineData("hevc", 10, "bt709", "bt709", true, false)]
    [InlineData("hevc", 10, "bt709", "bt2020nc", true, false)]
    [InlineData("hevc", 10, "bt2020", "bt709", true, false)]
    [InlineData("hevc", 10, null, null, true, false)]
    [InlineData("hevc", 8, "bt2020", "bt2020nc", true, false)]
    [InlineData("vp9", 8, "bt2020", "bt2020nc", true, false)]
    [InlineData("hevc", null, "bt2020", "bt2020nc", true, false)]
    [InlineData("av1", 10, "bt2020", "bt2020nc", true, false)]
    [InlineData("hevc", 10, "bt2020", "bt2020nc", false, false)]
    [InlineData("vp9", 10, "bt2020", "bt2020nc", false, false)]
    public void OnlyTenBitWideGamutOriginalsOnSdrArmTheStage(string codec, int? depth, string? primaries,
        string? matrix, bool sdrOutput, bool expected)
    {
        var route = PlaybackRoutePolicy.Decide(Plan(codec, depth), false, sdrOutput: sdrOutput,
            versions: [Version(codec, depth, primaries, matrix)]);
        Assert.Equal(expected, route.ConsoleToneMap);
        Assert.Equal(expected, route.RequiresD3D11);
        if (expected)
        {
            Assert.True(route.UseFfmpeg);
            Assert.False(route.SoftwareVideo);
            var config = FfmpegSource.Configuration(route);
            Assert.Equal(VideoDecoderMode.Automatic, config.Video.VideoDecoderMode);
            Assert.Equal("1", config.FFmpegOptions["bale_tonemap"]);
            Assert.Equal("SDR / console tone map", DebugHdrPlayback.Output(false, Plan(codec, depth), route.ConsoleToneMap));
        }
        else if (codec == "hevc") Assert.False(route.UseFfmpeg); // Native MP4 stays native.
    }

    [Fact]
    public void ColourFactsMustBelongToThePlayedFileAndBeUnambiguous()
    {
        var plan = Plan();
        var version = Version();
        Assert.False(ConsoleToneMapPolicy.RequiresToneMap(plan, true));
        version.FileId = "picked";
        Assert.False(ConsoleToneMapPolicy.RequiresToneMap(plan, true, [version]));
        version.FileId = "played";
        Assert.True(ConsoleToneMapPolicy.RequiresToneMap(plan, true, [version]));
        plan.EffectiveMediaFileId = null; // Older plans can name the effective file in Source.
        Assert.True(ConsoleToneMapPolicy.RequiresToneMap(plan, true, [version]));
        version.VideoTracks!.Add(new() { Codec = "hevc", BitDepth = 10 });
        Assert.False(ConsoleToneMapPolicy.RequiresToneMap(plan, true, [version]));
    }

    [Theory]
    [InlineData("progressive", false)]
    [InlineData("server_remux_progressive", false)]
    [InlineData("server_transcode_hls", true)]
    public void PackagedSdrOutputsDoNotInheritOriginalColourFacts(string delivery, bool hls)
    {
        var plan = Plan();
        plan.Delivery = delivery;
        Assert.False(PlaybackRoutePolicy.Decide(plan, hls, sdrOutput: true, versions: [Version()]).ConsoleToneMap);
    }

    [Fact]
    public void ExecutorOnlyRuleDoesNotExpandTheServerRangeClaim()
    {
        var version = Version();
        var device = ConsoleEvidence.Default with { DisplayHdrEvidence = "exact", OriginalDynamicRangeMappingSupported = true };
        Assert.False(ConsoleToneMapPolicy.CanManage(version, device));
        var plan = Plan();
        plan.Source!.BitDepth = null; // Catalog track can supply depth missing from the plan.
        Assert.True(ConsoleToneMapPolicy.RequiresToneMap(plan, true, [version]));
        version.VideoTracks![0].PixelFormat = "yuv444p10le";
        Assert.False(ConsoleToneMapPolicy.RequiresToneMap(plan, true, [version]));
    }

    [Theory]
    [InlineData("System", null, null, false)]
    [InlineData("Software", null, null, false)]
    [InlineData("Auto", "Off", "0", false)]
    [InlineData("Auto", "Hable", "2", true)]
    public void DebugDecoderAndToneMapOverridesStillWin(string decoder, string? curve, string? option, bool guard)
    {
        var route = PlaybackRoutePolicy.Decide(Plan(), false, sdrOutput: true, versions: [Version()]);
        var tuning = new DebugFfmpegInputTuning(Decoder: Enum.Parse<DebugFfmpegDecoder>(decoder),
            ToneMap: curve is null ? null : Enum.Parse<DebugToneMap>(curve));
        Assert.Equal(option, FfmpegSource.Configuration(route, tuning).FFmpegOptions.GetValueOrDefault("bale_tonemap"));
        Assert.Equal(guard, tuning.RequiresToneMapGuard(route));
    }

    [Fact]
    public void FailureUsesTheExistingRangeRecoveryAndStillRejectsTheSameOriginal()
    {
        var plan = Plan();
        plan.PlanId = "plan";
        plan.PlanAttemptKey = "key";
        var start = PlaybackStartBuilder.Build("played", "profile", "attempt", null, new(),
            ConsoleEvidence.Default, PlaybackTestSupport.Identity);
        var route = PlaybackRoutePolicy.Decide(plan, false, sdrOutput: true, versions: [Version()]);
        var recovery = PlaybackReplan.ForFailure(start, plan, "mount", [], 1, 25,
            PlaybackReplan.Classify(null, false, false), route);
        Assert.Same(start.ClientCapabilities, recovery.ClientCapabilities);
        Assert.Equal("Console tone mapping failed; server tone mapping required.", recovery.Failure!.Message);
        Assert.True(ConsoleToneMapPolicy.RequiresToneMap(plan, true, [Version()]));
        plan.Delivery = "server_transcode_hls";
        Assert.False(ConsoleToneMapPolicy.RequiresToneMap(plan, true, [Version()]));
    }
}
