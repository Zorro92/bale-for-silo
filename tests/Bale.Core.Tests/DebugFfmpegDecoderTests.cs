using Bale.Core.Playback;
using Bale.Xbox.Services;
using FFmpegInteropX;
using Windows.Media.Core;

namespace Bale.Core.Tests;

// Shares the existing log stand-in with HDMI tests; do not interleave exact log assertions.
[Collection("Player seek")]
public sealed class DebugFfmpegDecoderTests
{
    private static PlaybackRouteDecision Route(string codec = "hevc") =>
        new(true, codec == "av1", codec, "aac", 0, 3840, 2160);

    [Theory]
    [InlineData("Auto", "Automatic")]
    [InlineData("System", "ForceSystemDecoder")]
    [InlineData("Software", "ForceFFmpegSoftwareDecoder")]
    [InlineData(null, "ForceSystemDecoder")]
    public void OverrideMapsAfterTheProductionChoice(string? decoder, string expected)
    {
        var config = FfmpegSource.Configuration(Route(), new(Decoder: ParseDecoder(decoder)));
        Assert.Equal(expected, config.Video.VideoDecoderMode.ToString());
        Assert.True(config.Audio.SystemDecoderAAC);
        Assert.True(config.Audio.SystemDecoderMP3);
        Assert.Equal("1", config.FFmpegOptions["bale_system_decoder_dolby"]);
    }

    [Theory]
    [InlineData("hevc", "ForceSystemDecoder")]
    [InlineData("h264", "ForceSystemDecoder")]
    [InlineData("av1", "ForceFFmpegSoftwareDecoder")]
    public void DefaultKeepsTheProductionDecoder(string codec, string expected)
    {
        var route = Route(codec);
        Assert.Equal(expected, FfmpegSource.Configuration(route).Video.VideoDecoderMode.ToString());
        Assert.Equal(expected, FfmpegSource.Configuration(route, new()).Video.VideoDecoderMode.ToString());
        Assert.Contains("route.RequiresD3D11 ? VideoDecoderMode.Automatic", ProductionSource());
    }

    [Theory]
    [InlineData("hevc")]
    [InlineData("av1")]
    public void AutoDoesNotEnableSoftwareOrSystemDecoderTuningEvenOnASoftwareRoute(string codec)
    {
        var tuning = new DebugFfmpegInputTuning(DecodeAheadFrames: 32, Dav1dMaxFrameDelay: 4, DecodeThreadPriority: 2,
            FilmGrain: DebugFilmGrain.Off, DecoderUseMaxResolution: 1, DecoderMaxDpbCount: 3, Decoder: DebugFfmpegDecoder.Auto);
        var route = Route(codec);
        var config = FfmpegSource.Configuration(route, tuning);
        Assert.Equal(VideoDecoderMode.Automatic, config.Video.VideoDecoderMode);
        Assert.Null(SoftwareDecodeTuning.BufferTimeMs(tuning.SoftwareVideo(route.SoftwareVideo), width: route.VideoWidth, height: route.VideoHeight));
        Assert.Equal(750, SoftwareDecodeTuning.BufferTimeMs(tuning.SoftwareVideo(route.SoftwareVideo), 750));
        Assert.Equal(new[] { "bale_system_decoder_dolby", "rw_timeout" }, config.FFmpegOptions.Keys.Order().ToArray());
    }

    [Fact]
    public void SoftwareHevcUsesNormalSoftwareDefaultsAndAv1KeepsItsZeroCopyPath()
    {
        var tuning = new DebugFfmpegInputTuning(Decoder: DebugFfmpegDecoder.Software);
        var hevc = FfmpegSource.Configuration(Route(), tuning);
        Assert.Equal("8", hevc.FFmpegOptions["bale_decode_ahead_frames"]);
        Assert.Equal("6", hevc.FFmpegOptions["bale_dav1d_max_frame_delay"]);
        Assert.DoesNotContain("bale_zero_copy", hevc.FFmpegOptions.Keys);
        Assert.DoesNotContain("bale_d3d11_upload", hevc.FFmpegOptions.Keys);
        Assert.DoesNotContain("bale_film_grain_mode", hevc.FFmpegOptions.Keys);
        var av1 = FfmpegSource.Configuration(Route("av1"), tuning);
        Assert.Equal("1", av1.FFmpegOptions["bale_zero_copy"]);
        Assert.Equal("6", av1.FFmpegOptions["bale_d3d11_upload"]);
        Assert.Equal("2", av1.FFmpegOptions["bale_film_grain_mode"]);
    }

