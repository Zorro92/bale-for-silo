using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using FFmpegInteropX;

namespace Bale.Core.Tests;

// The debug software fallback for H.264/HEVC/VP9 past the console's hardware decoder (docs/software-decode.md).
// FfmpegSource logs through the shared stand-in, so this stays in the collection of the other log users.
[Collection("Player seek")]
public sealed class DebugSoftwareFallbackTests
{
    // What a Series console's decoder probe reports (HardwareDecodeClaimsTests): H.264 stops at 1080p.
    private static readonly IReadOnlyList<VideoDecodeCapability> SeriesHardware = ConsoleEvidence.BuildVideoDecode(
        new HardwareDecodeLimits(H264: new(1920, 1080), HevcMain: new(7680, 4320), HevcMain10: new(7680, 4320),
            Vp9Profile0: new(3840, 2160), Vp9Profile2: new(3840, 2160), Vc1: new(1920, 1080), Mpeg2: new(1920, 1080)))
        .Where(decode => decode.Hardware).ToArray();

    private static readonly DebugFfmpegInputTuning Fallback = new(SoftwareFallback: true);

    private static PlaybackRouteDecision Route(string codec, string? profile, int depth, int width, int height,
        string range = "sdr", bool sdrOutput = false)
    {
        var plan = PlaybackRoutePolicyTests.Plan("original_http", codec, "aac");
        plan.Source!.VideoProfile = profile;
        plan.Source.BitDepth = depth;
        plan.Source.Width = width;
        plan.Source.Height = height;
        plan.Source.DynamicRange = range;
        return PlaybackRoutePolicy.Decide(plan, false, sdrOutput: sdrOutput, hardwareDecode: SeriesHardware);
    }

