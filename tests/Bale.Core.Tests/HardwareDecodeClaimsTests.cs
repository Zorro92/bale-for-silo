using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class HardwareDecodeClaimsTests
{
    // What a Series console's D3D11 video device reported (docs/player-plan.md, caps gpu).
    private static readonly HardwareDecodeLimits Series = new(
        H264: new(1920, 1080),
        HevcMain: new(7680, 4320), HevcMain10: new(7680, 4320),
        Vp9Profile0: new(3840, 2160), Vp9Profile2: new(3840, 2160));

    [Fact]
    public void ASeriesProbeClaimsEachHardwareCodecAtItsLargestReportedSize()
    {
        var claims = ConsoleEvidence.ForHardware(Series);
        Assert.Equal(new[] { "h264", "hevc", "vp9", "av1" }, claims.VideoCodecs);
        Assert.Equal(new[] { "h264", "hevc", "vp9", "av1" }, claims.VideoDecode.Select(decode => decode.Codec));
        Assert.Equal(new[] { "mp4", "mkv" }, claims.Containers);

        var hardware = claims.VideoDecode.Where(decode => decode.Hardware).ToArray();
        Assert.Equal(new[] { ("h264", 1920, 1080), ("hevc", 7680, 4320), ("vp9", 3840, 2160) },
            hardware.Select(decode => (decode.Codec, decode.MaxWidth ?? 0, decode.MaxHeight ?? 0)));
        Assert.Equal(new[] { 8 }, hardware[0].BitDepths);
        Assert.Equal(new[] { 8, 10 }, hardware[1].BitDepths);
        Assert.Equal(new[] { 8, 10 }, hardware[2].BitDepths);
        Assert.All(hardware, decode =>
        {
            Assert.Equal("Media Foundation", decode.DecoderName);
            Assert.Equal(60, decode.MaxFrameRate);
            // silo-server's videoDecodeTakesSourceV3 reads a missing bitrate as no limit.
            Assert.Null(decode.MaxBitrateKbps);
            // Platform-attested hardware entries are matched without profile or level lists.
            Assert.Empty(decode.Profiles);
            Assert.Empty(decode.Levels);
        });

        // The measured software AV1 claim does not follow the hardware probe.
        var av1 = Assert.Single(claims.VideoDecode, decode => !decode.Hardware);
        Assert.Equal(("av1", 1920, 1080, 60.0), (av1.Codec, av1.MaxWidth ?? 0, av1.MaxHeight ?? 0, av1.MaxFrameRate ?? 0));
        Assert.Equal(ConsoleEvidence.Default.AudioDecodeCodecs, claims.AudioDecodeCodecs);
        Assert.Equal("platform_attested", claims.VideoEvidence);
    }

    [Fact]
    public void TheDebugAv1BoundKeepsTheProbedHardwareClaims()
    {
        var claims = ConsoleEvidence.BuildVideoDecode(Series, 3840, 2160);
        Assert.Equal(new[] { "h264", "hevc", "vp9", "av1", "av1" }, claims.Select(decode => decode.Codec));
        Assert.Equal(ConsoleEvidence.BuildVideoDecode(Series).Where(decode => decode.Hardware).Select(Shape),
            claims.Where(decode => decode.Hardware).Select(Shape));
        Assert.Equal(new[] { (1920, 1080, 60.0), (3840, 2160, 30.0) }, claims.Where(decode => !decode.Hardware)
            .Select(decode => (decode.MaxWidth ?? 0, decode.MaxHeight ?? 0, decode.MaxFrameRate ?? 0)));
    }

    [Fact]
    public void AProbeWithoutVp9NeverNamesVp9()
    {
        var claims = ConsoleEvidence.ForHardware(Series with { Vp9Profile0 = null, Vp9Profile2 = null });
        Assert.Equal(new[] { "h264", "hevc", "av1" }, claims.VideoCodecs);
        Assert.DoesNotContain(claims.VideoDecode, decode => decode.Codec == "vp9");
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null, PlaybackSelection.Auto, claims, PlaybackTestSupport.Identity);
        Assert.DoesNotContain("vp9", start.ClientCapabilities.CodecsVideo);
        Assert.All(start.ClientPlaybackContext.Deliveries.Values, delivery => Assert.DoesNotContain("vp9", delivery.VideoCodecs));
    }

    [Fact]
    public void HevcMainAloneClaimsOnly8Bit()
    {
        var claims = ConsoleEvidence.ForHardware(new HardwareDecodeLimits(H264: new(1920, 1080), HevcMain: new(3840, 2160)));
        var hevc = Assert.Single(claims.VideoDecode, decode => decode.Codec == "hevc");
        Assert.Equal(new[] { 8 }, hevc.BitDepths);
        Assert.Equal((3840, 2160), (hevc.MaxWidth, hevc.MaxHeight));
        Assert.Equal(new[] { "h264", "hevc", "av1" }, claims.VideoCodecs);

        // Main10 alone is the mirror case, and a codec the probe did not report is not claimed.
        var ten = ConsoleEvidence.ForHardware(new HardwareDecodeLimits(HevcMain10: new(3840, 2160)));
        Assert.Equal(new[] { 10 }, Assert.Single(ten.VideoDecode, decode => decode.Codec == "hevc").BitDepths);
        Assert.Equal(new[] { "hevc", "av1" }, ten.VideoCodecs);
    }

    [Fact]
    public void DifferentSizesPerBitDepthBecomeOneEntryEachSoNeitherIsOverclaimed()
    {
        var claims = ConsoleEvidence.BuildVideoDecode(new HardwareDecodeLimits(
            HevcMain: new(3840, 2160), HevcMain10: new(7680, 4320), Vp9Profile0: new(3840, 2160), Vp9Profile2: new(1920, 1080)));
        Assert.Equal(new[]
        {
            ("hevc", "8", 3840, 2160), ("hevc", "10", 7680, 4320), ("vp9", "8", 3840, 2160), ("vp9", "10", 1920, 1080),
        }, claims.Where(decode => decode.Hardware).Select(Shape));
    }

    [Fact]
    public void AFailedOrEmptyProbeFallsBackToTheFixed1080pBaseline()
    {
        Assert.Same(ConsoleEvidence.Default, ConsoleEvidence.ForHardware(null));
        Assert.Same(ConsoleEvidence.Default, ConsoleEvidence.ForHardware(new HardwareDecodeLimits()));
        foreach (var claims in new[] { ConsoleEvidence.BuildVideoDecode(null), ConsoleEvidence.BuildVideoDecode(new HardwareDecodeLimits()) })
        {
            Assert.Equal(new[] { ("h264", "8", 1920, 1080), ("hevc", "8,10", 1920, 1080) }, claims.Where(decode => decode.Hardware).Select(Shape));
            Assert.All(claims.Where(decode => decode.Hardware), decode => Assert.Equal(25000, decode.MaxBitrateKbps));
        }
        Assert.Equal(new[] { "h264", "hevc", "av1" }, ConsoleEvidence.Default.VideoCodecs);
        Assert.DoesNotContain(ConsoleEvidence.Default.VideoDecode, decode => decode.Codec == "vp9");
    }

    [Fact]
    public void TheProbeAnswersFoldIntoTheLargestSizePerProfileAndItsOwnOutputFormat()
    {
        const int nv12 = HardwareDecodeLimits.Nv12, p010 = HardwareDecodeLimits.P010;
        var limits = HardwareDecodeLimits.FromAcceptedSizes(
        [
            (HardwareDecodeLimits.H264Profile, nv12, 1920, 1080),
            (HardwareDecodeLimits.HevcMainProfile, nv12, 7680, 4320),
            (HardwareDecodeLimits.HevcMainProfile, nv12, 1920, 1080),
            (HardwareDecodeLimits.HevcMainProfile, nv12, 3840, 2160),
            (HardwareDecodeLimits.HevcMain10Profile, p010, 1920, 1080),
            (HardwareDecodeLimits.HevcMain10Profile, p010, 3840, 2160),
            // A 10-bit profile's NV12 answer and an 8-bit profile's P010 answer are not playback paths.
            (HardwareDecodeLimits.HevcMain10Profile, nv12, 7680, 4320),
            (HardwareDecodeLimits.Vp9Profile0Profile, p010, 3840, 2160),
            (HardwareDecodeLimits.Vp9Profile2Profile, p010, 3840, 2160),
            // H264 multiview (d3d11.h D3D11_DECODER_PROFILE_H264_VLD_MULTIVIEW_NOFGT) is not a claim.
            (new Guid("705b9d82-76cf-49d6-b7e6-ac8872db013c"), nv12, 7680, 4320),
            (HardwareDecodeLimits.H264Profile, nv12, 0, 0),
        ]);
        Assert.Equal(new HardwareDecodeLimits(new(1920, 1080), new(7680, 4320), new(3840, 2160), null, new(3840, 2160)), limits);
        Assert.False(limits.IsEmpty);
        Assert.True(HardwareDecodeLimits.FromAcceptedSizes([]).IsEmpty);
        Assert.True(HardwareDecodeLimits.FromAcceptedSizes([(Guid.NewGuid(), nv12, 1920, 1080)]).IsEmpty);
    }

    [Fact]
    public void ASeriesConsolePicksThe4KHevcVersionOnA4KDisplayAndThe1080pOneOnA1080pDisplay()
    {
        var claims = ConsoleEvidence.ForHardware(Series).VideoDecode;
        var hevc = new[] { Version("4k", "hevc", 3840, 2160, 10), Version("1080", "hevc", 1920, 1080, 10) };
        Assert.Same(hevc[0], PlaybackFormatting.SelectVersion(hevc, new DisplayVersionMatch(2160, claims)));
        Assert.Same(hevc[1], PlaybackFormatting.SelectVersion(hevc, new DisplayVersionMatch(1080, claims)));
        // The fixed baseline stops at 1080p, so a failed probe keeps the 1080p file on a 4K display.
        Assert.Same(hevc[1], PlaybackFormatting.SelectVersion(hevc, new DisplayVersionMatch(2160, ConsoleEvidence.Default.VideoDecode)));

        // H.264 hardware decode reports 1080p only, so its 4K file is still not the direct pick.
        var h264 = new[] { Version("4k", "h264", 3840, 2160, 8), Version("1080", "h264", 1920, 1080, 8) };
        Assert.Same(h264[1], PlaybackFormatting.SelectVersion(h264, new DisplayVersionMatch(2160, claims)));
        var vp9 = new[] { Version("4k", "vp9", 3840, 2160, 10), Version("1080", "vp9", 1920, 1080, 8) };
        Assert.Same(vp9[0], PlaybackFormatting.SelectVersion(vp9, new DisplayVersionMatch(2160, claims)));
        Assert.Same(vp9[1], PlaybackFormatting.SelectVersion(vp9, new DisplayVersionMatch(1080, claims)));
    }

    [Fact]
    public void Vp9IsOfferedForDirectPlayOnlyAndEveryFfmpegLessPathWithdrawsIt()
    {
        var claims = ConsoleEvidence.ForHardware(Series);
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null, PlaybackSelection.Auto, claims, PlaybackTestSupport.Identity);
        Assert.Contains("vp9", start.ClientCapabilities.CodecsVideo);
        Assert.Contains("vp9", start.ClientPlaybackContext.Deliveries["original_http"].VideoCodecs);
        // AdaptiveMediaSource has no FFmpeg demuxer, so HLS never carries VP9.
        Assert.All(start.ClientPlaybackContext.Deliveries.Where(pair => pair.Value.Containers.Contains("m3u8")),
            pair => Assert.Equal(new[] { "h264", "hevc" }, pair.Value.VideoCodecs));

        // Debug "ffmpeg off" has no demuxer either.
        var off = SoftwareDecodePolicy.WithoutFallbacks(claims);
        Assert.Equal(new[] { "h264", "hevc" }, off.VideoCodecs);
        Assert.Equal(new[] { "h264", "hevc" }, off.VideoDecode.Select(decode => decode.Codec));
        // Xbox One keeps hardware VP9 when its probe reports it; only software AV1 goes.
        Assert.Equal(new[] { "h264", "hevc", "vp9" }, SoftwareDecodePolicy.WithoutSoftwareVideo(claims).VideoCodecs);
    }

    [Theory]
    [InlineData("mkv")]
    [InlineData("webm")]
    [InlineData("mp4")]
    public void DirectVp9AlwaysUsesTheFfmpegDemuxerWithTheSystemDecoder(string container)
    {
        var plan = PlaybackRoutePolicyTests.Plan("original_http", "vp9", "aac");
        plan.Source!.Container = container;
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.True(route.UseFfmpeg);
        Assert.False(route.SoftwareVideo);
        Assert.Equal("vp9", route.VideoCodec);
        // The opened stream must be on the system decoder; an FFmpeg software VP9 decoder is refused.
        Assert.True(SoftwareDecodePolicy.VideoDecodersAllowed(route.SoftwareVideo, [("vp9", false, true)]));
        Assert.False(SoftwareDecodePolicy.VideoDecodersAllowed(route.SoftwareVideo, [("vp9", true, false)]));
        Assert.DoesNotContain("vp9", SoftwareDecodePolicy.VideoCodecs);
        // With FFmpeg off the route is Windows' own source, which is why "off" withdraws the claim.
        Assert.False(PlaybackRoutePolicy.Decide(plan, false, ffmpegEnabled: false).UseFfmpeg);
    }

    [Fact]
    public void AFailedVp9RouteWithdrawsVp9OnTheSingleReplanButAFailedHevcRouteKeepsHevc()
    {
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null, PlaybackSelection.Auto,
            ConsoleEvidence.ForHardware(Series), PlaybackTestSupport.Identity);
        var failure = new PlaybackFailureV3("decoder_failure", "open failed", "MediaPlayer");

        var vp9 = PlaybackRoutePolicyTests.Plan("original_http", "vp9", "opus");
        var request = PlaybackReplan.ForFailure(start, vp9, "attempt", [], 1, 0, failure, PlaybackRoutePolicy.Decide(vp9, false));
        Assert.Equal(new[] { "h264", "hevc", "av1" }, request.ClientCapabilities.CodecsVideo);
        Assert.Equal(new[] { "h264", "hevc", "av1" }, request.ClientCapabilities.VideoDecode.Select(decode => decode.Codec));
        Assert.All(request.ClientPlaybackContext.Deliveries.Values, delivery => Assert.DoesNotContain("vp9", delivery.VideoCodecs));
        Assert.DoesNotContain("opus", request.ClientCapabilities.CodecsAudio);
        Assert.Contains(PlaybackProtocol.SoftwareVideoDecodeFeature, request.ClientFeatures);

        // HEVC also plays through Windows' own sources, so an FFmpeg audio-fallback failure keeps it.
        var hevc = PlaybackRoutePolicyTests.Plan("original_http", "hevc", "dts");
        request = PlaybackReplan.ForFailure(start, hevc, "attempt", [], 1, 0, failure, PlaybackRoutePolicy.Decide(hevc, false));
        Assert.Equal(new[] { "h264", "hevc", "vp9", "av1" }, request.ClientCapabilities.CodecsVideo);
        Assert.DoesNotContain("dts", request.ClientCapabilities.CodecsAudio);
        // The admitted request stays as it was for Retry.
        Assert.Contains("vp9", start.ClientCapabilities.CodecsVideo);
    }

    [Fact]
    public void TheSystemDecoderListNamesVp9ButTheSoftwareAllowlistStaysAv1Only()
    {
        Assert.Equal(new[] { "h264", "hevc", "vp9" }, SoftwareDecodePolicy.SystemVideoCodecs);
        Assert.Equal(new[] { "vp9" }, SoftwareDecodePolicy.DemuxedVideoCodecs);
        Assert.Equal(new[] { "av1" }, SoftwareDecodePolicy.VideoCodecs);
        Assert.Equal("vp9", SoftwareDecodePolicy.NormalizeCodec("VP09"));
        Assert.Equal("vp9", SoftwareDecodePolicy.NormalizeCodec(" vp9 "));
    }

    [Fact]
    public void TheProductionProbeIsSharedGuardedAndLogsNothingSensitive()
    {
        var probe = Read("HardwareDecodeProbe.cs.txt");
        // Production code: outside the debug guard, D3D11 only, one cached run on a pool thread.
        Assert.DoesNotContain("#if", probe);
        Assert.DoesNotContain("D3D12", probe.Replace("the D3D12 video query removed the", "", StringComparison.Ordinal));
        Assert.DoesNotContain("d3d12.dll", probe);
        Assert.Contains("[DllImport(\"d3d11.dll\", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]", probe);
        Assert.Contains("IntPtr* device, int* featureLevel, IntPtr* immediateContext", probe);
        Assert.Contains("private static readonly Lazy<Task<HardwareDecodeLimits?>> Probe = new(() => Task.Run(Detect));", probe);
        Assert.Contains("HardwareDecodeLimits.FromAcceptedSizes(", probe);
        // A failure logs one fixed line or the HRESULT, never exception text, and gives the baseline.
        Assert.DoesNotContain(".Message", probe);
        Assert.DoesNotContain("ToString()", probe);
        Assert.Contains("error = ex.HResult;", probe);
        Assert.Equal(3, probe.Split("using the 1080p baseline claims").Length - 1);
        Assert.Equal(3, probe.Split("return null;").Length - 1);
        // The device and its interfaces are released on every path.
        var cleanup = probe[probe.IndexOf("finally", StringComparison.Ordinal)..];
        Assert.Contains("Release(videoDevice, trace);", cleanup);
        Assert.Contains("Release(context, trace);", cleanup);
        Assert.Contains("Release(device, trace);", cleanup);

        var claims = Read("PlaybackClaims.cs.txt");
        Assert.Contains("var hardware = await HardwareDecodeProbe.Limits;", claims);
        Assert.Contains("DisplayHdrOutput.DetectCapabilities(ConsoleEvidence.ForHardware(hardware))", claims);
        Assert.Contains("var claims = ConsoleEvidence.ForHardware(hardware);", claims);
        Assert.DoesNotContain("ConsoleEvidence.Default", claims);
        // The debug command prints the same walk instead of its own copy.
        Assert.Contains("var report = HardwareDecodeProbe.Enumerate(", Read("DebugServer.Capabilities.cs.txt"));
        // FFmpeg hands every system codec, VP9 included, to the system decoder.
        var ffmpeg = Read("FfmpegSource.cs.txt");
        Assert.Contains("!SoftwareDecodePolicy.SystemVideoCodecs.Contains(route.VideoCodec)", ffmpeg);
        Assert.DoesNotContain("ConsoleEvidence.Default.VideoDecode", ffmpeg);
    }

    private static (string Codec, string Depths, int Width, int Height) Shape(VideoDecodeCapability decode) =>
        (decode.Codec, string.Join(',', decode.BitDepths), decode.MaxWidth ?? 0, decode.MaxHeight ?? 0);

    private static FileVersion Version(string id, string codec, int width, int height, long bitDepth) => new()
    {
        FileId = id,
        Resolution = width > 1920 ? "2160p" : "1080p",
        CodecVideo = codec,
        VideoTracks = [new VideoTrack { Codec = codec, Width = width, Height = height, BitDepth = bitDepth, FrameRate = "24000/1001" }],
    };

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
