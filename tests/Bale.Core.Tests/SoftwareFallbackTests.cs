using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using FFmpegInteropX;

namespace Bale.Core.Tests;

// Phase A of docs/software-decode.md (owner, 2026-11-24): H.264/HEVC/VP9 the console's hardware decoder can't take decode
// in FFmpeg software inside the console-measured envelopes; everything else is refused and the replan withdraws the
// codec. FfmpegSource logs through the shared stand-in, so this stays in the collection of the other log users.
[Collection("Player seek")]
public sealed class SoftwareFallbackTests
{
    // What a Series console's decoder probe reports (HardwareDecodeClaimsTests): H.264 stops at 1080p.
    internal static readonly HardwareDecodeLimits Series = new(H264: new(1920, 1080), HevcMain: new(7680, 4320),
        HevcMain10: new(7680, 4320), Vp9Profile0: new(3840, 2160), Vp9Profile2: new(3840, 2160), Vc1: new(1920, 1080),
        Mpeg2: new(1920, 1080));

    internal static DeviceCapabilities Device { get; } = ConsoleEvidence.ForHardware(Series);

    /// <summary>A delivered original: the plan's source descriptor and the catalog version the route reads.</summary>
    internal static (PlaybackPlan Plan, FileVersion[] Versions) File(string codec, string? profile, int depth, int width, int height,
        double? fps, string? pixelFormat, string range = "sdr", string delivery = "original_http")
    {
        var plan = PlaybackRoutePolicyTests.Plan(delivery, codec, "aac");
        plan.Source!.MediaFileId = "file";
        plan.Source.VideoProfile = profile;
        plan.Source.BitDepth = depth;
        plan.Source.Width = width;
        plan.Source.Height = height;
        plan.Source.FrameRate = fps;
        plan.Source.DynamicRange = range;
        var track = new VideoTrack
        {
            Codec = codec, Profile = profile, BitDepth = depth, Width = width, Height = height, PixelFormat = pixelFormat,
            FrameRate = fps?.ToString(CultureInfo.InvariantCulture),
        };
        return (plan, [new FileVersion { FileId = "file", CodecVideo = codec, VideoTracks = [track] }]);
    }

    /// <summary>
    /// The player's call (PlayerPage.Media): probed hardware entries, the claims the attempt holds now, the display (unknown
    /// unless given), and the pinned wrapper and bale_gpu, which run the stage on software frames (phase B) unless a test
    /// says otherwise.
    /// </summary>
    internal static PlaybackRouteDecision Decide((PlaybackPlan Plan, FileVersion[] Versions) file,
        IReadOnlyList<VideoDecodeCapability>? claims = null, bool sdrOutput = false, (int Width, int Height)? display = null,
        bool sdrDownscale = true, bool softwareStage = true)
    {
        claims ??= Device.VideoDecode;
        return PlaybackRoutePolicy.Decide(file.Plan, false, sdrOutput: sdrOutput, versions: file.Versions,
            hardwareDecode: Device.VideoDecode.Where(decode => decode.Hardware).ToArray(), display: display,
            sdrDownscale: sdrDownscale, softwareDecode: claims, softwareStage: softwareStage);
    }

