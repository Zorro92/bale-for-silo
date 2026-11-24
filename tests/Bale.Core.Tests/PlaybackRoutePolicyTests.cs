using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackRoutePolicyTests
{
    [Fact]
    public void EverySoftwareClaimIsAnFfmpegDecoderAndOnlyAv1IsBounded()
    {
        // Owner, 2026-11-17: declare everything the FFmpeg build decodes, as AetherEngine does.
        Assert.Contains("av1", SoftwareDecodePolicy.VideoCodecs);
        Assert.Contains("mpeg4", SoftwareDecodePolicy.VideoCodecs);
        Assert.All(ConsoleEvidence.Default.VideoDecode.Where(decode => !decode.Hardware),
            decode => Assert.Contains(decode.Codec, SoftwareDecodePolicy.FfmpegVideoCodecs));
        Assert.All(ConsoleEvidence.Default.VideoDecode.Where(decode => !decode.Hardware && decode.Codec != "av1"),
            decode => Assert.Null(decode.MaxHeight));
        var av1 = Assert.Single(ConsoleEvidence.Default.VideoDecode, decode => decode.Codec == "av1");
        Assert.Equal("dav1d", av1.DecoderName);
        Assert.Equal(new[] { 8, 10 }, av1.BitDepths);
        Assert.Equal(1920, av1.MaxWidth);
        Assert.Equal(1080, av1.MaxHeight);
        Assert.Equal(60, av1.MaxFrameRate);
        Assert.Null(av1.MaxBitrateKbps);
        Assert.Equal(8, ConsoleEvidence.Default.MaxChannels);
        Assert.Empty(ConsoleEvidence.Default.AudioPassthroughCodecs);
        Assert.Empty(ConsoleEvidence.Default.PassthroughEntries);
    }

    [Theory]
    [InlineData("rv40")]
    [InlineData("cinepak")]
    [InlineData("hap")]
    public void BuilderRejectsASoftwareClaimOutsideTheBundledDecoders(string codec)
    {
        var device = ConsoleEvidence.Default with { VideoDecode = [new(codec, "invalid", [], [], [8], 1920, 1080, 30, null, false)] };
        Assert.Throws<ArgumentException>(() => PlaybackStartBuilder.Build("file", "profile", "attempt", null,
            PlaybackSelection.Auto, device, PlaybackTestSupport.Identity));
    }

    [Theory]
    [InlineData("original_http", "av1", "eac3", true, true)]
    [InlineData("original_http", "mpeg4", "mp3", true, true)]
    // FFmpeg reads packages like originals (owner, 2026-11-16): MPEG-4 decodes in software there too.
    [InlineData("progressive", "mpeg4", "mp3", true, true)]
    [InlineData("server_remux_progressive", "mpeg4", "mp3", true, true)]
    [InlineData("hls", "mpeg4", "mp3", false, false)]
    [InlineData("progressive", "av1", "aac", true, true)]
    [InlineData("original_http", "hevc", "dts", true, false)]
    [InlineData("original_http", "", "flac", true, false)]
    [InlineData("original_http", "h264", "truehd", true, false)]
    [InlineData("original_http", "hevc", "eac3", true, false)]
    [InlineData("progressive", "h264", "aac", false, false)]
    // VP9 has no Windows container source that plays here: FFmpeg demuxes it for the system decoder.
    [InlineData("original_http", "vp9", "aac", true, false)]
    [InlineData("original_http", "vp09", "aac", true, false)]
    [InlineData("hls", "vp9", "aac", false, false)]
    [InlineData("original_http", "vc1", "aac", true, false)]
    [InlineData("original_http", "mpeg2video", "aac", true, false)]
    [InlineData("hls", "vc1", "aac", false, false)]
    [InlineData("hls", "mpeg2video", "aac", false, false)]
    [InlineData("original_http", "unknown", "unknown", false, false)]
    [InlineData("unknown", "av1", "dts", false, false)]
    [InlineData("hls", "av1", "dts", false, false)]
    [InlineData("server_transcode_hls", "av1", "truehd", false, false)]
    public void RoutesOnlyTheAllowedVideoOrFallbackAudio(string delivery, string video, string audio, bool ffmpeg, bool softwareVideo)
    {
        var route = PlaybackRoutePolicy.Decide(Plan(delivery, video, audio), false);
        Assert.Equal(ffmpeg, route.UseFfmpeg);
        Assert.Equal(softwareVideo, route.SoftwareVideo);
        Assert.Equal(delivery == "server_remux_progressive" ? 0 : 3, route.AudioOrdinal);
    }

    [Theory]
    [InlineData("dts")]
    [InlineData("dts_hd")]
    [InlineData("truehd")]
    [InlineData("flac")]
    [InlineData("opus")]
    [InlineData("vorbis")]
    [InlineData("alac")]
    public void EveryClaimedAudioFallbackUsesFfmpegButNeverSoftwareHevc(string audio)
    {
        Assert.Contains(audio, ConsoleEvidence.Default.AudioDecodeCodecs);
        var route = PlaybackRoutePolicy.Decide(Plan("original_http", "hevc", audio), false);
        Assert.True(route.UseFfmpeg);
        Assert.True(route.SoftwareAudio);
        Assert.False(route.SoftwareVideo);
    }

    [Theory]
    [InlineData("aac")]
    [InlineData("ac3")]
    [InlineData("eac3")]
    [InlineData("mp3")]
    [InlineData("EC-3")]
    public void SystemAudioKeepsOrdinaryHevcOnSystemDecoders(string audio)
    {
        // Every original reads through FFmpeg (2026-11-16); system audio still decodes in the system.
        var route = PlaybackRoutePolicy.Decide(Plan("original_http", "hevc", audio), false);
        Assert.True(route.UseFfmpeg);
        Assert.False(route.SoftwareAudio);
        Assert.False(route.SoftwareVideo);
    }

    // LPCM and MP2 were never verified in Windows' own MP4 source: FFmpeg decodes them.
    [Theory]
    [InlineData("pcm")]
    [InlineData("pcm_s24le")]
    [InlineData("pcm_bluray")]
    [InlineData("mp2")]
    public void PcmAndMp2DecodeInFfmpeg(string audio)
    {
        var route = PlaybackRoutePolicy.Decide(Plan("original_http", "hevc", audio), false);
        Assert.True(route.UseFfmpeg);
        Assert.True(route.SoftwareAudio);
        Assert.False(route.SoftwareVideo);
    }

    [Fact]
    public void HlsDetectionAndDebugOffAlwaysOverrideCodecPolicy()
    {
        var plan = Plan("original_http", "av1", "dts");
        Assert.False(PlaybackRoutePolicy.Decide(plan, true).UseFfmpeg);
        Assert.False(PlaybackRoutePolicy.Decide(plan, false, ffmpegEnabled: false).UseFfmpeg);
        plan.Stream = new StreamV3 { Protocol = "HLS" };
        Assert.False(PlaybackRoutePolicy.Decide(plan, false).UseFfmpeg);
    }

    [Theory]
    [InlineData("original_http", "h264", "ac3")]
    [InlineData("original_http", "hevc", "eac3")]
    [InlineData("progressive", "h264", "aac")]
    public void DebugForceMovesDirectSystemCodecsOntoFfmpegWithSystemVideo(string delivery, string video, string audio)
    {
        var plan = Plan(delivery, video, audio);
        // Originals already read through FFmpeg; the force only matters for the legacy progressive route.
        Assert.Equal(delivery == "original_http", PlaybackRoutePolicy.Decide(plan, false).UseFfmpeg);
        var route = PlaybackRoutePolicy.Decide(plan, false, forceFfmpeg: true);
        Assert.True(route.UseFfmpeg);
        Assert.False(route.SoftwareVideo);
        Assert.False(route.SoftwareAudio);
        // HLS and the debug off switch still win over the force.
        Assert.False(PlaybackRoutePolicy.Decide(plan, true, forceFfmpeg: true).UseFfmpeg);
        Assert.False(PlaybackRoutePolicy.Decide(plan, false, ffmpegEnabled: false, forceFfmpeg: true).UseFfmpeg);
        Assert.False(PlaybackRoutePolicy.Decide(Plan("server_transcode_hls", video, audio), false, forceFfmpeg: true).UseFfmpeg);
    }

    [Theory]
    [InlineData("original_http", "mkv", null, "h264", "ac3", true)]
    [InlineData("original_http", null, "mkv", "hevc", "eac3", true)]
    [InlineData("original_http", "webm", null, "hevc", "aac", true)]
    [InlineData("original_http", "matroska,webm", null, "hevc", "aac", true)]
    [InlineData("progressive", " MKV ", "mp4", "h264", "aac", true)]
    [InlineData("server_remux_progressive", "mkv", "mkv", "hevc", "eac3", true)]
    // The delivered stream's container wins over the source's.
    [InlineData("progressive", "mp4", "mkv", "hevc", "eac3", false)]
    // A forward-only remux has no length or ranges for Windows' own MP4 source.
    [InlineData("server_remux_progressive", "mp4", "mkv", "hevc", "eac3", true)]
    // Every original reads through FFmpeg (owner, 2026-11-16), MP4 included.
    [InlineData("original_http", "mp4", null, "h264", "aac", true)]
    [InlineData("original_http", null, "mp4", "hevc", "eac3", true)]
    [InlineData("original_http", null, null, "h264", "ac3", true)]
    [InlineData("server_transcode_hls", "mkv", "mkv", "hevc", "eac3", false)]
    public void MatroskaDirectPlayUsesFfmpegDemuxWithSystemDecoders(string delivery, string? streamContainer, string? sourceContainer,
        string video, string audio, bool ffmpeg)
    {
        var plan = Plan(delivery, video, audio);
        plan.Source!.Container = sourceContainer;
        if (streamContainer is not null) plan.Stream = new StreamV3 { Container = streamContainer };
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.Equal(ffmpeg, route.UseFfmpeg);
        Assert.False(route.SoftwareVideo);
        Assert.False(route.SoftwareAudio);
        // HLS and the debug off switch still win.
        Assert.False(PlaybackRoutePolicy.Decide(plan, true).UseFfmpeg);
        Assert.False(PlaybackRoutePolicy.Decide(plan, false, ffmpegEnabled: false).UseFfmpeg);
    }

    [Fact]
    public void OriginalUsesTheSelectedSourceCodecAndProgressiveUsesItsActualRecipe()
    {
        var plan = Plan("original_http", "av1", "dts");
        plan.EffectiveRecipe = new EffectiveRecipeV3 { VideoCodec = "hevc", AudioCodec = "aac" };
        Assert.True(PlaybackRoutePolicy.Decide(plan, false).SoftwareVideo);
        plan.Delivery = "progressive";
        Assert.False(PlaybackRoutePolicy.Decide(plan, false).UseFfmpeg);
        plan.EffectiveRecipe.AudioCodec = "flac";
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.True(route.UseFfmpeg);
        Assert.False(route.SoftwareVideo);
        plan.Delivery = "server_remux_progressive";
        Assert.Equal(0, PlaybackRoutePolicy.Decide(plan, false).AudioOrdinal);
    }

    [Theory]
    [InlineData("original_http", 3840L, 2160L)]
    [InlineData("progressive", 1280L, 720L)]
    [InlineData("server_remux_progressive", 1280L, 720L)]
    public void VideoDimensionsFollowTheSameSourceAsTheVideoCodec(string delivery, long width, long height)
    {
        var plan = Plan(delivery, "av1", "aac");
        plan.Source!.Width = 3840;
        plan.Source.Height = 2160;
        plan.EffectiveRecipe = new EffectiveRecipeV3 { VideoCodec = "av1", Width = 1280, Height = 720 };
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.Equal(width, route.VideoWidth);
        Assert.Equal(height, route.VideoHeight);
        Assert.Equal(delivery == "original_http" ? SoftwareDecodeTuning.Above1080pDav1dMaxFrameDelay : (int?)null,
            SoftwareDecodeTuning.ForVideo(route.SoftwareVideo, route.VideoWidth, route.VideoHeight).Dav1dMaxFrameDelay);
    }

    [Theory]
    [InlineData("progressive", null, null, 3840L, 2160L)]
    [InlineData("progressive", 1280L, null, 1280L, 2160L)]
    [InlineData("progressive", null, 720L, 3840L, 720L)]
    [InlineData("server_remux_progressive", null, null, 3840L, 2160L)]
    [InlineData("server_remux_progressive", 1280L, null, 1280L, 2160L)]
    [InlineData("server_remux_progressive", null, 720L, 3840L, 720L)]
    public void MissingRecipeDimensionsFallBackToTheSourceIndependently(string delivery, long? recipeWidth, long? recipeHeight,
        long width, long height)
    {
        var plan = Plan(delivery, "av1", "aac");
        plan.Source!.Width = 3840;
        plan.Source.Height = 2160;
        plan.EffectiveRecipe = new EffectiveRecipeV3 { Width = recipeWidth, Height = recipeHeight };
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.Equal(width, route.VideoWidth);
        Assert.Equal(height, route.VideoHeight);
        plan.EffectiveRecipe = null;
        route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.Equal(3840, route.VideoWidth);
        Assert.Equal(2160, route.VideoHeight);
    }

    [Fact]
    public void OriginalDoesNotUseRecipeDimensionsWhenSourceDimensionsAreUnknown()
    {
        var plan = Plan("original_http", "av1", "aac");
        plan.EffectiveRecipe = new EffectiveRecipeV3 { Width = 3840, Height = 2160 };
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.Null(route.VideoWidth);
        Assert.Null(route.VideoHeight);
    }

    [Fact]
    public void RecipeDimensionsDoNotRequireASource()
    {
        var plan = new PlaybackPlan
        {
            Delivery = "progressive",
            EffectiveRecipe = new EffectiveRecipeV3 { VideoCodec = "av1", Width = 3840, Height = 2160 },
        };
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.Equal(3840, route.VideoWidth);
        Assert.Equal(2160, route.VideoHeight);
    }

    [Theory]
    [InlineData("original_http")]
    [InlineData("progressive")]
    public void MissingSourceAndRecipeLeaveDimensionsUnknown(string delivery)
    {
        var route = PlaybackRoutePolicy.Decide(new PlaybackPlan { Delivery = delivery }, false);
        Assert.Null(route.VideoWidth);
        Assert.Null(route.VideoHeight);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    public void PlannedAudioOrdinalMapsDemuxOrderToBestStreamFirstOrder(long ordinal, int index) =>
        Assert.Equal(index, PlaybackRoutePolicy.AudioTrackIndex(ordinal, [7, 1, 4]));

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData(3L)]
    [InlineData(long.MaxValue)]
    public void MissingOrOutOfRangePlannedAudioFailsInsteadOfPlayingTheDefault(long? ordinal) =>
        Assert.Throws<InvalidDataException>(() => PlaybackRoutePolicy.AudioTrackIndex(ordinal, [7, 1, 4]));

    [Fact]
    public void InvalidStreamIdentitiesFailAndVideoOnlyNeedsNoAudioSelection()
    {
        Assert.Equal(-1, PlaybackRoutePolicy.AudioTrackIndex(null, []));
        Assert.Throws<InvalidDataException>(() => PlaybackRoutePolicy.AudioTrackIndex(0, []));
        Assert.Throws<InvalidDataException>(() => PlaybackRoutePolicy.AudioTrackIndex(0, [1, 1]));
        Assert.Throws<InvalidDataException>(() => PlaybackRoutePolicy.AudioTrackIndex(0, [-1]));
        Assert.Throws<InvalidDataException>(() => PlaybackRoutePolicy.AudioTrackIndex(1, [1, 7], expectedAudioCount: 3));
        Assert.Equal(1, PlaybackRoutePolicy.AudioTrackIndex(0, [7, 1, 4], expectedAudioCount: 3));
    }

    // What a Series console's decoder probe reports (HardwareDecodeClaimsTests).
    private static readonly IReadOnlyList<VideoDecodeCapability> SeriesHardware = ConsoleEvidence.BuildVideoDecode(
        new HardwareDecodeLimits(H264: new(1920, 1080), HevcMain: new(7680, 4320), HevcMain10: new(7680, 4320),
            Vp9Profile0: new(3840, 2160), Vp9Profile2: new(3840, 2160), Vc1: new(1920, 1080), Mpeg2: new(1920, 1080)))
        .Where(decode => decode.Hardware).ToArray();

    [Theory]
    // Owner, 2026-11-17: the console takes what its decoder can; FFmpeg decodes the rest in software.
    [InlineData("h264", "high", 8, 1920, 1080, false)]
    [InlineData("h264", "high 10", 10, 1920, 1080, true)]
    [InlineData("h264", "high", 8, 3840, 2160, true)]
    [InlineData("h264", "high 4:4:4 predictive", 8, 1920, 1080, true)]
    [InlineData("hevc", "main 10", 10, 3840, 2160, false)]
    [InlineData("hevc", "main", 8, 7680, 4320, false)]
    [InlineData("hevc", "rext", 10, 1920, 1080, true)]
    [InlineData("hevc", "main 4:2:2 10", 10, 1920, 1080, true)]
    [InlineData("hevc", "main 12", 12, 1920, 1080, true)]
    [InlineData("vp9", "profile 0", 8, 3840, 2160, false)]
    [InlineData("vp9", "profile 2", 10, 3840, 2160, false)]
    [InlineData("vp9", "profile 1", 8, 1920, 1080, true)]
    [InlineData("vc1", "advanced", 8, 1920, 1080, false)]
    [InlineData("mpeg2video", "main", 8, 1920, 1080, false)]
    [InlineData("wmv3", "main", 8, 1280, 720, true)]
    [InlineData("vp8", null, 8, 1920, 1080, true)]
    [InlineData("msmpeg4v3", null, 8, 640, 480, true)]
    [InlineData("hevc", null, null, null, null, false)]
    public void HardwareCodecsTheConsoleCannotTakeDecodeInFfmpeg(string codec, string? profile, int? depth, int? width, int? height,
        bool software)
    {
        var plan = Plan("original_http", codec, "aac");
        plan.Source!.VideoProfile = profile;
        plan.Source.BitDepth = depth;
        plan.Source.Width = width;
        plan.Source.Height = height;
        var route = PlaybackRoutePolicy.Decide(plan, false, hardwareDecode: SeriesHardware);
        Assert.True(route.UseFfmpeg);
        Assert.Equal(software, route.SoftwareVideo);
        // An FFmpeg software stream is what that route allows; a hardware route stays on the system decoder.
        Assert.Equal(software, SoftwareDecodePolicy.VideoDecodersAllowed(route.SoftwareVideo, [(codec, true, false)], route.VideoCodec));
        Assert.Equal(!software, SoftwareDecodePolicy.VideoDecodersAllowed(route.SoftwareVideo, [(codec, false, true)], route.VideoCodec));
    }

    [Fact]
    public void WithoutTheProbeEntriesEveryHardwareCodecStaysInHardwareAsBefore()
    {
        var plan = Plan("original_http", "h264", "aac");
        plan.Source!.BitDepth = 10;
        plan.Source.Width = 3840;
        Assert.False(PlaybackRoutePolicy.Decide(plan, false).SoftwareVideo);
        // A non-hardware profile still goes to FFmpeg: no entry list says the hardware takes it.
        plan.Source.VideoProfile = "high 10";
        Assert.True(PlaybackRoutePolicy.Decide(plan, false).SoftwareVideo);
    }

    [Fact]
    public void AFailedSoftwareRouteForAHardwareCodecWithdrawsItsSoftwareFallbackOnly()
    {
        var device = ConsoleEvidence.ForHardware(new HardwareDecodeLimits(H264: new(1920, 1080), HevcMain: new(3840, 2160)));
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null, PlaybackSelection.Auto, device, PlaybackTestSupport.Identity);
        var plan = Plan("original_http", "h264", "aac");
        plan.Source!.VideoProfile = "high 10";
        plan.Source.BitDepth = 10;
        var route = PlaybackRoutePolicy.Decide(plan, false, hardwareDecode: device.VideoDecode);
        Assert.True(route.SoftwareVideo);
        var failure = new PlaybackFailureV3("decoder_failure", "open failed", "MediaPlayer");
        var replan = PlaybackReplan.ForFailure(start, plan, "attempt", [], 1, 0, failure, route);
        // The hardware H.264 entry stays (8-bit 1080p), so the server transcodes the 10-bit file instead.
        Assert.Contains(replan.ClientCapabilities.VideoDecode, decode => decode.Codec == "h264" && decode.Hardware);
        Assert.DoesNotContain(replan.ClientCapabilities.VideoDecode, decode => !decode.Hardware);
        Assert.Contains("h264", replan.ClientCapabilities.CodecsVideo);
    }

    private static PlaybackPlan Uhd(string video, string? range = "sdr", int? dvProfile = null)
    {
        var plan = Plan("original_http", video, "aac");
        plan.Source!.Width = 3840;
        plan.Source.Height = 2160;
        plan.Source.DynamicRange = range;
        plan.Source.DolbyVisionProfile = dvProfile;
        return plan;
    }

    [Theory]
    [InlineData("hevc")]
    [InlineData("vp9")]
    [InlineData("h264")]
    public void SdrLargerThanTheDisplayTakesTheConsoleDownscale(string video)
    {
        var route = PlaybackRoutePolicy.Decide(Uhd(video), false, display: (1920, 1080), sdrDownscale: true);
        Assert.True(route.UseFfmpeg);
        Assert.True(route.ConsoleDownscale);
        Assert.True(route.RequiresD3D11 && route.ConsoleStage);
        Assert.False(route.ConsoleToneMap || route.SoftwareVideo);
    }

    [Fact]
    public void ConsoleDownscaleNeedsSdrALargerVideoAKnownDisplayAndTheCapability()
    {
        Assert.False(PlaybackRoutePolicy.Decide(Uhd("hevc"), false, display: (1920, 1080), sdrDownscale: false).ConsoleDownscale);
        Assert.False(PlaybackRoutePolicy.Decide(Uhd("hevc"), false, display: (3840, 2160), sdrDownscale: true).ConsoleDownscale);
        Assert.False(PlaybackRoutePolicy.Decide(Uhd("hevc"), false, display: null, sdrDownscale: true).ConsoleDownscale);
        Assert.False(PlaybackRoutePolicy.Decide(Uhd("hevc", "hdr10"), false, display: (1920, 1080), sdrDownscale: true).ConsoleDownscale);
        Assert.False(PlaybackRoutePolicy.Decide(Uhd("hevc", "sdr", dvProfile: 5), false, display: (1920, 1080), sdrDownscale: true).ConsoleDownscale);
        Assert.False(PlaybackRoutePolicy.Decide(Uhd("mpeg2video"), false, display: (1920, 1080), sdrDownscale: true).ConsoleDownscale);
        Assert.False(PlaybackRoutePolicy.Decide(Uhd("hevc"), true, display: (1920, 1080), sdrDownscale: true).ConsoleDownscale);
        var hd = Uhd("hevc");
        hd.Source!.Width = 1920;
        hd.Source.Height = 1080;
        Assert.False(PlaybackRoutePolicy.Decide(hd, false, display: (1920, 1080), sdrDownscale: true).ConsoleDownscale);
    }

    internal static PlaybackPlan Plan(string delivery, string video, string audio) => new()
    {
        PlanId = "plan", PlanAttemptKey = "attempt-key", Delivery = delivery,
        Source = new PlaybackSource { VideoCodec = video, AudioCodec = audio },
        SelectedTracks = new SelectedTracksV3 { Audio = new TrackIdentityV3 { Id = "audio-3", Index = 3 } },
    };
}
