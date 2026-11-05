using System.Globalization;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugPlaybackSeekTests
{
    [Theory]
    [InlineData("0", null, 0, false)]
    [InlineData("12.5", "true", 12.5, true)]
    [InlineData("123", "false", 123, false)]
    public void ParsesAbsoluteSecondsAndOptionalPause(string seconds, string? pause, double expected, bool paused)
    {
        Assert.True(DebugPlaybackSeek.TryParse(seconds, pause, out var request));
        Assert.Equal(expected, request.Seconds);
        Assert.Equal(paused, request.Pause);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("-1", null)]
    [InlineData("NaN", null)]
    [InlineData("Infinity", null)]
    [InlineData("1e9", null)]
    [InlineData("9999999999999999999999999999", null)]
    [InlineData(" 12", null)]
    [InlineData("12,5", null)]
    [InlineData("1", "pause")]
    [InlineData("1", "TRUE")]
    public void RejectsUnsafeOrAmbiguousInput(string? seconds, string? pause) =>
        Assert.False(DebugPlaybackSeek.TryParse(seconds, pause, out _));

    [Fact]
    public void UsesInvariantSecondsEvenWithADecimalCommaCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(DebugPlaybackSeek.TryParse("12.5", null, out var request));
            Assert.Equal(12.5, request.Seconds);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(0, 0, 100, 0)]
    [InlineData(42.5, 0, 100, 42.5)]
    [InlineData(150, 120, 1000, 30)]
    [InlineData(120, 120, 1000, 0)]
    [InlineData(5, -10, 100, 15)]
    public void MapsSourceSecondsForNativeFfmpegAndReanchoredHls(double source, double offset, double duration, double player) =>
        Assert.Equal(player, new DebugPlaybackSeek(source, true).PlayerPosition(new(0, 0, offset), duration));

    [Fact]
    public void RejectsOutOfTimelinePositionsAndDoesNotRequireAKnownDuration()
    {
        Assert.Null(new DebugPlaybackSeek(100, false).PlayerPosition(new(120, 0, 120), 1000));
        Assert.Null(new DebugPlaybackSeek(101, false).PlayerPosition(new(0, 0, 0), 100));
        Assert.Equal(500, new DebugPlaybackSeek(500, false).PlayerPosition(new(0, 0, 0), null));
        Assert.Null(new DebugPlaybackSeek(TimeSpan.MaxValue.TotalSeconds, false).PlayerPosition(new(0, 0, 0), null));
    }

    [Fact]
    public void DebugSeekDelegatesToTheSerializedRealPlayerSeekAndWaitsForTheMediaClock()
    {
        var wrapper = Read("PlayerPage.Debug.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", wrapper);
        Assert.Contains("await SeekAsync(new PlaybackSeek(request.Seconds, request.Pause))", wrapper);
        var source = Read("PlayerPage.Seek.cs.txt");
        Assert.DoesNotContain("#if DEBUG_SERVER\nusing", source);
        foreach (var required in new[] { "_lifecycle.Wait(0)", "IsCurrent(attempt, media)", "session.CanSeek",
            "request.PlayerPosition(media.Transport.Timeline, _duration)", "session.SeekCompleted += OnSeekCompleted",
            "session.SeekCompleted -= OnSeekCompleted", "session.Position = TimeSpan.FromSeconds(target)",
            "Task.WhenAny(completed.Task", "if (paused) player.Pause()", "SourcePosition(attempt)", "_lifecycle.Release()",
            "Reporter.ReportProgressAsync(attempt.SessionId!, source, paused", "ReanchorSeekAsync", "FrameDelivery?.SetSeeking(true)" })
            Assert.Contains(required, source);
        Assert.DoesNotContain("ex.Message", source + wrapper);
        var endpoint = Read("DebugServer.Playback.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", endpoint);
        Assert.Contains("OnFfmpegUiAsync", endpoint);
        Assert.DoesNotContain("ex.Message", endpoint);
        Assert.Contains("case (\"POST\", \"/seek\")", Read("DebugServer.cs.txt"));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
