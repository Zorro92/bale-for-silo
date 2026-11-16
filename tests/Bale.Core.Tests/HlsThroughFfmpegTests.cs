using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

/// <summary>HLS through FFmpeg (docs/rust-port.md): the debug switch, the mount clock and the player wiring.</summary>
public sealed class HlsThroughFfmpegTests
{
    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    [Fact]
    public void TheSwitchDefaultsToWindowsHlsAndTakesOnlyItsTwoModes()
    {
        var previous = DebugHlsEngineSwitch.Mode;
        try
        {
            Assert.Equal("native", previous);
            Assert.True(DebugHlsEngineSwitch.TrySet("ffmpeg"));
            Assert.Equal("ffmpeg", DebugHlsEngineSwitch.Mode);
            foreach (var invalid in new string?[] { null, "", "FFmpeg", "rust", " native", "ffmpeg&x=1" })
                Assert.False(DebugHlsEngineSwitch.TrySet(invalid));
            Assert.Equal("ffmpeg", DebugHlsEngineSwitch.Mode);
        }
        finally
        {
            DebugHlsEngineSwitch.TrySet(previous);
        }
    }

    [Fact]
    public void AMountStartingAtASegmentShiftsOnlyItsPlayerClock()
    {
        var timeline = PlaybackTimeline.From(new TimelineV3 { SourceStartSeconds = 1200, PlayerStartSeconds = 15, TimelineOffsetSeconds = 1185 });
        var mount = timeline.StartingAt(12);
        // FFmpeg reads 0 at the segment that starts at player 12: source 1197.
        Assert.Equal(1197, mount.SourceForPlayer(0));
        Assert.Equal(1203, mount.SourceForPlayer(6));
        Assert.Equal(3, mount.PlayerForSource(1200));
        Assert.Same(timeline, timeline.StartingAt(0));
        Assert.Same(timeline, timeline.StartingAt(double.NaN));
    }

    [Fact]
    public void ThePlayerOpensTheReaderRemountsLocallyAndFallsBackToWindowsHls()
    {
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("if (transport.IsHls && !media.Route.UseFfmpeg)", media);
        Assert.Contains("var hls = await transport.OpenNativeHlsAsync(playerStart, media.Token);", media);
        Assert.Contains("transport.StartPlayerClockAt(hls.SegmentStartSeconds);", media);
        Assert.Contains("catch (HlsUnsupportedException) when (!attempt.HlsReaderRefused && ReferenceEquals(attempt.Media, media))", media);
        Assert.Contains("if (media.Progressive is null) sender.PlaybackSession.Position", media);
        Assert.Contains("hlsThroughFfmpeg: attempt.HlsThroughFfmpeg", media);
        var seek = Read("PlayerPage.Seek.cs.txt");
        Assert.Contains("var remount = forwardHls && request.CanSeekInMount(attempt.Plan!, isHls: true, canSeek: true);", seek);
        Assert.Contains("var local = !forwardHls && request.CanSeekInMount(", seek);
        Assert.Contains("media = await RemountHlsAsync(attempt, request, paused);", seek);
        var player = Read("PlayerPage.cs.txt");
        Assert.Contains("public bool HlsThroughFfmpeg => HlsEngine == \"ffmpeg\" && !HlsReaderRefused;", player);
        // Release builds keep Windows' HLS until the console A/B.
        Assert.Contains("public bool UseRustTransport => true;\n        public bool HlsThroughFfmpeg => false;", player);
    }
}
