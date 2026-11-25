using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using FFmpegInteropX;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Bale.Core.Tests;

// Phase B of docs/software-decode.md (owner, 2026-11-24; wrapper bale.80+): FFmpeg software frames take the console stage
// (the tone map, the downscale to the display) like d3d11va frames, so Media Foundation never scales a software frame above
// 1080p down. FfmpegSource logs through the shared stand-in, so this stays in the collection of the other log users.
[Collection("Player seek")]
public sealed class SoftwareStageTests
{
    private static readonly (int Width, int Height) Hd = (1920, 1080);
    private static readonly (int Width, int Height) Hd720 = (1280, 720);
    private static readonly (int Width, int Height) Uhd = (3840, 2160);

    [Theory]
    // The phase A envelopes at 1080p on a 720p display.
    [InlineData("h264", "high 10", 10, 23.976, "yuv420p10le")]
    [InlineData("h264", "high 4:2:2", 10, 29.97, "yuv422p10le")]
    [InlineData("hevc", "rext", 10, 29.97, "yuv422p10le")]
    [InlineData("hevc", "rext", 12, 23.976, "yuv420p12le")]
    [InlineData("vp9", "profile 1", 8, 29.97, "yuv444p")]
    public void SoftwareFramesLargerThanTheDisplayTakeTheConsoleDownscale(string codec, string profile, int depth, double fps,
        string pixelFormat)
    {
        var file = SoftwareFallbackTests.File(codec, profile, depth, 1920, 1080, fps, pixelFormat);
        var route = SoftwareFallbackTests.Decide(file, display: Hd720);
        Assert.True(route.SoftwareVideo && route.SoftwareFallback);
        Assert.True(route.ConsoleDownscale && route.ConsoleStage);
        Assert.False(route.ConsoleToneMap);
        var config = FfmpegSource.Configuration(route);
        // FFmpeg software (never Automatic, which could pick d3d11va), with the stage's key.
        Assert.Equal(VideoDecoderMode.ForceFFmpegSoftwareDecoder, config.Video.VideoDecoderMode);
        Assert.Equal("1", config.FFmpegOptions["bale_tonemap"]);
        FfmpegSource.ApplyDisplaySize(config, route, Hd720);
        Assert.Equal("1280", config.FFmpegOptions["bale_tonemap_output_width"]);
        Assert.Equal("720", config.FFmpegOptions["bale_tonemap_output_height"]);
        // The stage's stream is FFmpeg's software decoder; d3d11va or the system decoder fail the open.
        FfmpegSource.ValidateVideoDecoders(Stream(codec, DecoderEngine.FFmpegSoftwareDecoder), route);
        var error = Assert.Throws<InvalidDataException>(() =>
            FfmpegSource.ValidateVideoDecoders(Stream(codec, DecoderEngine.FFmpegD3D11HardwareDecoder), route));
        Assert.Equal("The software stage requires FFmpeg software decoding.", error.Message);
        Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(Stream(codec, DecoderEngine.SystemDecoder), route));

