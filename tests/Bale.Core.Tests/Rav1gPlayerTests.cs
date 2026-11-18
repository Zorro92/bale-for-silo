using Bale.Core.Playback;
using Bale.Xbox.Services;
using FFmpegInteropX;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Bale.Core.Tests;

[Collection("Player seek")]
public sealed class Rav1gPlayerTests
{
    private static PlaybackRouteDecision Route(string codec = "av1", bool software = true) =>
        new(UseFfmpeg: true, SoftwareVideo: software, VideoCodec: codec, AudioCodec: "opus", AudioOrdinal: null,
            VideoWidth: 3840, VideoHeight: 2160);

    private static Rav1gRouteDecision Decision(int? width = 1920, int? height = 1080, bool toneMap = true) =>
        new(true, 200, width, height, toneMap, new Av1SourceFacts(10, 2, 5, 1, null));

    [Fact]
    public void Rav1gRouteSetsTheForkKeyAndKeepsZeroCopyForTheOpenFallback()
    {
        var config = FfmpegSource.Configuration(Route(), new DebugFfmpegInputTuning(), Decision());
        Assert.Equal("1", config.FFmpegOptions["bale_av1_decoder"]);
        // Honoured only if the fork falls back to dav1d at open; ignored for a rav1g stream.
        Assert.Equal("1", config.FFmpegOptions["bale_zero_copy"]);
        Assert.Equal("1", config.FFmpegOptions["bale_tonemap"]);
        Assert.Equal("1920", config.FFmpegOptions["bale_tonemap_output_width"]);
        Assert.Equal("1080", config.FFmpegOptions["bale_tonemap_output_height"]);
        Assert.True(config.General.FastSeek);
        Assert.Equal(VideoDecoderMode.ForceFFmpegSoftwareDecoder, config.Video.VideoDecoderMode);
        var fits = FfmpegSource.Configuration(Route(), new DebugFfmpegInputTuning(), Decision(null, null, false));
        Assert.False(fits.FFmpegOptions.ContainsKey("bale_tonemap"));
        Assert.False(fits.FFmpegOptions.ContainsKey("bale_tonemap_output_width"));
    }

    [Fact]
    public void Rav1gRouteDecodesAFewFramesAheadAndTheTuneOverrideStillWins()
    {
        var decision = Decision() with { DecodeAheadFrames = 3 };
        Assert.Equal("3", FfmpegSource.Configuration(Route(), new DebugFfmpegInputTuning(), decision)
            .FFmpegOptions["bale_decode_ahead_frames"]);
        Assert.Equal("12", FfmpegSource.Configuration(Route(), new DebugFfmpegInputTuning(DecodeAheadFrames: 12), decision)
            .FFmpegOptions["bale_decode_ahead_frames"]);
        // dav1d keeps the software default (8 above 1080p), also when the switch decided dav1d.
        Assert.Equal("8", FfmpegSource.Configuration(Route()).FFmpegOptions["bale_decode_ahead_frames"]);
        Assert.Equal("8", FfmpegSource.Configuration(Route(), new DebugFfmpegInputTuning(), decision with { Rav1g = false })
            .FFmpegOptions["bale_decode_ahead_frames"]);
    }

    [Fact]
    public void Dav1dAndNonAv1RoutesNeverSetTheForkKey()
    {
        Assert.False(FfmpegSource.Configuration(Route()).FFmpegOptions.ContainsKey("bale_av1_decoder"));
        Assert.False(FfmpegSource.Configuration(Route(), new DebugFfmpegInputTuning(), Decision() with { Rav1g = false })
            .FFmpegOptions.ContainsKey("bale_av1_decoder"));
        Assert.False(FfmpegSource.Configuration(Route("mpeg4"), new DebugFfmpegInputTuning(), Decision())
            .FFmpegOptions.ContainsKey("bale_av1_decoder"));
        // A debug system-decoder override is not a software route: rav1g would otherwise take AV1 before decoder choice.
        var system = new DebugFfmpegInputTuning(Decoder: DebugFfmpegDecoder.System);
        Assert.False(FfmpegSource.Configuration(Route(), system, Decision()).FFmpegOptions.ContainsKey("bale_av1_decoder"));
    }

    [Theory]
    [InlineData(2, "av1", true)] // FFmpegD3D11HardwareDecoder: rav1g's provider
    [InlineData(1, "av1", true)] // FFmpegSoftwareDecoder: the open's dav1d fallback
    [InlineData(0, "av1", false)] // SystemDecoder
    [InlineData(2, "hevc", false)]
    public void Rav1gRouteAcceptsRav1gOrItsDav1dFallback(int engine, string codec, bool allowed)
    {
        var source = new FFmpegMediaSource();
        source.VideoStreams.Add(new() { CodecName = codec, DecoderEngine = (DecoderEngine)engine });
        var error = Record.Exception(() => FfmpegSource.ValidateVideoDecoders(source, Route(), new DebugFfmpegInputTuning(), rav1g: true));
        if (allowed) Assert.Null(error);
        else Assert.IsType<InvalidDataException>(error);
        Assert.Throws<InvalidDataException>(() =>
            FfmpegSource.ValidateVideoDecoders(new FFmpegMediaSource(), Route(), new DebugFfmpegInputTuning(), rav1g: true));
    }

