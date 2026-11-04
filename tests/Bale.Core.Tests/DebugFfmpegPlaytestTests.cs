using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugFfmpegPlaytestTests
{
    [Theory]
    [InlineData(null, null, 3, "auto")]
    [InlineData("1", "auto", 1, "auto")]
    [InlineData("30", "software", 30, "software")]
    [InlineData("60", "system", 60, "system")]
    public void ParsesBoundedDurationAndExplicitModes(string? seconds, string? mode, int expectedSeconds, string expectedMode)
    {
        Assert.True(DebugFfmpegPlaytest.TryParse("https://example.org/clip", seconds, mode,
            out _, out var duration, out var decoderMode, out var error));
        Assert.Equal(expectedSeconds, duration);
        Assert.Equal(expectedMode, decoderMode);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("0", "auto")]
    [InlineData("61", "auto")]
    [InlineData("-1", "auto")]
    [InlineData("1.5", "auto")]
    [InlineData(" 3", "auto")]
    [InlineData("3", "hardware")]
    [InlineData("3", "Software")]
    [InlineData("3", "")]
    public void RejectsInvalidInputs(string seconds, string mode) => Assert.False(
        DebugFfmpegPlaytest.TryParse("https://example.org/clip", seconds, mode, out _, out _, out _, out _));

    [Theory]
    [InlineData("file:///clip")]
    [InlineData("clip.mp4")]
    [InlineData("https://user:password@example.org/clip")]
    public void SharesTheBuiltInProbesUrlValidation(string url) => Assert.False(
        DebugFfmpegPlaytest.TryParse(url, "3", "auto", out _, out _, out _, out _));

    [Theory]
    [InlineData(0, "FAILS")]
    [InlineData(5, "SLOW")]
    [InlineData(9, "PLAYS - real time")]
    [InlineData(10, "PLAYS - real time")]
    [InlineData(11, "PLAYS - real time")]
    [InlineData(12, "PLAYS - clock faster")]
    public void VerdictMeasuresAdvancementAgainstWallTime(double advance, string verdict)
    {
        var start = TimeSpan.FromSeconds(20);
        Assert.StartsWith("Verdict: " + verdict, DebugFfmpegPlaytest.Verdict(true, start,
            start + TimeSpan.FromSeconds(advance), TimeSpan.FromSeconds(10), []));
    }

    [Fact]
    public void AnOpenOrMediaFailureOverridesAMovingClock()
    {
        var failure = DebugFfmpegPlaytest.Failure("MediaFailed", unchecked((int)0xC00D5212));
        Assert.Equal("MediaFailed: 0xC00D5212", failure);
        Assert.StartsWith("Verdict: FAILS", DebugFfmpegPlaytest.Verdict(false, TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), []));
        Assert.Equal("Verdict: FAILS - MediaFailed: 0xC00D5212\n", DebugFfmpegPlaytest.Verdict(true,
            TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), [failure]));
    }

    [Fact]
    public void MeasurementsUseCpuTimeDeltaInCoresAndInvariantDecimals()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("Position: start=2s end=11.5s wall=10s ratio=0.95\nBuffering entries: 3\nCPU: 2.5 cores\n",
                DebugFfmpegPlaytest.Measurements(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(11.5),
                    TimeSpan.FromSeconds(10), 3, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(125)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void MissingCpuAndZeroWallTimeStayFinite()
    {
        Assert.Equal(0, DebugFfmpegPlaytest.Ratio(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Contains("CPU: unavailable", DebugFfmpegPlaytest.Measurements(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, null, null));
        Assert.Contains("CPU: unavailable", DebugFfmpegPlaytest.Measurements(TimeSpan.Zero, TimeSpan.Zero,
            TimeSpan.FromSeconds(1), 0, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void WinRtProbeIsDebugOnlyMutedBoundedAndNeverFormatsMediaExceptions()
    {
        var source = Read("DebugServer.Ffmpeg.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        string[] required =
        [
            "Interlocked.CompareExchange(ref _playtestBusy, 1, 0)", "409 Conflict",
            "FFmpegInteropLogging.SetLogProvider(null)", "FFmpegMediaSource.CreateFromUriAsync(uri.AbsoluteUri, config).AsTask()",
            "using var source = await creation", "using var player = new MediaPlayer { IsMuted = true, AutoPlay = false }",
            "source.CreateMediaPlaybackItem()", "VideoDecoderMode.Automatic", "VideoDecoderMode.ForceFFmpegSoftwareDecoder",
            "VideoDecoderMode.ForceSystemDecoder", "source.CurrentVideoStream", "source.CurrentAudioStream", "video.BitsPerSample",
            "video.DecoderEngine", "audio.DecoderEngine", "ProcessDiagnosticInfo.GetForCurrentProcess().CpuUsage.GetReport()",
            "report.UserTime + report.KernelTime", "Stopwatch.StartNew()", "Task.Delay(DebugFfmpegPlaytest.CreateTimeoutMs)",
            "Task.Delay(DebugFfmpegPlaytest.OpenTimeoutMs)", "Task.Delay(TimeSpan.FromSeconds(duration))",
            "Interlocked.Increment(ref buffering)", "player.Pause()", "player.Source = null",
            "player.MediaOpened -= OnOpened", "player.MediaFailed -= OnFailed", "player.MediaEnded -= OnEnded",
            "player.BufferingStarted -= OnBuffering", "DisposeLateFfmpegSourceAsync(creation)",
            "Debug FFmpeg: before create", "Debug FFmpeg: before item/open", "Debug FFmpeg: before play", "Debug FFmpeg: done",
        ];
        foreach (var text in required) Assert.Contains(text, source);
        Assert.DoesNotContain("ex.Message", source);
        Assert.DoesNotContain("args.ErrorMessage", source);
        Assert.DoesNotContain("Log.Error(", source);
        Assert.DoesNotContain("Session.Client", source);
        Assert.Contains("case (\"GET\", \"/playtest-ffmpeg\"):", Read("DebugServer.cs.txt"));
    }

    [Fact]
    public void DebugManifestRegistersAllSixteenFactoriesAndPreservesTheStampedApp()
    {
        var original = XDocument.Parse(Read("Package.appxmanifest"));
        var transform = new XslCompiledTransform();
        using (var stylesheet = XmlReader.Create(Path.Combine(AppContext.BaseDirectory, "FFmpegManifest.xslt"))) transform.Load(stylesheet);
        using var output = new StringWriter();
        using (var input = original.CreateReader()) transform.Transform(input, null, output);
        var generated = XDocument.Parse(output.ToString());
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        Assert.True(XNode.DeepEquals(original.Root!.Element(ns + "Identity"), generated.Root!.Element(ns + "Identity")));
        Assert.True(XNode.DeepEquals(original.Root.Element(ns + "Dependencies"), generated.Root.Element(ns + "Dependencies")));
        Assert.Empty(original.Descendants(ns + "ActivatableClass"));
        var extensions = generated.Root.Element(ns + "Extensions");
        Assert.NotNull(extensions);
        Assert.Empty(generated.Descendants(ns + "Application").Elements(ns + "Extensions"));
        var server = Assert.Single(extensions.Descendants(ns + "InProcessServer"));
        Assert.Equal("FFmpegInteropX.dll", server.Element(ns + "Path")!.Value);
        var classes = server.Elements(ns + "ActivatableClass").ToArray();
        Assert.Equal(16, classes.Length);
        Assert.All(classes, c => Assert.Equal("both", c.Attribute("ThreadingModel")!.Value));
        Assert.Contains(classes, c => c.Attribute("ActivatableClassId")!.Value == "FFmpegInteropX.MediaSourceConfig");
        Assert.Contains(classes, c => c.Attribute("ActivatableClassId")!.Value == "FFmpegInteropX.FFmpegMediaSource");
        Assert.Contains(classes, c => c.Attribute("ActivatableClassId")!.Value == "FFmpegInteropX.FFmpegInteropLogging");
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        Assert.True(XNode.DeepEquals(original.Descendants(uap + "VisualElements").Single(), generated.Descendants(uap + "VisualElements").Single()));
    }

    [Fact]
    public void BothPackagesAndTheManifestTransformShareTheDebugServerCondition()
    {
        var project = XDocument.Parse(Read("Bale.Xbox.csproj.txt"));
        var packages = project.Descendants("PackageReference").Where(p => p.Attribute("Include")!.Value.StartsWith("FFmpegInteropX.")).ToArray();
        Assert.Equal(2, packages.Length);
        Assert.All(packages, p => Assert.Equal("'$(BaleDebugServer)' == 'true'", p.Parent!.Attribute("Condition")!.Value));
        Assert.Equal("2.1.0", packages.Single(p => p.Attribute("Include")!.Value.EndsWith(".Lib")).Attribute("Version")!.Value);
        Assert.Equal("8.1.2", packages.Single(p => p.Attribute("Include")!.Value.EndsWith(".FFmpeg")).Attribute("Version")!.Value);
        var target = project.Descendants("Target").Single(t => t.Attribute("Name")!.Value == "PrepareFFmpegManifest");
        Assert.Equal("'$(BaleDebugServer)' == 'true'", target.Attribute("Condition")!.Value);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
