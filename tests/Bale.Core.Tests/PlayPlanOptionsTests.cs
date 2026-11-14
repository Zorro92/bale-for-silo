using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlayPlanOptionsTests
{
    [Fact]
    public void HlsReanchorProbeKeepsTracksDecodeClaimsAndExplicitStartOver()
    {
        Assert.True(PlayPlanOptions.TryParse(["--delivery", "hls", "--start", "0", "--reanchor", "900.5",
            "--reanchor", "890.5", "--inspect-hls"], out var options, out _));
        Assert.Equal(0, options.StartSeconds);
        Assert.Equal(new[] { 900.5, 890.5 }, options.ReanchorSeconds);
        Assert.True(options.InspectHls);
        var original = PlaybackTestSupport.Start(new(AudioIndex: 1));
        var forced = options.Apply(original);
        Assert.Same(original.ClientCapabilities, forced.ClientCapabilities);
        Assert.Equal(original.AudioTrackIndex, forced.AudioTrackIndex);
        Assert.Same(original.ClientPlaybackContext.Deliveries["hls"], forced.ClientPlaybackContext.Deliveries["hls"]);
        Assert.All(forced.ClientPlaybackContext.Deliveries.Where(pair => pair.Key != "hls"), pair =>
        {
            Assert.False(pair.Value.Enabled);
            Assert.False(pair.Value.SupportedOnDevice);
        });
    }

    [Theory]
    [InlineData("--delivery", "direct")]
    [InlineData("--start", "-1")]
    [InlineData("--start", "NaN")]
    [InlineData("--reanchor", "Infinity")]
    [InlineData("--reanchor", "31536001")]
    [InlineData("--reanchor", "12,5")]
    public void HlsProbeRejectsUnknownDeliveryAndInvalidSourcePositions(string flag, string value) =>
        Assert.False(PlayPlanOptions.TryParse([flag, value], out _, out _));

    [Fact]
    public void Hdr10PlansLikeHdrPretendOnASeriesConsole()
    {
        Assert.True(PlayPlanOptions.TryParse(["--hdr10"], out var options, out _));
        Assert.True(options.Hdr10);
        var device = options.Capabilities();
        Assert.True(device.Hdr.Hdr10);
        Assert.Equal("exact", device.DisplayHdrEvidence);
        Assert.Contains(device.VideoDecode, decode => decode.Codec == "hevc" && decode.MaxWidth >= 3840 && decode.BitDepths.Contains(10));
        Assert.True(device.OriginalDolbyVision8BaseLayerSupported);
        Assert.False(PlayPlanOptions.TryParse(["--hdr10", "--hdr10"], out _, out _));
    }

    [Fact]
    public void DefaultsUseMeasuredCapabilitiesAndAnImmediateStop()
    {
        Assert.True(PlayPlanOptions.TryParse([], out var options, out var error));
        Assert.Empty(error);
        Assert.False(options.ClaimAv1);
        Assert.Null(options.UrlFile);
        Assert.Null(options.FileId);
        Assert.Equal(1080, options.Av1MaxHeight);
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

    [Fact]
    public void ParsesAnExplicitVersionAndAv1HeightWithAHeadSaveInEitherOrder()
    {
        Assert.True(PlayPlanOptions.TryParse(
            ["--file", "file-av1", "--av1-max-height", "2160", "--save-head", "head.mkv", "1"], out var options, out var error));
        Assert.Empty(error);
        Assert.Equal("file-av1", options.FileId);
        Assert.Equal(2160, options.Av1MaxHeight);
        Assert.Equal("head.mkv", options.SaveHead);
        Assert.Equal(1, options.HeadMegabytes);
        Assert.True(PlayPlanOptions.TryParse(
            ["Bale", "Test", "--save-head", "head.mkv", "1", "--av1-max-height", "2160", "--file", "file-av1"], out var reordered, out _));
        Assert.Equal(options, reordered);
        var start = PlaybackStartBuilder.Build("automatic-file", "profile", "attempt", null,
            new PlaybackSelection(options.FileId), options.Capabilities(), PlaybackTestSupport.Identity);
        Assert.Equal("file-av1", start.FileId);
    }

    [Theory]
    [InlineData("1080", 1920, 1080)]
    [InlineData("2160", 3840, 2160)]
    public void ExplicitAv1HeightChangesOnlyTheRequestedDecodeBounds(string value, int width, int height)
    {
        Assert.True(PlayPlanOptions.TryParse(["--av1-max-height", value], out var options, out _));
        Assert.Equal(height, options.Av1MaxHeight);
        var capabilities = options.Capabilities();
        Assert.Equal(ConsoleEvidence.Default with { VideoDecode = capabilities.VideoDecode }, capabilities);
        var av1 = capabilities.VideoDecode.Where(codec => codec.Codec == "av1").ToArray();
        Assert.Equal(height == 2160 ? [(1920, 1080, 60.0), (width, height, 30.0)] : new[] { (width, height, 60.0) },
            av1.Select(codec => (codec.MaxWidth ?? 0, codec.MaxHeight ?? 0, codec.MaxFrameRate ?? 0)));
        Assert.All(av1, codec =>
        {
            Assert.Equal(new[] { 8, 10 }, codec.BitDepths);
            Assert.False(codec.Hardware);
        });
        Assert.All(capabilities.VideoDecode.Where(codec => codec.Codec != "av1"), codec =>
        {
            Assert.Equal(1920, codec.MaxWidth);
            Assert.Equal(1080, codec.MaxHeight);
        });
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null,
            PlaybackSelection.Auto, capabilities, PlaybackTestSupport.Identity);
        Assert.Same(capabilities.VideoDecode, start.ClientCapabilities.VideoDecode);
        var baseline = Assert.Single(ConsoleEvidence.Default.VideoDecode, codec => codec.Codec == "av1");
        Assert.Equal(1920, baseline.MaxWidth);
        Assert.Equal(1080, baseline.MaxHeight);
        if (height == 1080) Assert.Same(ConsoleEvidence.Default, capabilities);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("--hold")]
    [InlineData("--av1-max-height")]
    public void RejectsInvalidFileIds(string fileId) => Assert.False(
        PlayPlanOptions.TryParse(["--file", fileId], out _, out _));

    [Theory]
    [InlineData("")]
    [InlineData("720")]
    [InlineData("1081")]
    [InlineData("4320")]
    [InlineData("2160p")]
    [InlineData("02160")]
    [InlineData("2160.0")]
    [InlineData(" 2160")]
    [InlineData("2160 ")]
    [InlineData("+2160")]
    [InlineData("-1080")]
    [InlineData("999999999999999999")]
    [InlineData("--claim-av1")]
    public void RejectsInvalidAv1Heights(string height) => Assert.False(
        PlayPlanOptions.TryParse(["--av1-max-height", height], out _, out _));

    [Fact]
    public void SaveHeadTakesAPathAndABoundedMebibyteCount()
    {
        Assert.True(PlayPlanOptions.TryParse(["--save-head", "/tmp/h.mkv", "200"], out var options, out _));
        Assert.Equal("/tmp/h.mkv", options.SaveHead);
        Assert.Equal(200, options.HeadMegabytes);
        Assert.False(PlayPlanOptions.TryParse(["--save-head", "/tmp/h.mkv", "0"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--save-head", "/tmp/h.mkv", "2049"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--save-head", "--hold", "5"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--save-head", "/tmp/h.mkv"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--save-head", "/a", "1", "--save-head", "/b", "1"], out _, out _));
    }

    [Fact]
    public void SaveRangeTakesAPathAnOffsetAndABoundedByteCount()
    {
        Assert.True(PlayPlanOptions.TryParse(["--save-range", "/tmp/c.bin", "9018564004", "183497"], out var options, out _));
        Assert.Equal("/tmp/c.bin", options.SaveRange);
        Assert.Equal(9018564004L, options.RangeOffset);
        Assert.Equal(183497, options.RangeBytes);
        Assert.False(PlayPlanOptions.TryParse(["--save-range", "/tmp/c.bin", "0", "0"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--save-range", "/tmp/c.bin", "-1", "10"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--save-range", "/tmp/c.bin", "0", "67108865"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--save-range", "/tmp/c.bin", "0"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--save-range", "--hold", "0", "1"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--save-range", "/a", "0", "1", "--save-range", "/b", "0", "1"], out _, out _));
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--file")]
    [InlineData("--av1-max-height")]
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
        Assert.False(PlayPlanOptions.TryParse(["--file", "first", "--file", "second"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--file", "same", "--file", "same"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--av1-max-height", "1080", "--av1-max-height", "2160"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--av1-max-height", "2160", "--av1-max-height", "1080"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--av1-max-height", "1080", "--av1-max-height", "1080"], out _, out _));
        Assert.False(PlayPlanOptions.TryParse(["--av1-max-height", "2160", "--av1-max-height", "2160"], out _, out _));
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
        Assert.Equal(60, av1.MaxFrameRate);
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
    public void DetailListsVersionIdsAndSharedFormatLabelsAndUsageDocumentsTheOptions()
    {
        var program = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CliProgram.cs.txt"));
        Assert.Contains("foreach (var version in detail.Versions)", program);
        Assert.Contains("PlaybackFormatting.VersionCompactLabel(version)", program);
        Assert.Contains("PlaybackFormatting.VersionDetailLabel(version)", program);
        Assert.Contains("Console.WriteLine($\"  version file={version.FileId} {summary}\")", program);
        var usage = program[program.IndexOf("static int Usage()", StringComparison.Ordinal)..];
        Assert.Contains("[--file <fileId>]", usage);
        Assert.Contains("[--av1-max-height <1080|2160>]", usage);
    }

    [Fact]
    public void PlayPlanHidesGrantsAndStopsInFinallyUsingAnIndependentDeadline()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PlayPlanCommand.cs.txt"));
        Assert.Contains("p.Name == \"Bale Test\"", source);
        Assert.Contains("PlaybackStartBuilder.Build(detail, new PlaybackSelection(options.FileId, SubtitleIndex: options.SubtitleIndex), profile.Id", source);
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
