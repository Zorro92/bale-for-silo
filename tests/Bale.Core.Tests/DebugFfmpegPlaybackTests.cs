using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugFfmpegPlaybackTests
{
    [Fact]
    public void ToggleOnlyAcceptsPolicyDirectAndOffAndNeverPersistsThem()
    {
        var previous = DebugFfmpegPlayback.Mode;
        try
        {
            foreach (var mode in new[] { "off", "direct", "policy" })
            {
                Assert.True(DebugFfmpegPlayback.TrySet(mode));
                Assert.Equal(mode, DebugFfmpegPlayback.Mode);
            }
            foreach (var invalid in new string?[] { null, "", "Policy", "auto", "software", "system", " policy", "unknown" })
            {
                Assert.False(DebugFfmpegPlayback.TrySet(invalid));
                Assert.Equal("policy", DebugFfmpegPlayback.Mode);
            }
        }
        finally { DebugFfmpegPlayback.TrySet(previous); }
        var source = Read("DebugFfmpegPlayback.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("private static string _mode = \"policy\"", source);
        Assert.Contains("Interlocked.Exchange", source);
        Assert.DoesNotContain("Settings", source);
        Assert.DoesNotContain("Storage", source);
        var server = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"POST\", \"/ffmpeg\"):", server);
        Assert.Contains("DebugFfmpegPlayback.TrySet(ffmpegMode)", server);
    }

    [Fact]
    public void PolicyUsesMeasuredCapabilitiesAndOffWithdrawsEveryFfmpegDependency()
    {
        Assert.Same(ConsoleEvidence.Default, DebugFfmpegPlayback.Capabilities("policy"));
        Assert.Same(ConsoleEvidence.Default, DebugFfmpegPlayback.Capabilities("direct"));
        Assert.Same(new PlayPlanOptions(true, null, 0, FallbackClaims: true).Capabilities(), DebugFfmpegPlayback.Capabilities("policy"));
        var off = DebugFfmpegPlayback.Capabilities("off");
        Assert.DoesNotContain("av1", off.VideoCodecs);
        Assert.All(off.VideoDecode, decode => Assert.True(decode.Hardware));
        Assert.Equal(SoftwareDecodePolicy.SystemAudioCodecs, off.AudioDecodeCodecs);
        Assert.Empty(off.AudioPassthroughCodecs);
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null, PlaybackSelection.Auto, off, PlaybackTestSupport.Identity);
        Assert.DoesNotContain(PlaybackProtocol.SoftwareVideoDecodeFeature, start.ClientFeatures);
        Assert.All(start.ClientPlaybackContext.Deliveries.Values, delivery =>
        {
            Assert.DoesNotContain("av1", delivery.VideoCodecs);
            Assert.DoesNotContain(delivery.AudioDecodeCodecs, SoftwareDecodePolicy.AudioCodecs.Contains);
        });
        Assert.Contains("av1", ConsoleEvidence.Default.VideoCodecs);
        Assert.Contains("dts", ConsoleEvidence.Default.AudioDecodeCodecs);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("off")]
    public void BothOverridesKeepTheRuntimeDisplay(string mode)
    {
        var detected = ConsoleEvidence.Default with { Hdr = new() { Hdr10 = true }, DisplayHdrEvidence = "exact" };
        var capabilities = DebugFfmpegPlayback.Capabilities(mode, detected);
        Assert.Same(detected.Hdr, capabilities.Hdr);
        Assert.Equal("exact", capabilities.DisplayHdrEvidence);
        Assert.Equal(mode == "policy", capabilities.VideoCodecs.Contains("av1"));
    }

    [Fact]
    public void TheAttemptCapturesTheOverrideBeforePlanningAndRecoveryKeepsTheFailedRoute()
    {
        var source = Read("PlayerPage.cs.txt");
        Assert.Contains("public string FfmpegMode { get; } = DebugFfmpegPlayback.Mode", source);
        Assert.Contains("var (capabilities, softwareVideo) = await PlaybackClaims.DetectAsync()", source);
        Assert.Contains("var capabilities = DisplayHdrOutput.DetectCapabilities(ConsoleEvidence.ForHardware(hardware))", Read("PlaybackClaims.cs.txt"));
        Assert.Contains("capabilities = DebugFfmpegPlayback.Capabilities(attempt.FfmpegMode, capabilities)", source);
        Assert.Contains("public bool FfmpegEnabled => true", source);
        Assert.Contains("public bool FfmpegEnabled => FfmpegMode != \"off\"", source);
        Assert.Contains("public bool ForceFfmpeg => FfmpegMode == \"direct\"", source);
        // Release builds never force the FFmpeg demuxer.
        Assert.Contains("public bool ForceFfmpeg => false", source);
        Assert.Contains("var failedRoute = attempt.Media?.Route", source);
        Assert.Contains("position, failure, failedRoute)", source);
        var recovery = source[source.IndexOf("var failedRoute =", StringComparison.Ordinal)..];
        Assert.True(recovery.IndexOf("var failedRoute =", StringComparison.Ordinal) < recovery.IndexOf("DisposeMount(attempt)", StringComparison.Ordinal));
    }

    [Fact]
    public void RealPlaybackFeedsAuthenticatedBytesAndKeepsTheNativeSourceUntilStop()
    {
        var source = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("using FFmpegInteropX;", source);
        Assert.DoesNotContain("#if DEBUG_SERVER\nusing FFmpegInteropX", source);
        Assert.Contains("media.Stream = await OpenStepAsync(transport.OpenNativeStreamAsync(media.Token), media.Token);", source);
        Assert.Contains("media.RandomStream = NativeRandomAccessStream(media.Stream!) ?? media.Stream!.AsRandomAccessStream()", source);
        Assert.Contains("Route = PlaybackRoutePolicy.Decide(attempt.Plan!, transport.IsHls, attempt.FfmpegEnabled, attempt.ForceFfmpeg,", source);
        Assert.Contains("if (media.Route.UseFfmpeg)", source);
        Assert.Contains("FfmpegSource.Configuration(media.Route, attempt.Dav1dGpuFilmGrain)", source);
        Assert.Contains(": await FFmpegMediaSource.CreateFromStreamAsync(media.RandomStream!, config).AsTask()", source);
        // A progressive remux reaches FFmpeg through its non-seekable input, still as authenticated bytes.
        Assert.Contains("? await FFmpegMediaSource.CreateFromInputStreamAsync(forward, config).AsTask()", source);
        Assert.Contains("media.Progressive = attempt.UseRustTransport\n                        ? await OpenStepAsync(transport.OpenNativeProgressiveAsync(media.Token), media.Token)\n                        : await OpenStepAsync(transport.OpenProgressiveAsync(media.Token), media.Token);", source);
        Assert.Contains("FfmpegSource.ValidateVideoDecoders(media.FfmpegSource, media.Route)", source);
        Assert.Contains("media.Item = media.FfmpegSource.CreateMediaPlaybackItem()", source);
        Assert.Contains("media.Item ??= new MediaPlaybackItem(media.Source)", source);
        Assert.Contains("player.Source = media.Item", source);
        // The setter takes the wrapper's source lock: handed over before Source, not on the UI thread after open.
        Assert.Contains("if (media.FfmpegSource is { } sessionSource) sessionSource.PlaybackSession = player.PlaybackSession;", source);
        Assert.Contains("packaged.AudioStreams.Select(stream => stream.StreamIndex)", source);
        Assert.Contains("PlaybackRoutePolicy.AudioTrackIndex(media.Route.AudioOrdinal, streams, expectedCount", source);
        // Silo packages carry at most one audio stream on every packaged delivery, HLS included.
        Assert.Contains("int? expectedCount = catalogCount > 0 ? 1 : null;", source);
        Assert.DoesNotContain("Delivery == \"server_remux_progressive\") expectedCount", source);
        Assert.Contains("if (index >= 0) sender.SelectAudio(index);", source);
        Assert.True(source.IndexOf("sender.SelectAudio(index)", StringComparison.Ordinal) < source.IndexOf("sender.Play()", StringComparison.Ordinal));
        Assert.True(source.IndexOf("SelectOriginalAudioTrack(attempt, media, attempt.Plan.SelectedTracks?.Audio?.Index)", StringComparison.Ordinal)
            < source.IndexOf("sender.Play()", StringComparison.Ordinal));
        Assert.Contains("PlaybackOriginalAudioSelection.Select(planIndex, catalog, mounted", source);
        Assert.Contains("() => player.SelectedAudioIndex", source);
        // MediaPlayerBackend reports FullySupported/Degraded tracks as decodable.
        Assert.Contains("stream?.Name ?? track.Name, track.Decodable", source);
        Assert.Contains("media.SelectedAudioTrack.OpenFailed += OnAudioTrackOpenFailed", source);
        Assert.Contains("audio.OpenFailed -= OnAudioTrackOpenFailed", source);
        Assert.DoesNotContain("CreateFromUriAsync", source);
        Assert.DoesNotContain("ex.Message", source);
        Assert.DoesNotContain("AdaptiveMediaSource", source);
        var nativeDisposal = source.IndexOf("Cleanup(\"dispose FFmpeg source\"", StringComparison.Ordinal);
        Assert.True(source.IndexOf("Cleanup(\"dispose player\"", StringComparison.Ordinal) < nativeDisposal);
        Assert.True(nativeDisposal < source.IndexOf("Cleanup(\"dispose random stream\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionConfigurationLimitsAutomaticToD3D11RoutesAndUsesSupportedSystemAudioSwitches()
    {
        var source = Read("FfmpegSource.cs.txt");
        // Production tunes software video only; input experiments stay in the debug-only overload.
        var production = DebugFfmpegDecoderTests.ProductionSource();
        // Production sets only exact seeks on General (2026-11-16); input experiments stay debug-only.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(production, @"config\.General\."));
        Assert.Contains("config.General.FastSeek = route.SoftwareVideo;", production);
        Assert.Contains("ApplyDecodeTuning(config, SoftwareDecodeTuning.ForVideo(softwareVideo, route.VideoWidth, route.VideoHeight))", production);
        Assert.Contains("FfmpegLogging.Configure()", production);
        Assert.Contains("VideoDecoderMode.ForceFFmpegSoftwareDecoder : VideoDecoderMode.ForceSystemDecoder", source);
        Assert.Contains("route.RequiresD3D11 ? VideoDecoderMode.Automatic", production);
        Assert.Contains("config.Audio.SystemDecoderAAC = true", source);
        Assert.Contains("config.Audio.SystemDecoderMP3 = true", source);
        // Patch 0021: AC3/EAC3 go undecoded to the system Dolby decoder on every FFmpeg mount.
        Assert.Contains("config.FFmpegOptions[\"bale_system_decoder_dolby\"] = \"1\"", production);
        Assert.Contains("config.Subtitles.AutoSelectForcedSubtitles = false", source);
        // Every video stream goes to the shared policy (SoftwareVideoSupportTests cover it).
        Assert.Contains("var streams = source.VideoStreams.Select(video => ((string?)video.CodecName,", source);
        Assert.Contains("video.DecoderEngine == DecoderEngine.FFmpegSoftwareDecoder, video.DecoderEngine == DecoderEngine.SystemDecoder", source);
        Assert.Contains("if (!SoftwareDecodePolicy.VideoDecodersAllowed(route.SoftwareVideo, streams, route.VideoCodec))", source);
        Assert.DoesNotContain("ex.Message", source);
    }

    [Fact]
    public void FfmpegStatsUseNativeCodecAndDecoderInfoAndRetainProcessCpuCounters()
    {
        var source = Read("PlayerPage.Stats.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        // The official rows are read in every build (the Options panel's Stats tab); the overlay builds on them.
        var hud = Read("PlayerPage.HudStats.cs.txt");
        Assert.DoesNotContain("#if", hud);
        string[] official =
        [
            "if (media.FfmpegSource is not null)", "stats = ReadFfmpegStats(stats, media)",
            "else if (media is { Opened: true, Item: { } item })", "BackendDisplayName = \"FFmpegInteropX\"",
            "BackendRoute = media.Progressive is null ? \"FFmpeg Direct\" : media.Transport.IsHls ? \"FFmpeg HLS\" : \"FFmpeg Progressive\"", "CurrentVideoStream", "CurrentAudioStream",
            "VideoDecoderName = FfmpegSource.DecoderName(video.DecoderEngine)",
            "AudioDecoderName = FfmpegSource.DecoderName(audio.DecoderEngine)",
            "bandwidth = media.Throughput.Sample(stream.DownloadedBytes, stream.DownloadTime)",
        ];
        foreach (var text in official) Assert.Contains(text, hud);
        Assert.DoesNotContain("ex.Message", hud);
        string[] required =
        [
            "var stats = RefreshHudStats(attempt);", "DebugProcessCpu.Read(out var failure)", "Stopwatch.GetElapsedTime(previous, now)",
            "DebugFfmpegPlaytest.CpuUsage(media.CpuTime, value, wall, Environment.ProcessorCount)", "SetStats(stats, processCpu, frames)", "(\"CPU\", processCpu)",
        ];
        foreach (var text in required) Assert.Contains(text, source);
        Assert.DoesNotContain("ex.Message", source);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
