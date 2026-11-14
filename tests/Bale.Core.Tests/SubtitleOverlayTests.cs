using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class SubtitleOverlayTests
{
    [Fact]
    public void UnknownVideoSizeUsesTheWholePanelInPhysicalPixels()
    {
        // Xbox lays out at 960×540 effective pixels; a 4K output has composition scale 4.
        var layout = SubtitleOverlayLayout.Compute(960, 540, 4, 4, 0, 0);
        Assert.Equal(new SubtitleOverlayLayout(3840, 2160, 4, 4, 0, 0, 3840, 2160), layout);
        Assert.Equal(layout, SubtitleOverlayLayout.Compute(960, 540, 4, 4, 1920, 0));
    }

    [Fact]
    public void SameAspectVideoFillsThePanel()
    {
        var layout = SubtitleOverlayLayout.Compute(960, 540, 2, 2, 3840, 2160);
        Assert.Equal(new SubtitleOverlayLayout(1920, 1080, 2, 2, 0, 0, 1920, 1080), layout);
    }

    [Fact]
    public void WideVideoIsLetterboxed()
    {
        var layout = SubtitleOverlayLayout.Compute(960, 540, 2, 2, 1920, 800);
        Assert.Equal(0, layout.VideoX);
        Assert.Equal(140, layout.VideoY);
        Assert.Equal(1920, layout.VideoWidth);
        Assert.Equal(800, layout.VideoHeight);
    }

    [Fact]
    public void NarrowVideoIsPillarboxed()
    {
        var layout = SubtitleOverlayLayout.Compute(960, 540, 2, 2, 1440, 1080);
        Assert.Equal(240, layout.VideoX);
        Assert.Equal(0, layout.VideoY);
        Assert.Equal(1440, layout.VideoWidth);
        Assert.Equal(1080, layout.VideoHeight);
    }

    [Fact]
    public void SmallVideoIsScaledUpAndAnisotropicScaleIsKept()
    {
        var layout = SubtitleOverlayLayout.Compute(960, 540, 2, 1.5, 640, 480);
        Assert.Equal(1920u, layout.Width);
        Assert.Equal(810u, layout.Height);
        Assert.Equal(2f, layout.ScaleX);
        Assert.Equal(1.5f, layout.ScaleY);
        Assert.Equal(1080, layout.VideoWidth);
        Assert.Equal(810, layout.VideoHeight);
        Assert.Equal(420, layout.VideoX);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(double.NaN, 540, 0, -1)]
    [InlineData(-5, double.PositiveInfinity, double.NaN, double.PositiveInfinity)]
    public void UnlaidOutOrInvalidPanelsStillGetAOnePixelSwapChain(double width, double height, double scaleX, double scaleY)
    {
        var layout = SubtitleOverlayLayout.Compute(width, height, scaleX, scaleY, 1920, 1080);
        Assert.True(layout.Width >= 1 && layout.Height >= 1);
        Assert.True(float.IsFinite(layout.ScaleX) && layout.ScaleX > 0);
        Assert.True(float.IsFinite(layout.ScaleY) && layout.ScaleY > 0);
        Assert.True(float.IsFinite(layout.VideoWidth) && float.IsFinite(layout.VideoHeight));
    }

    [Fact]
    public void TheFirstClockIsAlwaysPushed()
    {
        var clock = new SubtitleClockPush();
        Assert.True(clock.ShouldPush(playing: false, 1, now: 0));
        Assert.False(clock.ShouldPush(playing: false, 1, now: 50));
    }

    [Fact]
    public void PlayingCorrectsDriftOncePerSecondNotPerTick()
    {
        var clock = new SubtitleClockPush();
        Assert.True(clock.ShouldPush(true, 1, 0));
        for (var now = 100; now < 1000; now += 100) Assert.False(clock.ShouldPush(true, 1, now));
        Assert.True(clock.ShouldPush(true, 1, 1000));
        Assert.False(clock.ShouldPush(true, 1, 1900));
        Assert.True(clock.ShouldPush(true, 1, 2000));
    }

    [Fact]
    public void APausedClockIsNotRepushed()
    {
        var clock = new SubtitleClockPush();
        Assert.True(clock.ShouldPush(false, 1, 0));
        Assert.False(clock.ShouldPush(false, 1, 60_000));
    }

    [Fact]
    public void StateRateSeekAndResetPushImmediately()
    {
        var clock = new SubtitleClockPush();
        Assert.True(clock.ShouldPush(true, 1, 0));
        Assert.True(clock.ShouldPush(false, 1, 10));
        Assert.True(clock.ShouldPush(true, 1, 20));
        Assert.True(clock.ShouldPush(true, 1.5, 30));
        Assert.False(clock.ShouldPush(true, 1.5, 40));
        Assert.True(clock.ShouldPush(true, 1.5, 50, force: true));
        // A forced push restarts the drift interval.
        Assert.False(clock.ShouldPush(true, 1.5, 1040));
        Assert.True(clock.ShouldPush(true, 1.5, 1050));
        clock.Reset();
        Assert.True(clock.ShouldPush(true, 1.5, 1060));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1.2344, 1234)]
    [InlineData(5025.0006, 5025001)]
    [InlineData(-3, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void SourceSecondsBecomeWholeMilliseconds(double seconds, long expected) =>
        Assert.Equal(expected, SubtitleClockPush.Milliseconds(seconds));

    [Theory]
    [InlineData(1.25, 1.25)]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(double.NaN, 1)]
    public void UnusableRatesFallBackToNormalSpeed(double rate, double expected) =>
        Assert.Equal(expected, SubtitleClockPush.Rate(rate));

    [Fact]
    public void ThePanelSitsDirectlyAboveTheVideoAndNeverTakesInput()
    {
        var xaml = Read("PlayerPage.xaml").ReplaceLineEndings("\n");
        var video = xaml.IndexOf("<Border x:Name=\"VideoHost\"", StringComparison.Ordinal);
        var layer = xaml.IndexOf("<SwapChainPanel x:Name=\"SubtitleLayer\" IsHitTestVisible=\"False\" />", StringComparison.Ordinal);
        var feedback = xaml.IndexOf("x:Name=\"TransportFeedback\"", StringComparison.Ordinal);
        Assert.True(video >= 0 && layer > video && feedback > layer);
    }

    [Fact]
    public void TheDebugEndpointIsDebugOnlyAndNeverPrintsExceptionText()
    {
        var source = Read("DebugServer.SubtitleOverlay.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.DoesNotContain("ex.Message", source);
        Assert.DoesNotContain("AbsoluteUri", source);
        var server = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"GET\", \"/suboverlay\"):", server);
        Assert.Contains("case (\"POST\", \"/suboverlay\"):", server);
        Assert.Contains("scripts/bale-debug.sh suboverlay [stats|load <url> [font-url...]|clear]", Read("bale-debug.sh"));
        // The wrapper ships in every build and reports HRESULTs only.
        var overlay = Read("SubtitleOverlay.cs.txt");
        Assert.DoesNotContain("#if DEBUG_SERVER", overlay);
        Assert.DoesNotContain("ex.Message", overlay);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