    [Fact]
    public void SystemOverrideOnAv1DoesNotEnableSoftwareSetup()
    {
        var tuning = new DebugFfmpegInputTuning(Decoder: DebugFfmpegDecoder.System, DecoderUseMaxResolution: 1, DecoderMaxDpbCount: 3);
        var config = FfmpegSource.Configuration(Route("av1"), tuning);
        Assert.False(tuning.SoftwareVideo(true));
        Assert.Equal(VideoDecoderMode.ForceSystemDecoder, config.Video.VideoDecoderMode);
        Assert.DoesNotContain("bale_zero_copy", config.FFmpegOptions.Keys);
        Assert.DoesNotContain("bale_decode_ahead_frames", config.FFmpegOptions.Keys);
        Assert.Equal("1", config.FFmpegOptions["bale_decoder_use_max_resolution"]);
        Assert.Equal("3", config.FFmpegOptions["bale_decoder_max_dpb_count"]);
        // The wrapper cannot supply compressed AV1: reject its software fallback before mount.
        Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(Source(DecoderEngine.FFmpegSoftwareDecoder), Route("av1"), tuning));
    }

    [Theory]
    [InlineData("Auto", "FFmpegD3D11HardwareDecoder", true)]
    [InlineData("Auto", "FFmpegSoftwareDecoder", true)]
    [InlineData("Auto", "SystemDecoder", false)]
    [InlineData("Auto", "99", false)]
    [InlineData("System", "SystemDecoder", true)]
    [InlineData("System", "FFmpegD3D11HardwareDecoder", false)]
    [InlineData("Software", "FFmpegSoftwareDecoder", true)]
    [InlineData("Software", "FFmpegD3D11HardwareDecoder", false)]
    [InlineData(null, "FFmpegD3D11HardwareDecoder", false)]
    [InlineData(null, "FFmpegSoftwareDecoder", false)]
    [InlineData(null, "SystemDecoder", true)]
    public void ValidationAllowsOnlyTheRequestedEngines(string? decoder, string engine, bool allowed)
    {
        var error = Record.Exception(() => FfmpegSource.ValidateVideoDecoders(Source(Enum.Parse<DecoderEngine>(engine)), Route(), new(Decoder: ParseDecoder(decoder))));
        if (allowed) Assert.Null(error);
        else Assert.IsType<InvalidDataException>(error);
    }

    [Fact]
    public void AutoStillRejectsAnUnexpectedExtraStreamAndMissingVideo()
    {
        var tuning = new DebugFfmpegInputTuning(Decoder: DebugFfmpegDecoder.Auto);
        var source = Source(DecoderEngine.FFmpegD3D11HardwareDecoder);
        source.VideoStreams.Add(new() { DecoderEngine = DecoderEngine.SystemDecoder });
        Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(source, Route(), tuning));
        source.VideoStreams.Clear();
        Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(source, Route(), tuning));
        // Audio-only files retain their production no-video behavior.
        FfmpegSource.ValidateVideoDecoders(source, Route() with { VideoCodec = null }, tuning);
    }

    [Fact]
    public void DiagnosticRefreshesTheActualEngineAndMssSubtypeAfterStarting()
    {
        var source = Source(DecoderEngine.FFmpegD3D11HardwareDecoder);
        source.CurrentVideoStream!.HardwareDecoderStatus = HardwareDecoderStatus.Available;
        using var observer = new DebugVideoMediaType(source, VideoDecoderMode.Automatic);
        source.Source.Request(new object()); // Audio must not finish the video observer.
        Assert.Null(observer.Description);
        Assert.Equal(1, source.Source.Observers);
        var descriptor = new VideoStreamDescriptor { EncodingProperties = new() { Subtype = "NV12", Width = 3840, Height = 2160 } };
        source.Source.Request(descriptor);
        Assert.Contains("decoderStatus=pending", observer.Description);
        Assert.Equal(0, source.Source.Observers);
        // Model the wrapper's first decoded Main10 frame updating the live encoding properties.
        descriptor.EncodingProperties.Subtype = "{30313050-0000-0010-8000-00aa00389b71}";
        observer.Refresh(playbackStarted: true);
        Assert.Contains("subtype=P010 width=3840 height=2160", observer.Description);
        Assert.Contains("decoderMode=Automatic decoder=ffmpeg-d3d11 compressed=no hardwareStatus=Available decoderStatus=observed", observer.Description);
        // The wrapper changes DecoderEngine on software fallback; hardware status can stay Available.
        source.CurrentVideoStream.DecoderEngine = DecoderEngine.FFmpegSoftwareDecoder;
        observer.Refresh(playbackStarted: true);
        Assert.Contains("decoder=ffmpeg-software compressed=no", observer.Description);
        Assert.Contains("fallback=software", observer.Description);
        Assert.DoesNotContain("decoder=ffmpeg-d3d11", observer.Description);
        observer.Dispose();
        var last = observer.Description;
        source.CurrentVideoStream.DecoderEngine = DecoderEngine.SystemDecoder;
        observer.Refresh(playbackStarted: true);
        Assert.Equal(last, observer.Description);
    }

    [Fact]
    public void DiagnosticReportsCompressedSystemVideoAndDetachesOnEarlyDisposal()
    {
        var source = Source(DecoderEngine.SystemDecoder);
        using (var observer = new DebugVideoMediaType(source, VideoDecoderMode.ForceSystemDecoder))
        {
            source.Source.Request(new VideoStreamDescriptor { EncodingProperties = new() { Subtype = "HEVC" } });
            observer.Refresh(playbackStarted: true);
            Assert.Contains("subtype=HEVC", observer.Description);
            Assert.Contains("decoder=system compressed=yes", observer.Description);
            Assert.DoesNotContain("fallback=software", observer.Description);
        }
        using (var observer = new DebugVideoMediaType(source, VideoDecoderMode.Automatic))
            Assert.Equal(1, source.Source.Observers);
        Assert.Equal(0, source.Source.Observers);
    }

    [Theory]
    [InlineData("P010", "P010")]
    [InlineData("{30313050-0000-0010-8000-00aa00389b71}", "P010")]
    [InlineData("NV12", "NV12")]
    [InlineData("3231564e-0000-0010-8000-00aa00389b71", "NV12")]
    [InlineData("", "unset")]
    [InlineData("private diagnostic: suppressed", "unknown")]
    public void SubtypesReportMfNamesOrSanitizedGuids(string subtype, string expected)
    {
        var description = DebugVideoMediaType.Describe("ffmpeg", "mss", new() { Subtype = subtype });
        Assert.StartsWith($"route=ffmpeg source=mss subtype={expected} width=0 height=0 ", description);
    }

    [Fact]
    public void EffectiveSoftwareFlagAndLiveDecoderReachPlayerStatsAndWhere()
    {
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("media.SoftwareVideo = attempt.FfmpegTuning.SoftwareVideo(media.Route.SoftwareVideo)", media);
        Assert.Contains("softwareVideo = media.SoftwareVideo;", media);
        Assert.Contains("if (media.SoftwareVideo && media.FfmpegSource is { } softwareSource)", media);
        Assert.Contains("FfmpegSource.ValidateVideoDecoders(media.FfmpegSource, media.Route, attempt.FfmpegTuning)", media);
        Assert.Contains("decoder={media.FfmpegDecoderOverride} decoderMode={config.Video.VideoDecoderMode}", media);
        var stats = Read("PlayerPage.Stats.cs.txt");
        Assert.Contains("media.MssVideoMediaType?.Refresh(media.Started)", stats);
        Assert.Contains("if (_attempt is { } attempt) RefreshStats(attempt);", stats);
        Assert.Contains("(\"FFmpeg decoder override\", media?.FfmpegDecoderOverride ?? \"unavailable\")", stats);
    }

    private static DebugFfmpegDecoder? ParseDecoder(string? value) => value is null ? null : Enum.Parse<DebugFfmpegDecoder>(value);

    private static FFmpegMediaSource Source(DecoderEngine engine)
    {
        var source = new FFmpegMediaSource();
        source.VideoStreams.Add(new() { CodecName = "hevc", DecoderEngine = engine });
        return source;
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));

    // FfmpegSource has only DEBUG_SERVER conditional blocks. Check the actual production branch,
    // rather than assuming the first conditional occurs after all production code.
    internal static string ProductionSource()
    {
        var debug = false;
        return string.Join('\n', Read("FfmpegSource.cs.txt").Split('\n').Where(line =>
        {
            if (line == "#if DEBUG_SERVER") { debug = true; return false; }
            if (line == "#endif") { debug = false; return false; }
            return !debug;
        }));
    }
}