        // Without the stage on software frames (an older wrapper), or once this attempt declined the downscale, a 1080p
        // software frame goes to Media Foundation as in phase A: it scales it, as it does a declined hardware stage's.
        foreach (var older in new[]
        {
            SoftwareFallbackTests.Decide(file, display: Hd720, softwareStage: false),
            SoftwareFallbackTests.Decide(file, display: Hd720, sdrDownscale: false),
        })
        {
            Assert.True(older.SoftwareFallback);
            Assert.False(older.ConsoleStage);
            Assert.DoesNotContain("bale_tonemap", FfmpegSource.Configuration(older).FFmpegOptions.Keys);
        }
        // A display that fits the video needs no stage.
        Assert.False(SoftwareFallbackTests.Decide(file, display: Hd).ConsoleStage);
    }

    [Fact]
    public void AFrameAbove1080pNeverReachesMediaFoundationLarger()
    {
        // The 4K30 H.264 envelope (phase B); any software entry above 1080p takes this rule.
        var claims = SoftwareFallbackTests.Device.VideoDecode;
        var file = SoftwareFallbackTests.File("h264", "high", 8, 3840, 2160, 29.97, "yuv420p");

        // A 1080p display: only through the stage's downscale.
        var staged = SoftwareFallbackTests.Decide(file, claims, display: Hd);
        Assert.True(staged.SoftwareVideo && staged.SoftwareFallback && staged.ConsoleDownscale);
        var config = FfmpegSource.Configuration(staged);
        Assert.Equal("1", config.FFmpegOptions["bale_tonemap"]);
        FfmpegSource.ApplyDisplaySize(config, staged, Hd);
        Assert.Equal("1920", config.FFmpegOptions["bale_tonemap_output_width"]);
        Assert.Equal("1080", config.FFmpegOptions["bale_tonemap_output_height"]);

        // Declined (the remount after a declined stage), no stage on software frames, or an unknown display: refused, so the
        // open fails before a decoder exists and the replan withdraws the codec.
        foreach (var refused in new[]
        {
            SoftwareFallbackTests.Decide(file, claims, display: Hd, sdrDownscale: false),
            SoftwareFallbackTests.Decide(file, claims, display: Hd, softwareStage: false),
            SoftwareFallbackTests.Decide(file, claims),
        })
        {
            Assert.True(refused.SoftwareVideo);
            Assert.False(refused.SoftwareFallback);
            Assert.False(refused.ConsoleStage);
            Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(refused));
        }

        // A 4K display shows the frames at their size: no stage, and Media Foundation scales nothing.
        var native = SoftwareFallbackTests.Decide(file, claims, display: Uhd);
        Assert.True(native.SoftwareFallback);
        Assert.False(native.ConsoleStage);
        Assert.DoesNotContain("bale_tonemap", FfmpegSource.Configuration(native).FFmpegOptions.Keys);
    }

    [Theory]
    // codec, profile, size, fps, kbps, then: the server admits it (Bale's mirror), plays through the stage on a 1080p TV.
    [InlineData("high", 3840, 2160, 29.97, 50_000, true)]
    [InlineData("main", 2560, 1440, 30.0, 20_000, true)]
    [InlineData("constrained baseline", 3840, 2160, 25.0, null, true)]
    // Above the measured 50 Mbps, above 30 fps, a 10-bit or 4:2:2 profile, or wider than 3840: the server transcodes.
    [InlineData("high", 3840, 2160, 29.97, 60_000, false)]
    [InlineData("high", 3840, 2160, 59.94, 20_000, false)]
    [InlineData("high 10", 3840, 2160, 23.976, 20_000, false)]
    [InlineData("high 4:2:2", 3840, 2160, 23.976, 20_000, false)]
    [InlineData("high", 4096, 2160, 24.0, 20_000, false)]
    public void TheUhdH264EnvelopePlaysOnlyThroughTheStage(string profile, int width, int height, double fps, int? kbps, bool admitted)
    {
        var depth = profile.Contains("10") || profile.Contains("4:2:2") ? 10 : 8;
        var file = SoftwareFallbackTests.File("h264", profile, depth, width, height, fps,
            profile.Contains("4:2:2") ? "yuv422p10le" : depth == 10 ? "yuv420p10le" : "yuv420p");
        file.Versions[0].VideoTracks![0].Bitrate = kbps;
        var claims = SoftwareFallbackTests.Device.VideoDecode;
        Assert.Equal(admitted, PlaybackFormatting.Decodes(file.Versions[0], claims));
        var route = SoftwareFallbackTests.Decide(file, display: Hd);
        Assert.True(route.SoftwareVideo);
        Assert.Equal(admitted, route.SoftwareFallback);
        // On a 1080p TV every such route takes the downscale; only an admitted one opens.
        Assert.True(route.ConsoleDownscale);
        if (!admitted)
        {
            Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(route));
            return;
        }
        var config = FfmpegSource.Configuration(route);
        Assert.Equal(VideoDecoderMode.ForceFFmpegSoftwareDecoder, config.Video.VideoDecoderMode);
        Assert.Equal("1", config.FFmpegOptions["bale_tonemap"]);
        // Above 1080p: the software tuning for large frames (8 frames decode-ahead, 500 ms buffer).
        Assert.Equal("8", config.FFmpegOptions["bale_decode_ahead_frames"]);
        Assert.Equal(500, SoftwareDecodeTuning.BufferTimeMs(route.SoftwareVideo, width: route.VideoWidth, height: route.VideoHeight));
        // A failed decode withdraws H.264 and every software entry on the single replan, so the server transcodes.
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null, PlaybackSelection.Auto, SoftwareFallbackTests.Device,
            PlaybackTestSupport.Identity);
        var replan = PlaybackReplan.ForFailure(start, file.Plan, "attempt", [], 1, 0,
            new PlaybackFailureV3("decoder_failure", "decode failed", "FFmpegInteropX"), route);
        Assert.False(PlaybackFormatting.Decodes(file.Versions[0], replan.ClientCapabilities.VideoDecode));
    }

    [Fact]
    public void TheUhdEntryIsClaimedOnlyWhereTheStageTakesSoftwareFrames()
    {
        var device = SoftwareFallbackTests.Device;
        Assert.Contains(device.VideoDecode, decode => decode is { Codec: "h264", Hardware: false, MaxHeight: 2160 });
        // An older wrapper or a bale_gpu without the SDR downscale: the entry goes, the 1080p envelopes stay.
        var without = SoftwareDecodePolicy.ForSoftwareStage(device, supported: false);
        Assert.Equal(device.VideoDecode.Where(decode => decode.MaxHeight != 2160 || decode.Hardware), without.VideoDecode);
        Assert.Equal(device.VideoCodecs, without.VideoCodecs);
        Assert.Equal(device.VideoDecode, SoftwareDecodePolicy.ForSoftwareStage(device, supported: true).VideoDecode);
        var file = SoftwareFallbackTests.File("h264", "high", 8, 3840, 2160, 29.97, "yuv420p");
        Assert.False(PlaybackFormatting.Decodes(file.Versions[0], without.VideoDecode));
        // The player's claims do this from the runtime and the pinned wrapper (PlaybackClaims.DetectAsync).
        var claims = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PlaybackClaims.cs.txt"));
        Assert.Contains("SoftwareDecodePolicy.ForSoftwareStage(capabilities, softwareStage && ConsoleToneMapRuntime.SupportsSdrDownscale)", claims);
        Assert.Contains("SoftwareDynamicRangeMappingSupported = softwareStage,", claims);
    }

    [Fact]
    public void AutoNeverPrefersTheSoftwareUhdDecode()
    {
        // A 4K TV: the 4K H.264 version plays only in software (~3 cores), so Auto keeps the 1080p one the hardware takes.
        var match = new DisplayVersionMatch(2160, SoftwareFallbackTests.Device.VideoDecode);
        FileVersion Version(string id, int width, int height) => new()
        {
            FileId = id, Resolution = height >= 2160 ? "4K" : "1080p", CodecVideo = "h264",
            VideoTracks = [new VideoTrack { Codec = "h264", Profile = "high", BitDepth = 8, Width = width, Height = height,
                FrameRate = "30000/1001", PixelFormat = "yuv420p" }],
        };
        var versions = new[] { Version("uhd", 3840, 2160), Version("hd", 1920, 1080) };
        Assert.Same(versions[1], PlaybackFormatting.SelectVersion(versions, match));
    }

    [Fact]
    public void HdrSoftwareFilesArePromisedTheConsoleToneMap()
    {
        var sdrTv = SoftwareFallbackTests.Device with
        {
            DisplayHdrEvidence = "exact", OriginalDynamicRangeMappingSupported = true, SoftwareDynamicRangeMappingSupported = true,
        };
        FileVersion Hdr(string codec, string profile, int depth, double fps, string pixelFormat, string transfer = "smpte2084")
        {
            var version = SoftwareFallbackTests.File(codec, profile, depth, 1920, 1080, fps, pixelFormat).Versions[0];
            var track = version.VideoTracks![0];
            track.ColorTransfer = transfer;
            track.ColorPrimaries = "bt2020";
            track.ColorSpace = "bt2020nc";
            return version;
        }
        // Inside a software envelope: PQ or HLG, any chroma, 10 or 12 bits (the stage takes swscale's P010).
        foreach (var version in new[]
        {
            Hdr("hevc", "rext", 10, 29.97, "yuv422p10le"),
            Hdr("hevc", "rext", 12, 23.976, "yuv420p12le"),
            Hdr("h264", "high 4:2:2", 10, 29.97, "yuv422p10le", "arib-std-b67"),
            Hdr("h264", "high 10", 10, 59.94, "yuv420p10le"),
            Hdr("vp9", "profile 3", 10, 25.0, "yuv422p10le"),
        })
        {
            Assert.True(ConsoleToneMapPolicy.CanManage(version, sdrTv), version.VideoTracks![0].Profile);
            // Not without the stage on software frames, nor without the software entries.
            Assert.False(ConsoleToneMapPolicy.CanManage(version, sdrTv with { SoftwareDynamicRangeMappingSupported = false }));
            Assert.False(ConsoleToneMapPolicy.CanManage(version, SoftwareDecodePolicy.ForSoftwareVideo(sdrTv, supported: false)));
        }
        // Outside the envelopes (4:2:2 at 60 fps), SDR transfer, 8-bit, or Dolby Vision: not promised.
        Assert.False(ConsoleToneMapPolicy.CanManage(Hdr("hevc", "rext", 10, 59.94, "yuv422p10le"), sdrTv));
        Assert.False(ConsoleToneMapPolicy.CanManage(Hdr("hevc", "rext", 10, 29.97, "yuv422p10le", "bt709"), sdrTv));
        Assert.False(ConsoleToneMapPolicy.CanManage(Hdr("hevc", "rext", 8, 29.97, "yuv422p"), sdrTv));
        var dv = Hdr("hevc", "rext", 10, 29.97, "yuv422p10le");
        dv.VideoTracks![0].DolbyVision = "profile 8";
        dv.VideoTracks[0].DvProfile = 8;
        Assert.False(ConsoleToneMapPolicy.CanManage(dv, sdrTv));
        // The hardware path is unchanged: HEVC Main 10 4:2:0 PQ without the software flag.
        Assert.True(ConsoleToneMapPolicy.CanManage(Hdr("hevc", "main 10", 10, 23.976, "yuv420p10le"),
            sdrTv with { SoftwareDynamicRangeMappingSupported = false }));

        // The start request for that version carries the promise, so the server plans it direct.
        var rext = Hdr("hevc", "rext", 10, 29.97, "yuv422p10le");
        var detail = new ItemDetail("item", "movie", "", null, null, null, null, null, [], null, [], [], null, [], null,
            "item", null, null, new(false, false, false), []) { Versions = [rext] };
        var start = PlaybackStartBuilder.Build(detail, PlaybackSelection.Auto, "profile", "attempt", 0, sdrTv, PlaybackTestSupport.Identity);
        Assert.Contains(PlaybackProtocol.ClientManagedDynamicRangeClaim, start.ClientPlaybackContext.Deliveries["original_http"].ValidatedClaims);
    }

    [Fact]
    public void HdrSoftwareFramesTakeTheToneMapFromWrapperBale80()
    {
        // HDR HEVC RExt 4:2:2 on an SDR TV: the stage tone-maps software frames as it does d3d11va's.
        var hdr = SoftwareFallbackTests.File("hevc", "rext", 10, 1920, 1080, 29.97, "yuv422p10le", range: "hdr10");
        var route = SoftwareFallbackTests.Decide(hdr, sdrOutput: true, display: Hd);
        Assert.True(route.SoftwareVideo && route.SoftwareFallback && route.ConsoleToneMap);
        Assert.False(route.ConsoleDownscale);
        var config = FfmpegSource.Configuration(route);
        Assert.Equal(VideoDecoderMode.ForceFFmpegSoftwareDecoder, config.Video.VideoDecoderMode);
        Assert.Equal("1", config.FFmpegOptions["bale_tonemap"]);
        FfmpegSource.ValidateVideoDecoders(Stream("hevc", DecoderEngine.FFmpegSoftwareDecoder), route);
        // On a smaller display the tone map writes display-sized output, as on hardware.
        var small = SoftwareFallbackTests.Decide(hdr, sdrOutput: true, display: Hd720);
        var smallConfig = FfmpegSource.Configuration(small);
        FfmpegSource.ApplyDisplaySize(smallConfig, small, Hd720);
        Assert.Equal("1280", smallConfig.FFmpegOptions["bale_tonemap_output_width"]);

        // Without the stage on software frames (an older wrapper) the route is refused, as in phase A.
        var older = SoftwareFallbackTests.Decide(hdr, sdrOutput: true, display: Hd, softwareStage: false);
        Assert.True(older.ConsoleToneMap);
        Assert.False(older.SoftwareFallback);
        Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(older));
        // An HDR TV needs no stage: FFmpeg software with the stream's HDR10 colour.
        var hdrTv = SoftwareFallbackTests.Decide(hdr, sdrOutput: false, display: Hd);
        Assert.True(hdrTv.SoftwareFallback);
        Assert.False(hdrTv.ConsoleStage);
    }

    [Fact]
    public void TheGuardTakesTheStageOutputFromTheSoftwareStream()
    {
        var failures = 0;
        var software = Source(DecoderEngine.FFmpegSoftwareDecoder);
        using (new FfmpegVideoOutputGuard(software, () => failures++, softwareStage: true))
            Assert.NotNull(software.Source.Request(new VideoStreamDescriptor { EncodingProperties = MarkedSdr() }).Sample);
        Assert.Equal(0, failures);

        // A declined software stage hands Media Foundation the unmarked frames: rejected before presentation.
        var declined = Source(DecoderEngine.FFmpegSoftwareDecoder);
        var unmarked = MarkedSdr();
        unmarked.Properties.Remove(FfmpegVideoOutputGuard.ToneMapped);
        using (new FfmpegVideoOutputGuard(declined, () => failures++, softwareStage: true))
            Assert.Null(declined.Source.Request(new VideoStreamDescriptor { EncodingProperties = unmarked }).Sample);
        Assert.Equal(1, failures);

        // Another stream than the route's stage: rejected both ways.
        var hardware = Source(DecoderEngine.FFmpegD3D11HardwareDecoder);
        using (new FfmpegVideoOutputGuard(hardware, () => failures++, softwareStage: true))
            Assert.Null(hardware.Source.Request(new VideoStreamDescriptor { EncodingProperties = MarkedSdr() }).Sample);
        Assert.Equal(2, failures);
        var hardwareRoute = Source(DecoderEngine.FFmpegSoftwareDecoder);
        using (new FfmpegVideoOutputGuard(hardwareRoute, () => failures++))
            Assert.Null(hardwareRoute.Source.Request(new VideoStreamDescriptor { EncodingProperties = MarkedSdr() }).Sample);
        Assert.Equal(3, failures);
    }

    [Fact]
    public void Av1AndTheOtherSoftwareCodecsStayOffTheStage()
    {
        // Release AV1 (dav1d on D3D11) and the software-only codecs never take the downscale: the codec list.
        foreach (var (codec, profile, width, height) in new[] { ("av1", "main", 1920, 1080), ("mpeg4", "advanced simple profile", 1920, 1080) })
        {
            var file = SoftwareFallbackTests.File(codec, profile, 8, width, height, 23.976, "yuv420p");
            var route = SoftwareFallbackTests.Decide(file, display: Hd720);
            Assert.True(route.SoftwareVideo);
            Assert.False(route.ConsoleStage);
            Assert.False(route.SoftwareFallback);
            var config = FfmpegSource.Configuration(route);
            foreach (var key in new[] { "bale_tonemap", "bale_tonemap_output_width", "bale_tonemap_output_height" })
                Assert.DoesNotContain(key, config.FFmpegOptions.Keys);
        }
        // HDR AV1 on an SDR TV still wants the tone map only rav1g's own stage gives: refused as before (server tone map).
        var hdrAv1 = SoftwareFallbackTests.File("av1", "main", 10, 1920, 1080, 23.976, "yuv420p10le", range: "hdr10");
        var toneMap = SoftwareFallbackTests.Decide(hdrAv1, sdrOutput: true, display: Hd);
        Assert.True(toneMap.ConsoleToneMap && toneMap.SoftwareVideo);
        Assert.False(toneMap.SoftwareFallback);
        Assert.Equal("Console tone mapping is unavailable for this source.",
            Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(toneMap)).Message);
    }

    [Fact]
    public void TheDebugSwitchPastTheEnvelopeKeepsTheStage()
    {
        // 4K H.264 High 10 on a 1080p display: outside every envelope, so only swfallback=on decodes it, through the downscale.
        var file = SoftwareFallbackTests.File("h264", "high 10", 10, 3840, 2160, 23.976, "yuv420p10le");
        var route = SoftwareFallbackTests.Decide(file, display: Hd);
        Assert.True(route.SoftwareVideo && route.ConsoleDownscale);
        Assert.False(route.SoftwareFallback);
        Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(route));
        var fallback = new DebugFfmpegInputTuning(SoftwareFallback: true);
        Assert.True(fallback.SoftwareFallbackAdmits(route));
        var config = FfmpegSource.Configuration(route, fallback);
        Assert.Equal(VideoDecoderMode.ForceFFmpegSoftwareDecoder, config.Video.VideoDecoderMode);
        Assert.Equal("1", config.FFmpegOptions["bale_tonemap"]);
        // The guard holds the stage to its marked output there too (the debug guard follows the route's stage).
        Assert.True(fallback.RequiresToneMapGuard(route));
    }

    private static FFmpegMediaSource Stream(string codec, DecoderEngine engine)
    {
        var source = new FFmpegMediaSource();
        source.VideoStreams.Add(new() { CodecName = codec, DecoderEngine = engine });
        return source;
    }

    private static FFmpegMediaSource Source(DecoderEngine engine)
    {
        var source = new FFmpegMediaSource();
        source.VideoStreams.Add(new() { CodecName = "hevc", DecoderEngine = engine });
        return source;
    }

    // The stage's processed output: limited BT.709 P010 with the fork's tone-map mark.
    private static VideoEncodingProperties MarkedSdr() => new()
    {
        Subtype = "P010",
        Properties =
        {
            [FfmpegVideoOutputGuard.ToneMapped] = 1u,
            [FfmpegVideoOutputGuard.Primaries] = 2u,
            [FfmpegVideoOutputGuard.Transfer] = 5u,
            [FfmpegVideoOutputGuard.Matrix] = 1u,
            [FfmpegVideoOutputGuard.Range] = 2u,
        },
    };
}
