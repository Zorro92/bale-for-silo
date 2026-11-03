using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugPlaytestTests
{
    [Theory]
    [InlineData("https://example.org/stream.mp4?x=1&y=two", null, 3)]
    [InlineData("http://example.org/live", "1", 1)]
    [InlineData("https://example.org/stream.webm", "10", 10)]
    public void AcceptsOnlyPublicUrlSyntaxAndBoundedDurations(string url, string? seconds, int expected)
    {
        Assert.True(DebugPlaytest.TryParse(url, seconds, out var uri, out var duration, out var error));
        Assert.Equal(new Uri(url), uri);
        Assert.Equal(expected, duration);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stream.mp4")]
    [InlineData("file:///stream.mp4")]
    [InlineData("ftp://example.org/stream.mp4")]
    [InlineData("https://user:password@example.org/stream.mp4")]
    public void RejectsMissingRelativeNonHttpAndCredentialBearingUrls(string? url)
    {
        Assert.False(DebugPlaytest.TryParse(url, null, out _, out _, out var error));
        Assert.Contains("http(s)", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("-1")]
    [InlineData("3.5")]
    [InlineData(" 3")]
    [InlineData("+3")]
    [InlineData("999999999999999")]
    public void RejectsInvalidDurations(string seconds)
    {
        Assert.False(DebugPlaytest.TryParse("https://example.org/stream.mp4", seconds, out _, out _, out var error));
        Assert.Contains("1 to 10", error);
    }

    [Fact]
    public void PlaysRequiresAnOpenedStreamAndAClockThatAdvancedFromItsInitialPosition()
    {
        Assert.Equal("Verdict: PLAYS\n", DebugPlaytest.Verdict(true, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), []));
        Assert.Equal("Verdict: FAILS - media did not open\n", DebugPlaytest.Verdict(false, TimeSpan.Zero, TimeSpan.FromSeconds(3), []));
        Assert.Equal("Verdict: FAILS - playback position did not advance\n",
            DebugPlaytest.Verdict(true, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), []));
        Assert.StartsWith("Verdict: FAILS", DebugPlaytest.Verdict(true, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), []));
    }

    [Theory]
    [InlineData("Video 1 DecoderStatus=UnsupportedSubtype")]
    [InlineData("Audio 1 DecoderStatus=Degraded")]
    [InlineData("VideoTrack.OpenFailed ExtendedError=0xC00D5212")]
    [InlineData("MediaFailed: Error=DecodingError ErrorMessage=decode failed ExtendedErrorCode=0xC00D5212")]
    public void AnyTrackOrPlayerFailureOverridesAMovingClock(string failure)
    {
        Assert.Equal($"Verdict: FAILS - {failure}\n", DebugPlaytest.Verdict(true, TimeSpan.Zero, TimeSpan.FromSeconds(3), [failure]));
    }

    [Fact]
    public void FailureReasonsAreSingleLineBoundedAndDeduplicated()
    {
        var output = DebugPlaytest.Verdict(false, TimeSpan.Zero, TimeSpan.Zero, ["open\r\ntimed out", "open timed out", new string('x', 500)]);
        Assert.Equal("Verdict: FAILS - open timed out; " + new string('x', 297) + "...; media did not open\n", output);
    }

    [Fact]
    public void ProbeStaysGuardedIsolatedMutedBoundedAndCleansUpItsHandlersAndSource()
    {
        // Linux cannot compile or exercise the WinRT player; protect its wiring and release guard.
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.Capabilities.cs.txt"));
        var probe = source[..source.IndexOf("// only=video", StringComparison.Ordinal)];
        Assert.StartsWith("#if DEBUG_SERVER", probe);
        Assert.Contains("Interlocked.CompareExchange(ref _playtestBusy, 1, 0)", probe);
        Assert.Contains("409 Conflict", probe);
        Assert.Contains("using var source = MediaSource.CreateFromUri(uri);", probe);
        Assert.Contains("new MediaPlayer { IsMuted = true, AutoPlay = false }", probe);
        Assert.Contains("new MediaPlaybackItem(source)", probe);
        Assert.Contains("TaskCreationOptions.RunContinuationsAsynchronously", probe);
        Assert.Contains("Task.Delay(DebugPlaytest.OpenTimeoutMs)", probe);
        Assert.Contains("Task.Delay(TimeSpan.FromSeconds(duration))", probe);
        Assert.Contains("player.MediaOpened -= OnOpened", probe);
        Assert.Contains("player.MediaFailed -= OnFailed", probe);
        Assert.Contains("item.VideoTracksChanged -= OnVideoTracksChanged", probe);
        Assert.Contains("track.OpenFailed -= OnVideoOpenFailed", probe);
        Assert.Contains("player.Pause()", probe);
        Assert.Contains("player.Source = null", probe);
        Assert.Contains("MediaDecoderStatus.FullySupported", probe);
        Assert.Contains("Debug playtest: before open", probe);
        Assert.Contains("Debug playtest: before play", probe);
        Assert.Contains("Debug playtest: done", probe);
        Assert.DoesNotContain("Session.Client", probe);
        Assert.DoesNotContain("Authorization", probe);
        Assert.DoesNotContain("chromium", probe);
        var server = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.cs.txt"));
        Assert.Contains("case (\"GET\", \"/playtest\"):", server);
    }

    [Fact]
    public void WrapperEncodesUrlsAndKeepsEachCallUnderTwentySecondsWithALongerSuiteTimeout()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bale-debug.sh"));
        Assert.Contains("--max-time \"$((playtest_seconds + 9))\"", script);
        Assert.Contains("--data-urlencode \"url=$1\" --data-urlencode \"seconds=$playtest_seconds\"", script);
        Assert.Contains("timeout 300 scripts/bale-debug.sh playtest suite", script);
        Assert.Contains("suite_base='https://raw.githubusercontent.com/chromium/chromium/main/media/test/data'", script);
        Assert.Contains("playtest_url \"$suite_base/$sample\" || suite_status=$?", script);
        string[] samples =
        [
            "bear-1280x720.mp4", "bear-hevc-frag.mp4", "bear-320x240-v_frag-hevc.mp4", "bear-vp9.webm",
            "bear-av1.mp4", "bear-av1.webm", "bear-av1-320x180-10bit.mp4", "bear-ac3-only-frag.mp4",
            "bear-eac3-only-frag.mp4", "bear-flac.mp4", "bear-opus.mp4",
        ];
        foreach (var sample in samples) Assert.Contains(sample, script);
        Assert.Equal(8000, DebugPlaytest.OpenTimeoutMs);
    }
}