    [Theory]
    // The cases the backlog names: 4K H.264 on a 1080p-only H.264 decoder, and profiles the console's decoders lack.
    [InlineData("h264", "high", 8, 3840, 2160)]
    [InlineData("h264", "high 10", 10, 1920, 1080)]
    [InlineData("h264", "high 4:2:2", 10, 1920, 1080)]
    [InlineData("hevc", "rext", 10, 1920, 1080)]
    [InlineData("hevc", "main 12", 12, 1920, 1080)]
    [InlineData("vp9", "profile 1", 8, 1920, 1080)]
    public void ReleaseRefusesTheRouteAndTheSwitchDecodesItInFfmpegSoftware(string codec, string profile, int depth, int width, int height)
    {
        var route = Route(codec, profile, depth, width, height);
        Assert.True(route.UseFfmpeg);
        Assert.True(route.SoftwareVideo);
        Assert.False(route.ConsoleStage);

        // Release behaviour, and the debug default: refused, so a Silo mount replans to the server.
        Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(route));
        Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(route, new DebugFfmpegInputTuning()));
        Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(route, new DebugFfmpegInputTuning(SoftwareFallback: false)));
        Assert.False(new DebugFfmpegInputTuning().SoftwareFallbackAdmits(route));

        Assert.True(Fallback.SoftwareFallbackAdmits(route));
        var config = FfmpegSource.Configuration(route, Fallback);
        Assert.Equal(VideoDecoderMode.ForceFFmpegSoftwareDecoder, config.Video.VideoDecoderMode);
        // The same software defaults as software AV1/MPEG-4: decode-ahead and the keyframe fast seek, no stage.
        var above1080p = width > 1920 || height > 1080;
        Assert.Equal(above1080p ? "8" : "16", config.FFmpegOptions["bale_decode_ahead_frames"]);
        Assert.Equal(above1080p ? 500 : 1000, SoftwareDecodeTuning.BufferTimeMs(Fallback.SoftwareVideo(route.SoftwareVideo),
            width: route.VideoWidth, height: route.VideoHeight));
        Assert.True(config.General.FastSeek);
        Assert.DoesNotContain("bale_tonemap", config.FFmpegOptions.Keys);
        // No AV1-only option reaches an ordinary software decoder.
        foreach (var key in new[] { "bale_film_grain_mode", "bale_av1_decoder", "bale_av1_fallback_max_height", "bale_zero_copy", "bale_d3d11_upload" })
            Assert.DoesNotContain(key, config.FFmpegOptions.Keys);
        Assert.Equal(0u, config.Video.MaxDecoderThreads);

        // decoder=software forces FFmpeg software anyway, so it takes these routes too.
        var forced = new DebugFfmpegInputTuning(Decoder: DebugFfmpegDecoder.Software);
        Assert.True(forced.SoftwareFallbackAdmits(route));
        Assert.Equal(VideoDecoderMode.ForceFFmpegSoftwareDecoder, FfmpegSource.Configuration(route, forced).Video.VideoDecoderMode);
        // auto and system ask for other decoders: still refused.
        foreach (var decoder in new[] { DebugFfmpegDecoder.Auto, DebugFfmpegDecoder.System })
        {
            Assert.False(new DebugFfmpegInputTuning(Decoder: decoder).SoftwareFallbackAdmits(route));
            Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(route, new DebugFfmpegInputTuning(Decoder: decoder)));
        }
    }

    [Fact]
    public void TheOpenedStreamMustBeTheFfmpegSoftwareDecoder()
    {
        var route = Route("h264", "high", 8, 3840, 2160);
        FfmpegSource.ValidateVideoDecoders(Source("h264", DecoderEngine.FFmpegSoftwareDecoder), route, Fallback);
        FfmpegSource.ValidateVideoDecoders(Source("h264", DecoderEngine.FFmpegSoftwareDecoder), route,
            new DebugFfmpegInputTuning(Decoder: DebugFfmpegDecoder.Software));
        foreach (var engine in new[] { DecoderEngine.SystemDecoder, DecoderEngine.FFmpegD3D11HardwareDecoder })
            Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(Source("h264", engine), route, Fallback));
        // Another codec than the route's, or no video at all, still fails the open.
        Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(Source("hevc", DecoderEngine.FFmpegSoftwareDecoder), route, Fallback));
        Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(new FFmpegMediaSource(), route, Fallback));
    }

    [Fact]
    public void NeverWithAConsoleStage()
    {
        // HDR HEVC RExt on an SDR display: the route wants the tone map, which reads d3d11va textures only.
        var route = Route("hevc", "rext", 10, 1920, 1080, range: "hdr10", sdrOutput: true);
        Assert.True(route.SoftwareVideo);
        Assert.True(route.ConsoleToneMap);
        Assert.False(Fallback.SoftwareFallbackAdmits(route));
        Assert.False(new DebugFfmpegInputTuning(Decoder: DebugFfmpegDecoder.Software).SoftwareFallbackAdmits(route));
        Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(route, Fallback));
    }

    [Theory]
    [InlineData("vc1", "advanced", 8)]
    [InlineData("mpeg2video", "main", 8)]
    public void OnlyH264HevcAndVp9TakeTheFallback(string codec, string profile, int depth)
    {
        var route = Route(codec, profile, depth, 2560, 1440);
        Assert.True(route.SoftwareVideo);
        Assert.False(Fallback.SoftwareFallbackAdmits(route));
        Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(route, Fallback));
        Assert.Equal(new[] { "h264", "hevc", "vp9" }, DebugFfmpegInputTuning.SoftwareFallbackCodecs.ToArray());
    }

    [Theory]
    // Hardware routes, and the software-only codecs, configure exactly as without the switch.
    [InlineData("hevc", "main 10", 10, 3840, 2160)]
    [InlineData("h264", "high", 8, 1920, 1080)]
    [InlineData("vp9", "profile 2", 10, 3840, 2160)]
    [InlineData("av1", "main", 10, 1920, 1080)]
    [InlineData("mpeg4", "advanced simple profile", 8, 720, 480)]
    public void OtherRoutesAreUnchanged(string codec, string profile, int depth, int width, int height)
    {
        var route = Route(codec, profile, depth, width, height);
        Assert.False(Fallback.SoftwareFallbackAdmits(route));
        var release = FfmpegSource.Configuration(route, new DebugFfmpegInputTuning());
        var debug = FfmpegSource.Configuration(route, Fallback);
        Assert.Equal(release.Video.VideoDecoderMode, debug.Video.VideoDecoderMode);
        Assert.Equal(release.General.FastSeek, debug.General.FastSeek);
        Assert.Equal(release.FFmpegOptions.OrderBy(pair => pair.Key), debug.FFmpegOptions.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void TheSwitchNeverChangesClaims()
    {
        var device = ConsoleEvidence.ForHardware(new HardwareDecodeLimits(H264: new(1920, 1080), HevcMain: new(7680, 4320),
            HevcMain10: new(7680, 4320), Vp9Profile0: new(3840, 2160), Vp9Profile2: new(3840, 2160)));
        device = device with { OriginalDynamicRangeMappingSupported = true };
        Assert.Equal(device, Fallback.ToneMapCapabilities(device));
        // Release claims carry no software entry for the hardware codecs (2026-11-24, docs/software-decode.md fix 1);
        // the switch is a play-url measurement aid and never adds one.
        Assert.All(new[] { "h264", "hevc", "vp9" }, codec => Assert.DoesNotContain(device.VideoDecode,
            decode => decode.Codec == codec && !decode.Hardware));
    }

    [Fact]
    public void ThreadsReachOnlySoftwareVideo()
    {
        var tuning = new DebugFfmpegInputTuning(DecoderThreads: 4, SoftwareFallback: true);
        Assert.Equal(4u, FfmpegSource.Configuration(Route("h264", "high", 8, 3840, 2160), tuning).Video.MaxDecoderThreads);
        Assert.Equal(4u, FfmpegSource.Configuration(Route("av1", "main", 10, 1920, 1080), tuning).Video.MaxDecoderThreads);
        Assert.Equal(4u, FfmpegSource.Configuration(Route("hevc", "main 10", 10, 3840, 2160),
            tuning with { Decoder = DebugFfmpegDecoder.Software }).Video.MaxDecoderThreads);
        // d3d11va and the system decoder take no FFmpeg software threads.
        Assert.Equal(0u, FfmpegSource.Configuration(Route("hevc", "main 10", 10, 3840, 2160), tuning).Video.MaxDecoderThreads);
        Assert.Equal(0u, FfmpegSource.Configuration(Route("h264", "high", 8, 3840, 2160), new DebugFfmpegInputTuning(SoftwareFallback: true))
            .Video.MaxDecoderThreads);
    }

    [Fact]
    public void ReleaseBuildsKeepTheRefusalAndPlayUrlExplainsIt()
    {
        var production = DebugFfmpegDecoderTests.ProductionSource();
        Assert.DoesNotContain("SoftwareFallback", production);
        Assert.DoesNotContain("MaxDecoderThreads", production);
        Assert.Contains("? !SoftwareDecodePolicy.VideoCodecs.Contains(route.VideoCodec!)", production);
        Assert.Contains("throw new InvalidDataException(\"The video codec cannot use the local fallback.\");", production);
        var source = Read("FfmpegSource.cs.txt");
        var bypass = source.IndexOf("if (tuning.SoftwareFallbackAdmits(route))", StringComparison.Ordinal);
        Assert.True(bypass > 0 && bypass < source.IndexOf("throw new InvalidDataException(\"The video codec cannot use the local fallback.\");",
            StringComparison.Ordinal));
        var url = Read("PlayerPage.DebugUrl.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", url);
        Assert.Contains("&& !attempt.FfmpegTuning.SoftwareFallbackAdmits(media.Route))", url);
        Assert.Contains("(debug: ffmpeg-tune swfallback=on)", url);
        Assert.Contains("if (attempt.FfmpegTuning.SoftwareFallbackAdmits(media.Route))", url);
        // The switch is the captured per-attempt tuning, not a setting.
        Assert.Contains("public DebugFfmpegInputTuning FfmpegTuning { get; } = DebugFfmpegInputTuning.Current;", Read("PlayerPage.cs.txt"));
        Assert.Contains("swfallback=on|off|default", Read("DebugServer.cs.txt"));
        Assert.Contains("[swfallback=on|off|default]", Read("bale-debug.sh"));
        Assert.Contains("[threads=0..16|default]", Read("bale-debug.sh"));
    }

    private static FFmpegMediaSource Source(string codec, DecoderEngine engine)
    {
        var source = new FFmpegMediaSource();
        source.VideoStreams.Add(new() { CodecName = codec, DecoderEngine = engine });
        return source;
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