    [Fact]
    public void TheClaimsCarryEachEnvelopeAsSiloServerMatchesIt()
    {
        var fallback = Device.VideoDecode
            .Where(decode => !decode.Hardware && SoftwareDecodePolicy.SoftwareFallbackVideoCodecs.Contains(decode.Codec)).ToArray();
        Assert.Equal(new[] { "h264", "hevc", "vp9" }, SoftwareDecodePolicy.SoftwareFallbackVideoCodecs);
        Assert.Equal(new[]
        {
            // 4:2:0 above 8 bits up to 60 fps; 4:2:2 / 4:4:4 up to 30 (the console's measurements, owner).
            "h264|high 10|8,10|1920x1080@60",
            "hevc|rext|12|1920x1080@60",
            "h264|high 4:2:2,high 4:4:4 predictive|8,10|1920x1080@30",
            "hevc|rext|8,10,12|1920x1080@30",
            "vp9|profile 1,profile 3|8,10,12|1920x1080@30",
            // Phase B: 8-bit H.264 past the hardware's 1080p, up to 4K30 (2.87 cores at 50 Mbps), through the stage.
            "h264|constrained baseline,baseline,main,high|8|3840x2160@30",
        }, fallback.Select(Shape));
        Assert.All(fallback, decode =>
        {
            Assert.Equal("FFmpeg", decode.DecoderName);
            Assert.False(decode.Hardware);
            Assert.Empty(decode.Levels);
            // None was approved at 1080p (the console clips ran at 20-50 Mbps); the 4K entry has the 50 Mbps measured.
            Assert.Equal(decode.MaxHeight == 2160 ? 50_000 : null, decode.MaxBitrateKbps);
        });
        // The start request opts into software decode, and every claim names a bundled FFmpeg decoder.
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null, PlaybackSelection.Auto, Device, PlaybackTestSupport.Identity);
        Assert.Contains(PlaybackProtocol.SoftwareVideoDecodeFeature, start.ClientFeatures);
        Assert.Equal(Device.VideoDecode, start.ClientCapabilities.VideoDecode);
        // Claimed whatever the probe found: the software decoder doesn't depend on it.
        Assert.Equal(fallback.Select(Shape), ConsoleEvidence.Default.VideoDecode.Where(decode => !decode.Hardware
            && SoftwareDecodePolicy.SoftwareFallbackVideoCodecs.Contains(decode.Codec)).Select(Shape));
    }

    [Theory]
    // codec, profile, depth, width, height, fps, pixel format, then: the server admits it (Bale's mirror of its
    // matching), the route is software, the player decodes it (route.SoftwareFallback).
    // Inside the envelopes: planned direct and decoded in FFmpeg software.
    [InlineData("h264", "high 10", 10, 1920, 1080, 23.976, "yuv420p10le", true, true, true)]
    [InlineData("h264", "High 10", 10, 1920, 1080, 60.0, "yuv420p10le", true, true, true)]
    [InlineData("h264", "high 4:2:2", 10, 1920, 1080, 29.97, "yuv422p10le", true, true, true)]
    [InlineData("h264", "high 4:4:4 predictive", 8, 1280, 720, 30.0, "yuv444p", true, true, true)]
    [InlineData("hevc", "rext", 10, 1920, 1080, 29.97, "yuv422p10le", true, true, true)]
    [InlineData("hevc", "Rext", 12, 1920, 1080, 23.976, "yuv420p12le", true, true, true)]
    [InlineData("hevc", "rext", 12, 1920, 1080, 60.0, "yuv420p12le", true, true, true)]
    [InlineData("vp9", "profile 1", 8, 1920, 1080, 29.97, "yuv444p", true, true, true)]
    [InlineData("vp9", "profile 3", 10, 1920, 1080, 25.0, "yuv422p10le", true, true, true)]
    // Outside them, where no entry admits the file: the server transcodes up front (4K30 H.264 8-bit is SoftwareStageTests').
    [InlineData("h264", "high", 8, 3840, 2160, 60.0, "yuv420p", false, true, false)]
    [InlineData("h264", "high 10", 10, 3840, 2160, 23.976, "yuv420p10le", false, true, false)]
    [InlineData("h264", "high 4:2:2", 10, 1920, 1080, 60.0, "yuv422p10le", false, true, false)]
    [InlineData("h264", "high 10", 10, 1920, 1080, 120.0, "yuv420p10le", false, true, false)]
    // Outside them, but the profile-blind hardware entry (or the chroma-blind 12-bit one) admits the file: planned
    // direct, refused by the player, and the replan withdraws the codec (fix 2), so the server transcodes.
    [InlineData("h264", "high 4:2:2", 8, 1920, 1080, 60.0, "yuv422p", true, true, false)]
    [InlineData("hevc", "rext", 10, 1920, 1080, 60.0, "yuv422p10le", true, true, false)]
    [InlineData("hevc", "rext", 10, 3840, 2160, 29.97, "yuv422p10le", true, true, false)]
    [InlineData("hevc", "rext", 12, 1920, 1080, 60.0, "yuv422p12le", true, true, false)]
    [InlineData("vp9", "profile 1", 8, 1920, 1080, 60.0, "yuv444p", true, true, false)]
    [InlineData("vp9", "profile 3", 10, 3840, 2160, 30.0, "yuv444p10le", true, true, false)]
    // The hardware takes these, as before.
    [InlineData("hevc", "main 10", 10, 3840, 2160, 60.0, "yuv420p10le", true, false, false)]
    [InlineData("h264", "high", 8, 1920, 1080, 60.0, "yuv420p", true, false, false)]
    [InlineData("vp9", "profile 2", 10, 3840, 2160, 60.0, "yuv420p10le", true, false, false)]
    public void TheServerAndThePlayerSplitTheWorkAsPlanned(string codec, string profile, int depth, int width, int height, double fps,
        string pixelFormat, bool serverAdmits, bool softwareRoute, bool fallback)
    {
        var file = File(codec, profile, depth, width, height, fps, pixelFormat);
        Assert.Equal(serverAdmits, PlaybackFormatting.Decodes(file.Versions[0], Device.VideoDecode));
        var route = Decide(file);
        Assert.True(route.UseFfmpeg);
        Assert.Equal(softwareRoute, route.SoftwareVideo);
        Assert.Equal(fallback, route.SoftwareFallback);
        Assert.False(route.ConsoleStage);
        if (!softwareRoute)
        {
            Assert.NotEqual(VideoDecoderMode.ForceFFmpegSoftwareDecoder, FfmpegSource.Configuration(route).Video.VideoDecoderMode);
            return;
        }
        if (fallback)
        {
            // The production gate: FFmpeg software, the ordinary software defaults, no stage and no AV1-only option.
            var config = FfmpegSource.Configuration(route);
            Assert.Equal(VideoDecoderMode.ForceFFmpegSoftwareDecoder, config.Video.VideoDecoderMode);
            Assert.Equal("16", config.FFmpegOptions["bale_decode_ahead_frames"]);
            Assert.Equal(1000, SoftwareDecodeTuning.BufferTimeMs(route.SoftwareVideo, width: route.VideoWidth, height: route.VideoHeight));
            Assert.True(config.General.FastSeek);
            foreach (var key in new[] { "bale_tonemap", "bale_film_grain_mode", "bale_av1_decoder", "bale_dav1d_export_film_grain" })
                Assert.DoesNotContain(key, config.FFmpegOptions.Keys);
            // Release asks for no d3d11va pool on a software route (only debug ffmpeg-tune pool= ever sends the key there).
            Assert.Contains("if (!route.SoftwareVideo && route.VideoCodec is \"hevc\" or \"h264\" or \"vp9\")",
                DebugFfmpegDecoderTests.ProductionSource());
            // The opened stream must be FFmpeg's software decoder for this codec, as for software AV1.
            FfmpegSource.ValidateVideoDecoders(Stream(codec, DecoderEngine.FFmpegSoftwareDecoder), route);
            Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(Stream(codec, DecoderEngine.SystemDecoder), route));
            return;
        }
        Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(route));
        // The refused open's replan withdraws the codec, so the server can't plan a copy of the stream again.
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null, PlaybackSelection.Auto, Device, PlaybackTestSupport.Identity);
        var replan = PlaybackReplan.ForFailure(start, file.Plan, "attempt", [], 1, 0,
            new PlaybackFailureV3("decoder_failure", "open failed", "FFmpegInteropX"), route);
        Assert.DoesNotContain(replan.ClientCapabilities.VideoDecode, decode => decode.Codec == codec);
        Assert.DoesNotContain(codec, replan.ClientCapabilities.CodecsVideo);
        Assert.False(PlaybackFormatting.Decodes(file.Versions[0], replan.ClientCapabilities.VideoDecode));
    }

    [Fact]
    public void AFailedFallbackWithdrawsTheCodecAndEverySoftwareEntryAndTheNextRouteTakesNone()
    {
        var file = File("h264", "high 10", 10, 1920, 1080, 23.976, "yuv420p10le");
        var route = Decide(file);
        Assert.True(route.SoftwareFallback);
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null, PlaybackSelection.Auto, Device, PlaybackTestSupport.Identity);
        var replan = PlaybackReplan.ForFailure(start, file.Plan, "attempt", [], 1, 0,
            new PlaybackFailureV3("decoder_failure", "decode failed", "FFmpegInteropX"), route);
        Assert.DoesNotContain(replan.ClientCapabilities.VideoDecode, decode => decode.Codec == "h264");
        Assert.DoesNotContain(replan.ClientCapabilities.VideoDecode, decode => !decode.Hardware);
        Assert.DoesNotContain(PlaybackProtocol.SoftwareVideoDecodeFeature, replan.ClientFeatures);
        Assert.All(replan.ClientPlaybackContext.Deliveries.Values, delivery => Assert.DoesNotContain("h264", delivery.VideoCodecs));
        // The player re-decides every mount with the claims as they stand: after that replan nothing admits a fallback.
        Assert.False(Decide(file, replan.ClientCapabilities.VideoDecode).SoftwareFallback);
        // A start that ran out of time withdraws no claim (silo-apple b9446d5d); a next copy route may take the fallback again.
        var budget = PlaybackReplan.ForFailure(start, file.Plan, "attempt", [], 1, 0, PlaybackStartBudget.Failure, route);
        Assert.True(Decide(file, budget.ClientCapabilities.VideoDecode).SoftwareFallback);
    }

    [Fact]
    public void WithoutSoftwareVideoClaimsNothingFallsBack()
    {
        // Xbox One, or a CPU without AVX2 (SoftwareVideoSupported): the hardware entries stay, the software ones go.
        var device = SoftwareDecodePolicy.ForSoftwareVideo(Device, supported: false);
        Assert.DoesNotContain(device.VideoDecode, decode => !decode.Hardware);
        var file = File("h264", "high 10", 10, 1920, 1080, 23.976, "yuv420p10le");
        Assert.False(PlaybackFormatting.Decodes(file.Versions[0], device.VideoDecode));
        var route = Decide(file, device.VideoDecode);
        Assert.True(route.SoftwareVideo);
        Assert.False(route.SoftwareFallback);
        Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(route));
        // Debug "ffmpeg off" (WithoutFallbacks) drops them too.
        Assert.DoesNotContain(SoftwareDecodePolicy.WithoutFallbacks(Device).VideoDecode, decode => !decode.Hardware);
    }

    [Fact]
    public void NoFallbackWithoutTheFilesFacts()
    {
        // Without the delivered file's catalog version (pixel format, rate), or with another file's, the route stays refused.
        var file = File("h264", "high 10", 10, 1920, 1080, 23.976, "yuv420p10le");
        var hardware = Device.VideoDecode.Where(decode => decode.Hardware).ToArray();
        Assert.False(PlaybackRoutePolicy.Decide(file.Plan, false, hardwareDecode: hardware, softwareDecode: Device.VideoDecode).SoftwareFallback);
        file.Versions[0].FileId = "another";
        Assert.False(Decide(file).SoftwareFallback);
    }

    [Fact]
    public void ARemuxOfAnEnvelopeFileTakesTheSameDecoder()
    {
        // The progressive remux copies the stream: the route judges it on the source file's version, as the original.
        var file = File("hevc", "rext", 10, 1920, 1080, 29.97, "yuv422p10le", delivery: "server_remux_progressive");
        var route = Decide(file);
        Assert.True(route.UseFfmpeg);
        Assert.True(route.SoftwareVideo);
        Assert.True(route.SoftwareFallback);
    }

    [Fact]
    public void DisplayMatchingCountsAVersionOnlyWhereTheConsolePlaysIt()
    {
        var match = new DisplayVersionMatch(1080, Device.VideoDecode);
        // A 1080p Hi10P file plays in the software fallback: Auto keeps it over a smaller hardware version.
        var hi10 = new[] { Version("hi10", "h264", "high 10", 10, 1920, 1080, "24000/1001", "yuv420p10le"),
            Version("720", "h264", "high", 8, 1280, 720, "24000/1001", "yuv420p") };
        Assert.Same(hi10[0], PlaybackFormatting.SelectVersion(hi10, match));
        // A 1080p60 HEVC 4:2:2 file passes the profile-blind hardware entry but plays nowhere on the console (the player
        // refuses it and the server transcodes): Auto takes the version the console plays.
        var rext = new[] { Version("rext", "hevc", "rext", 10, 1920, 1080, "60", "yuv422p10le"),
            Version("720", "h264", "high", 8, 1280, 720, "60", "yuv420p") };
        Assert.Same(rext[1], PlaybackFormatting.SelectVersion(rext, match));
        // At 30 fps the same file plays in the fallback.
        rext[0].VideoTracks![0].FrameRate = "30000/1001";
        Assert.Same(rext[0], PlaybackFormatting.SelectVersion(rext, match));
        // The hardware takes the files it decodes, as before.
        var hevc = new[] { Version("main10", "hevc", "main 10", 10, 1920, 1080, "60", "yuv420p10le"),
            Version("720", "h264", "high", 8, 1280, 720, "60", "yuv420p") };
        Assert.Same(hevc[0], PlaybackFormatting.SelectVersion(hevc, match));
    }

    private static FileVersion Version(string id, string codec, string profile, int depth, int width, int height, string frameRate,
        string pixelFormat) => new()
    {
        FileId = id,
        Resolution = height >= 1080 ? "1080p" : "720p",
        CodecVideo = codec,
        VideoTracks = [new VideoTrack { Codec = codec, Profile = profile, BitDepth = depth, Width = width, Height = height,
            FrameRate = frameRate, PixelFormat = pixelFormat }],
    };

    private static string Shape(VideoDecodeCapability decode) => string.Create(CultureInfo.InvariantCulture,
        $"{decode.Codec}|{string.Join(",", decode.Profiles)}|{string.Join(",", decode.BitDepths)}|{decode.MaxWidth}x{decode.MaxHeight}@{decode.MaxFrameRate}");

    private static FFmpegMediaSource Stream(string codec, DecoderEngine engine)
    {
        var source = new FFmpegMediaSource();
        source.VideoStreams.Add(new() { CodecName = codec, DecoderEngine = engine });
        return source;
    }
}
