using Bale.Core.Playback;
using Bale.Xbox.Services;
using FFmpegInteropX;

namespace Bale.Core.Tests;

// The debug software fallback past the release envelope (ffmpeg-tune swfallback=on / decoder=software,
// docs/software-decode.md); SoftwareFallbackTests covers the release fallback itself. FfmpegSource logs through the
// shared stand-in, so this stays in the collection of the other log users.
[Collection("Player seek")]
public sealed class DebugSoftwareFallbackTests
{
    private static readonly DebugFfmpegInputTuning Fallback = new(SoftwareFallback: true);

    // The player's own decision for a file (versions and the attempt's claims), so the release fallback is real.
    private static PlaybackRouteDecision Route(string codec, string? profile, int depth, int width, int height, double fps = 30,
        string? pixelFormat = null, string range = "sdr", bool sdrOutput = false) =>
        SoftwareFallbackTests.Decide(SoftwareFallbackTests.File(codec, profile, depth, width, height, fps, pixelFormat, range), sdrOutput: sdrOutput);

    [Theory]
    // Outside the release envelope (1920x1080; 4:2:2 / 4:4:4 up to 30 fps): 4K H.264, 4:2:2 at 60 fps, 4K VP9 profile 1.
    [InlineData("h264", "high", 8, 3840, 2160, 30, "yuv420p")]
    [InlineData("h264", "high 10", 10, 3840, 2160, 24, "yuv420p10le")]
    [InlineData("h264", "high 4:2:2", 10, 1920, 1080, 60, "yuv422p10le")]
    [InlineData("hevc", "rext", 10, 1920, 1080, 60, "yuv422p10le")]
    [InlineData("vp9", "profile 1", 8, 3840, 2160, 30, "yuv444p")]
    public void OutsideTheReleaseEnvelopeOnlyTheSwitchDecodesInFfmpegSoftware(string codec, string profile, int depth, int width,
        int height, double fps, string pixelFormat)
    {
        var route = Route(codec, profile, depth, width, height, fps, pixelFormat);
        Assert.True(route.UseFfmpeg);
        Assert.True(route.SoftwareVideo);
        Assert.False(route.SoftwareFallback);
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
    public void InsideTheReleaseEnvelopeTheSwitchChangesNothing()
    {
        var route = Route("h264", "high 10", 10, 1920, 1080, 23.976, "yuv420p10le");
        Assert.True(route.SoftwareFallback);
        var release = FfmpegSource.Configuration(route, new DebugFfmpegInputTuning());
        var debug = FfmpegSource.Configuration(route, Fallback);
        Assert.Equal(VideoDecoderMode.ForceFFmpegSoftwareDecoder, release.Video.VideoDecoderMode);
        Assert.Equal(release.Video.VideoDecoderMode, debug.Video.VideoDecoderMode);
        Assert.Equal(release.FFmpegOptions.OrderBy(pair => pair.Key), debug.FFmpegOptions.OrderBy(pair => pair.Key));
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
    public void ThroughTheRoutesConsoleStageButDecoderSoftwareDropsIt()
    {
        // HDR HEVC RExt 4:2:2 at 60 fps on an SDR display: outside the envelope (4:2:2 up to 30 fps), and the route wants the
        // tone map, which takes software frames since wrapper bale.80 (phase B).
        var route = Route("hevc", "rext", 10, 1920, 1080, 60, "yuv422p10le", range: "hdr10", sdrOutput: true);
        Assert.True(route.SoftwareVideo);
        Assert.True(route.ConsoleToneMap);
        Assert.False(route.SoftwareFallback);
        Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(route, new DebugFfmpegInputTuning()));
        Assert.True(Fallback.SoftwareFallbackAdmits(route));
        var config = FfmpegSource.Configuration(route, Fallback);
        Assert.Equal(VideoDecoderMode.ForceFFmpegSoftwareDecoder, config.Video.VideoDecoderMode);
        Assert.Equal("1", config.FFmpegOptions["bale_tonemap"]);
        // decoder=software forces FFmpeg software and drops the stage, as on every route (a comparison mode).
        var forced = new DebugFfmpegInputTuning(Decoder: DebugFfmpegDecoder.Software);
        Assert.True(forced.SoftwareFallbackAdmits(route));
        Assert.DoesNotContain("bale_tonemap", FfmpegSource.Configuration(route, forced).FFmpegOptions.Keys);
    }

    [Theory]
    [InlineData("vc1", "advanced", 8)]
    [InlineData("mpeg2video", "main", 8)]
    public void OnlyH264HevcAndVp9TakeTheFallback(string codec, string profile, int depth)
    {
        var route = Route(codec, profile, depth, 2560, 1440, 30, "yuv420p");
        Assert.True(route.SoftwareVideo);
        Assert.False(route.SoftwareFallback);
        Assert.False(Fallback.SoftwareFallbackAdmits(route));
        Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(route, Fallback));
        Assert.Equal(new[] { "h264", "hevc", "vp9" }, SoftwareDecodePolicy.SoftwareFallbackVideoCodecs);
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
        var route = Route(codec, profile, depth, width, height, 24, "yuv420p");
        Assert.False(Fallback.SoftwareFallbackAdmits(route));
        Assert.False(route.SoftwareFallback);
        var release = FfmpegSource.Configuration(route, new DebugFfmpegInputTuning());
        var debug = FfmpegSource.Configuration(route, Fallback);
        Assert.Equal(release.Video.VideoDecoderMode, debug.Video.VideoDecoderMode);
        Assert.Equal(release.General.FastSeek, debug.General.FastSeek);
        Assert.Equal(release.FFmpegOptions.OrderBy(pair => pair.Key), debug.FFmpegOptions.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void TheSwitchNeverChangesClaims()
    {
        var device = SoftwareFallbackTests.Device with { OriginalDynamicRangeMappingSupported = true };
        Assert.Equal(device, Fallback.ToneMapCapabilities(device));
        // The release claims carry the bounded envelopes only (phases A and B); the switch is a play-url measurement aid and
        // never widens them, so the server never plans what only the switch would decode.
        Assert.All(device.VideoDecode.Where(decode => !decode.Hardware && SoftwareDecodePolicy.SoftwareFallbackVideoCodecs.Contains(decode.Codec)),
            decode => Assert.True(decode is { MaxWidth: 1920, MaxHeight: 1080, MaxBitrateKbps: null }
                or { Codec: "h264", MaxWidth: 3840, MaxHeight: 2160, MaxFrameRate: 30, MaxBitrateKbps: 50_000 }));
    }

    [Fact]
    public void ThreadsReachOnlySoftwareVideo()
    {
        var tuning = new DebugFfmpegInputTuning(DecoderThreads: 4, SoftwareFallback: true);
        Assert.Equal(4u, FfmpegSource.Configuration(Route("h264", "high", 8, 3840, 2160), tuning).Video.MaxDecoderThreads);
        Assert.Equal(4u, FfmpegSource.Configuration(Route("h264", "high 10", 10, 1920, 1080), tuning with { SoftwareFallback = false })
            .Video.MaxDecoderThreads);
        Assert.Equal(4u, FfmpegSource.Configuration(Route("av1", "main", 10, 1920, 1080), tuning).Video.MaxDecoderThreads);
        Assert.Equal(4u, FfmpegSource.Configuration(Route("hevc", "main 10", 10, 3840, 2160),
            tuning with { Decoder = DebugFfmpegDecoder.Software }).Video.MaxDecoderThreads);
        // d3d11va and the system decoder take no FFmpeg software threads.
        Assert.Equal(0u, FfmpegSource.Configuration(Route("hevc", "main 10", 10, 3840, 2160), tuning).Video.MaxDecoderThreads);
        Assert.Equal(0u, FfmpegSource.Configuration(Route("h264", "high", 8, 3840, 2160), new DebugFfmpegInputTuning(SoftwareFallback: true))
            .Video.MaxDecoderThreads);
    }

    [Fact]
    public void ReleaseTakesOnlyTheBoundedFallbackAndPlayUrlExplainsTheRest()
    {
        var production = DebugFfmpegDecoderTests.ProductionSource();
        // Release builds know no debug switch: only the route's bounded decision opens the gate.
        foreach (var debugOnly in new[] { "SoftwareFallbackAdmits", "swfallback", "MaxDecoderThreads", "DebugFfmpegInputTuning" })
            Assert.DoesNotContain(debugOnly, production);
        Assert.Contains("? !SoftwareDecodePolicy.VideoCodecs.Contains(route.VideoCodec!) && !route.SoftwareFallback", production);
        Assert.Contains("throw new InvalidDataException(\"The video codec cannot use the local fallback.\");", production);
        Assert.Contains("player: software fallback codec={route.VideoCodec}", production);
        var source = Read("FfmpegSource.cs.txt");
        var bypass = source.IndexOf("if (!route.SoftwareFallback && tuning.SoftwareFallbackAdmits(route))", StringComparison.Ordinal);
        Assert.True(bypass > 0 && bypass < source.IndexOf("throw new InvalidDataException(\"The video codec cannot use the local fallback.\");",
            StringComparison.Ordinal));
        Assert.Contains("(past the release bounds{stageWords})", source);
        var url = Read("PlayerPage.DebugUrl.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", url);
        Assert.Contains("&& !media.Route.SoftwareFallback && !attempt.FfmpegTuning.SoftwareFallbackAdmits(media.Route))", url);
        Assert.Contains("outside Bale's software envelope", url);
        Assert.Contains("(debug: ffmpeg-tune swfallback=on)", url);
        Assert.Contains("if (attempt.FfmpegTuning.SoftwareFallbackAdmits(media.Route))", url);
        // The player decides every mount with the attempt's claims as they stand.
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("softwareDecode: attempt.Start?.ClientCapabilities.VideoDecode,", media);
        // Phase B: software frames take the stage when the pinned wrapper and bale_gpu run it on them.
        Assert.Contains("softwareStage: ConsoleToneMapPolicy.SoftwareStageWrapperSupported(FfmpegWrapperVersion.Value)", media);
        Assert.Contains("softwareStage: media.Route.SoftwareVideo);", media);
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