    private static VideoEncodingProperties Format(string subtype, bool rav1gMark = true, bool toneMapMark = false)
    {
        var format = new VideoEncodingProperties { Subtype = subtype };
        format.Properties[new Guid("dbfbe4d7-0740-4ee0-8192-850ab0e21935")] = 2u;
        format.Properties[new Guid("5fb0fce9-be5c-4935-a811-ec838f8eed93")] = 5u;
        format.Properties[new Guid("3e23d450-2c75-4d25-a00e-b91670d12327")] = 1u;
        format.Properties[new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11")] = 2u;
        if (rav1gMark) format.Properties[Rav1gPolicy.OutputMark] = 1u;
        if (toneMapMark) format.Properties[FfmpegVideoOutputGuard.ToneMapped] = 1u;
        return format;
    }

    [Fact]
    public void GuardAcceptsMarkedRav1gOutputAtTheSourceDepthAndRejectsNv12For10Bit()
    {
        var source = new FFmpegMediaSource();
        source.VideoStreams.Add(new() { CodecName = "av1", DecoderEngine = DecoderEngine.FFmpegD3D11HardwareDecoder });
        var failures = 0;
        var check = new Rav1gOutputCheck(new Av1SourceFacts(10, 2, 5, 1, null), sdrDisplay: true, allowNv12Stage: false);
        using var guard = new FfmpegVideoOutputGuard(source, () => failures++, check);
        Assert.NotNull(source.Source.Request(new VideoStreamDescriptor { EncodingProperties = Format("P010") }).Sample);
        Assert.Equal("direct", check.Verdict);
        Assert.True(check.GpuEngine);
        Assert.Null(source.Source.Request(new VideoStreamDescriptor { EncodingProperties = Format("NV12") }).Sample);
        Assert.Equal(1, failures);
        Assert.Equal("reject", check.Verdict);
    }

    [Fact]
    public void GuardAcceptsStageOutputAndTheUnmarkedDav1dFallback()
    {
        var source = new FFmpegMediaSource();
        source.VideoStreams.Add(new() { CodecName = "av1", DecoderEngine = DecoderEngine.FFmpegSoftwareDecoder });
        var failures = 0;
        var check = new Rav1gOutputCheck(new Av1SourceFacts(8, 2, 5, 1, null), true, false);
        using var guard = new FfmpegVideoOutputGuard(source, () => failures++, check);
        Assert.NotNull(source.Source.Request(new VideoStreamDescriptor { EncodingProperties = Format("NV12", rav1gMark: false) }).Sample);
        Assert.Equal("dav1d", check.Verdict);
        Assert.False(check.GpuEngine);
        Assert.NotNull(source.Source.Request(new VideoStreamDescriptor
            { EncodingProperties = Format("P010", rav1gMark: false, toneMapMark: true) }).Sample);
        Assert.Equal("stage", check.Verdict);
        Assert.Equal(0, failures);
    }

    [Fact]
    public void SwitchIsDav1dUnlessSetAndRejectsOtherNames()
    {
        var previous = DebugAv1DecoderSwitch.Mode;
        try
        {
            Assert.True(DebugAv1DecoderSwitch.TrySet("rav1g"));
            Assert.Equal("rav1g", DebugAv1DecoderSwitch.Mode);
            Assert.False(DebugAv1DecoderSwitch.TrySet("libaom"));
            Assert.False(DebugAv1DecoderSwitch.TrySet(null));
            Assert.Equal("rav1g", DebugAv1DecoderSwitch.Mode);
            Assert.True(DebugAv1DecoderSwitch.TrySet("dav1d"));
        }
        finally { DebugAv1DecoderSwitch.TrySet(previous); }
    }

    [Fact]
    public void LogObserverKeepsThisStreamsFirstFallbackAndFailureAndTheProcessLoadLine()
    {
        DebugRav1gLog.Observe("Bale rav1g: loaded rav1g 0.4.0-alpha.1 (interface 1)\n");
        DebugRav1gLog.Reset();
        DebugRav1gLog.Observe("Bale rav1g: active 1920x1080 bits=10 slots=3 parse=1 threads=2 device=120.0 MiB");
        DebugRav1gLog.Observe("Bale rav1g: output 1920x1080 P010 direct hdr=0");
        DebugRav1gLog.Observe("Bale rav1g: stream failed (decode, hr=0x80042B23)");
        DebugRav1gLog.Observe("Bale rav1g: stream failed (drain, hr=0x80042B24)");
        DebugRav1gLog.Observe("Bale tone map: request ignored");
        DebugRav1gLog.Observe(null);
        var log = DebugRav1gLog.Snapshot();
        Assert.Equal("loaded rav1g 0.4.0-alpha.1 (interface 1)", log.Loaded);
        Assert.Equal("output 1920x1080 P010 direct hdr=0", log.Output);
        Assert.Equal("stream failed (decode, hr=0x80042B23)", log.Failed);
        Assert.Null(log.Fallback);
        DebugRav1gLog.Reset();
        Assert.Null(DebugRav1gLog.Snapshot().Failed);
        Assert.NotNull(DebugRav1gLog.Snapshot().Loaded);
    }

    [Fact]
    public void FailedRav1gMountRemountsThroughTheSharedSamePlanPath()
    {
        var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PlayerPage.cs.txt"));
        Assert.Contains("Rav1gPolicy.RemountOnDav1d(failedMount.Rav1gRoute, failedMount.Rav1gActive,", page);
        Assert.Contains("attempt.Rav1gRemounted = true;\n            await RemountSamePlanAsync(attempt, position, \"player: rav1g failed; remounting the same plan on dav1d\");", page);
        Assert.Contains("attempt.SdrDownscaleDeclined = true;\n            await RemountSamePlanAsync(", page);
        // The remount withdraws rav1g for the rest of the attempt, so its mount is dav1d's and a failure replans.
        var media = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PlayerPage.Media.cs.txt"));
        Assert.Contains("DisplayHdrOutput.CurrentRawSize(), sdrDisplay, attempt.Rav1gRemounted);", media);
    }
}
