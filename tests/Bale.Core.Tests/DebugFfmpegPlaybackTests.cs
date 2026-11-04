using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugFfmpegPlaybackTests
{
    [Fact]
    public void ToggleOnlyAcceptsKnownModesAndNeverPersistsThem()
    {
        var previous = DebugFfmpegPlayback.Mode;
        try
        {
            foreach (var mode in new[] { "off", "auto", "software", "system" })
            {
                Assert.True(DebugFfmpegPlayback.TrySet(mode));
                Assert.Equal(mode, DebugFfmpegPlayback.Mode);
            }
            foreach (var invalid in new string?[] { null, "", "Software", "hardware", " software", "unknown" })
            {
                Assert.False(DebugFfmpegPlayback.TrySet(invalid));
                Assert.Equal("system", DebugFfmpegPlayback.Mode);
            }
        }
        finally { DebugFfmpegPlayback.TrySet(previous); }
        var source = Read("DebugFfmpegPlayback.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("private static string _mode = \"off\"", source);
        Assert.Contains("Interlocked.Exchange", source);
        Assert.DoesNotContain("Settings", source);
        Assert.DoesNotContain("Storage", source);
        var server = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"POST\", \"/ffmpeg\"):", server);
        Assert.Contains("DebugFfmpegPlayback.TrySet(ffmpegMode)", server);
        Assert.Contains("Log.Info($\"Debug FFmpeg mode: {ffmpegMode}", server);
    }

    [Theory]
    [InlineData("off", false, false)]
    [InlineData("off", true, false)]
    [InlineData("auto", false, true)]
    [InlineData("software", false, true)]
    [InlineData("system", false, true)]
    [InlineData("auto", true, false)]
    [InlineData("software", true, false)]
    [InlineData("system", true, false)]
    [InlineData("unknown", false, false)]
    public void OnlyEnabledDirectStreamsUseFfmpeg(string mode, bool hls, bool expected) =>
        Assert.Equal(expected, DebugFfmpegPlayback.UseForDirectStream(mode, hls));

    [Theory]
    [InlineData("auto")]
    [InlineData("software")]
    [InlineData("system")]
    public void PlayerAndCliShareTheExplicitAv1Claim(string mode)
    {
        var capabilities = DebugFfmpegPlayback.Capabilities(mode);
        Assert.Same(DebugPlaybackCapabilities.Av1SoftwareClaim, capabilities);
        Assert.Same(new PlayPlanOptions(true, null, 0).Capabilities(), capabilities);
        var av1 = Assert.Single(capabilities.VideoDecode, decode => decode.Codec == "av1");
        Assert.Equal(new[] { 8, 10 }, av1.BitDepths);
        Assert.Equal(3840, av1.MaxWidth);
        Assert.Equal(2160, av1.MaxHeight);
        Assert.False(av1.Hardware);
        Assert.DoesNotContain("av1", capabilities.HardwareVideoCodecs);
        Assert.DoesNotContain("av1", ConsoleEvidence.Default.VideoCodecs);
        Assert.Same(ConsoleEvidence.Default, DebugFfmpegPlayback.Capabilities("off"));
    }

    [Fact]
    public void TheAttemptCapturesModeBeforePlanningSoToggleChangesCannotChangeItsBackend()
    {
        var source = Read("PlayerPage.cs.txt");
        Assert.Contains("public string FfmpegMode { get; } = DebugFfmpegPlayback.Mode", source);
        Assert.Contains("capabilities = DebugFfmpegPlayback.Capabilities(attempt.FfmpegMode)", source);
        Assert.Contains("startPosition, capabilities, DeviceIdentity.Current()", source);
        Assert.Contains("#if DEBUG_SERVER\n        // Match the capability claim", source);
    }

    [Fact]
    public void RealPlaybackFeedsAuthenticatedBytesAndKeepsTheNativeSourceUntilStop()
    {
        var source = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("using FFmpegInteropX;\n#endif", source);
        Assert.Contains("media.Stream = await transport.OpenStreamAsync(media.Token)", source);
        Assert.Contains("media.RandomStream = media.Stream.AsRandomAccessStream()", source);
        Assert.Contains("DebugFfmpegPlayback.UseForDirectStream(attempt.FfmpegMode, transport.IsHls)", source);
        Assert.Contains("media.FfmpegSource = await FFmpegMediaSource.CreateFromStreamAsync(media.RandomStream, config).AsTask()", source);
        Assert.Contains("media.Item = media.FfmpegSource.CreateMediaPlaybackItem()", source);
        Assert.Contains("media.Item ??= new MediaPlaybackItem(media.Source)", source);
        Assert.Contains("Video.SetMediaPlayer(player)", source);
        Assert.Contains("player.Source = media.Item", source);
        Assert.Contains("ffmpeg.PlaybackSession = sender.PlaybackSession", source);
        Assert.DoesNotContain("CreateFromUriAsync", source);
        Assert.DoesNotContain("ex.Message", source);
        var hls = source[source.IndexOf("if (transport.IsHls)", StringComparison.Ordinal)
            ..source.IndexOf("media.Stream = await transport.OpenStreamAsync", StringComparison.Ordinal)];
        Assert.Contains("AdaptiveMediaSource.CreateFromStreamAsync", hls);
        Assert.Contains("media.Adaptive.DownloadRequested += media.DownloadHandler", hls);
        Assert.DoesNotContain("FFmpeg", hls);
        var nativeDisposal = source.IndexOf("Cleanup(\"dispose FFmpeg source\"", StringComparison.Ordinal);
        Assert.True(source.IndexOf("Cleanup(\"dispose player\"", StringComparison.Ordinal) < nativeDisposal);
        Assert.True(nativeDisposal < source.IndexOf("Cleanup(\"dispose random stream\"", StringComparison.Ordinal));
    }

    [Fact]
    public void FfmpegStatsUseNativeCodecAndDecoderInfoAndTheSameProcessCpuCounters()
    {
        var source = Read("PlayerPage.Stats.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        string[] required =
        [
            "if (media.FfmpegSource is not null)", "stats = ReadFfmpegStats(stats, media)",
            "else if (media is { Opened: true, Item: { } item })", "FFmpegInteropX ({media.FfmpegMode})",
            "BackendRoute = \"FFmpeg Direct\"", "CurrentVideoStream", "CurrentAudioStream",
            "VideoCodec = DebugScreenText.Compact(video.CodecName", "VideoDecoderName = DebugFfmpegSource.DecoderName(video.DecoderEngine)",
            "AudioCodec = DebugScreenText.Compact(audio.CodecName", "AudioDecoderName = DebugFfmpegSource.DecoderName(audio.DecoderEngine)",
            "DebugProcessCpu.Read(out var failure)", "Stopwatch.GetElapsedTime(previous, now)",
            "DebugFfmpegPlaytest.CpuCores(media.CpuTime, value, wall)", "media.LastCpuFailure = failure",
            "SetStats(stats, processCpu)", "(\"CPU\", processCpu)",
        ];
        foreach (var text in required) Assert.Contains(text, source);
        Assert.DoesNotContain("BitsPerSample", source);
        Assert.DoesNotContain("ex.Message", source);
        var native = Read("DebugFfmpegSource.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", native);
        Assert.Contains("FFmpegInteropLogging.SetLogProvider(null)", native);
        Assert.Contains("DecoderEngine.SystemDecoder => \"system\"", native);
        Assert.Contains("DecoderEngine.FFmpegSoftwareDecoder => \"FFmpeg software\"", native);
        Assert.Contains("DecoderEngine.FFmpegD3D11HardwareDecoder => \"FFmpeg D3D11\"", native);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
