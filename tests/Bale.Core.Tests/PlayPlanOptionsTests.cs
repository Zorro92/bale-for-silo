using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlayPlanOptionsTests
{
    [Fact]
    public void DefaultsUseMeasuredCapabilitiesAndAnImmediateStop()
    {
        Assert.True(PlayPlanOptions.TryParse([], out var options, out var error));
        Assert.Empty(error);
        Assert.False(options.ClaimAv1);
        Assert.Null(options.UrlFile);
        Assert.Equal(0, options.HoldSeconds);
        Assert.Same(ConsoleEvidence.Default, options.Capabilities());
    }

    [Fact]
    public void ParsesAnAv1UrlFileAndHoldWithoutTreatingOptionsAsAProfile()
    {
        Assert.True(PlayPlanOptions.TryParse(["--claim-av1", "--url-file", "/tmp/x", "--hold", "90"], out var options, out _));
        Assert.True(options.ClaimAv1);
        Assert.Equal("/tmp/x", options.UrlFile);
        Assert.Equal(90, options.HoldSeconds);
        Assert.True(PlayPlanOptions.TryParse(["Bale", "Test", "--hold", "0"], out _, out _));
        Assert.True(PlayPlanOptions.TryParse(["--hold", "3600", "Bale Test"], out _, out _));
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--url-file")]
    [InlineData("--hold")]
    [InlineData("Own")]
    [InlineData("Kids")]
    public void RejectsUnknownMissingAndUnsafeProfileArguments(string arg) =>
        Assert.False(PlayPlanOptions.TryParse([arg], out _, out _));

    [Theory]
    [InlineData("-1")]
    [InlineData("3601")]
    [InlineData("1.5")]
    [InlineData(" 90")]
    [InlineData("+90")]
    [InlineData("999999999999999999")]
    public void RejectsInvalidHoldTimes(string seconds) => Assert.False(
        PlayPlanOptions.TryParse(["--hold", seconds], out _, out _));

    [Fact]
    public void RejectsDuplicatesAndMissingFileValues()
    {
        Assert.False(PlayPlanOptions.TryParse(["--claim-av1", "--claim-av1"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--hold", "0", "--hold", "1"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--url-file", "/tmp/x", "--url-file", "/tmp/y"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--url-file", "--claim-av1"], out _, out _));
    }

    [Fact]
    public void LegacyAv1FlagIsANoOpAndCannotExpandTheMeasuredProductionClaim()
    {
        Assert.True(PlayPlanOptions.TryParse(["--claim-av1"], out var legacy, out _));
        Assert.True(PlayPlanOptions.TryParse([], out var normal, out _));
        Assert.Same(ConsoleEvidence.Default, legacy.Capabilities());
        Assert.Same(normal.Capabilities(), legacy.Capabilities());
        var av1 = Assert.Single(legacy.Capabilities().VideoDecode, codec => codec.Codec == "av1");
        Assert.Equal(new[] { 8, 10 }, av1.BitDepths);
        Assert.Equal(1920, av1.MaxWidth);
        Assert.Equal(1080, av1.MaxHeight);
        Assert.Equal(30, av1.MaxFrameRate);
        Assert.False(av1.Hardware);
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null,
            Bale.Core.Detail.PlaybackSelection.Auto, normal.Capabilities(), PlaybackTestSupport.Identity);
        Assert.Contains(PlaybackProtocol.SoftwareVideoDecodeFeature, start.ClientFeatures);
        Assert.Contains("av1", start.ClientPlaybackContext.Deliveries["original_http"].VideoCodecs);
        Assert.DoesNotContain("av1", start.ClientPlaybackContext.Deliveries["hls"].VideoCodecs);
    }

    [Fact]
    public async Task UrlFilesArePrivateAtomicAndReplaceRatherThanFollowSymlinks()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("bale-url-test-").FullName;
        try
        {
            var path = Path.Combine(directory, "url");
            var untouched = Path.Combine(directory, "untouched");
            await File.WriteAllTextAsync(untouched, "leave me alone");
            File.CreateSymbolicLink(path, untouched);
            var url = new Uri("https://example.org/direct?st=fixture");
            await PlayPlanUrlFile.WriteAsync(path, url);
            Assert.Equal("leave me alone", await File.ReadAllTextAsync(untouched));
            Assert.Null(new FileInfo(path).LinkTarget);
            Assert.Equal(url.AbsoluteUri, await File.ReadAllTextAsync(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            await PlayPlanUrlFile.WriteAsync(path, url);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CancelledUrlWriteLeavesNoPartialOrTemporaryFile()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("bale-url-cancel-").FullName;
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlayPlanUrlFile.WriteAsync(
                Path.Combine(directory, "url"), new Uri("https://example.org/direct"), cancellation.Token));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void PlayPlanHidesGrantsAndStopsInFinallyUsingAnIndependentDeadline()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PlayPlanCommand.cs.txt"));
        Assert.Contains("p.Name == \"Bale Test\"", source);
        Assert.Contains("options.Capabilities()", source);
        Assert.Contains("reporter.RunAsync(sessionId, heartbeat.Token)", source);
        Assert.Contains("Task.Delay(TimeSpan.FromSeconds(options.HoldSeconds), cancellationToken)", source);
        Assert.Contains("finally", source);
        Assert.Contains("new CancellationTokenSource(TimeSpan.FromSeconds(15))", source);
        Assert.Contains("reporter.StopAsync(sessionId, stopAt, true, cleanup.Token)", source);
        Assert.Contains("Console.WriteLine(\"url written\")", source);
        Assert.Contains("plan.Delivery != \"original_http\"", source);
        Assert.DoesNotContain("Terminal?.Message", source);
        Assert.DoesNotContain("stream.Headers", source);
        Assert.DoesNotContain("ex.Message", source);
        Assert.DoesNotContain("url={", source);
        var program = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CliProgram.cs.txt"));
        Assert.Contains("catch (Exception ex) when (command == \"play-plan\")", program);
    }
}
