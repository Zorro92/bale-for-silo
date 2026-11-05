using System.Globalization;
using System.Xml.Linq;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackTransportFeedbackTests
{
    [Fact]
    public void EveryPressRestartsTheOfficialFeedbackTimeout()
    {
        var feedback = new PlaybackTransportFeedback();
        Assert.False(feedback.Visible(0));
        feedback.ShowSkip(30, 80, 100);
        Assert.True(feedback.Visible(1299));
        Assert.False(feedback.Visible(1300));
        feedback.ShowSkip(-10, 70, 1299);
        Assert.True(feedback.Visible(2498));
        Assert.False(feedback.Visible(2499));
        Assert.Equal(-10, feedback.DeltaSeconds);
        Assert.Equal(70, feedback.TargetSeconds);
        feedback.Hide();
        Assert.False(feedback.Visible(1300));
    }

    [Theory]
    [InlineData(0, false, "0:00")]
    [InlineData(9.99, false, "0:09")]
    [InlineData(61, false, "1:01")]
    [InlineData(3599, false, "59:59")]
    [InlineData(3600, false, "1:00:00")]
    [InlineData(3671, false, "1:01:11")]
    [InlineData(61, true, "0:01:01")]
    [InlineData(double.NaN, false, "0:00")]
    [InlineData(double.PositiveInfinity, false, "0:00")]
    [InlineData(-1, false, "0:00")]
    public void ClocksMatchScrubberAndSkipIndicatorHourRules(double seconds, bool forceHours, string expected) =>
        Assert.Equal(expected, PlaybackTransportFeedback.Clock(seconds, forceHours));

    [Fact]
    public void RemainingAndSignedDeltaUseTheOfficialMinusAndInvariantDigits()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("−10s", PlaybackTransportFeedback.Delta(-10));
            Assert.Equal("+30s", PlaybackTransportFeedback.Delta(30));
            Assert.Equal("−1:01", PlaybackTransportFeedback.Remaining(20, 81));
            Assert.Equal("−0:00", PlaybackTransportFeedback.Remaining(90, 81));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(50, 100.0, 0.5)]
    [InlineData(150, 100.0, 1)]
    [InlineData(-1, 100.0, 0)]
    [InlineData(50, null, 0)]
    [InlineData(50, 0.0, 0)]
    [InlineData(50, double.PositiveInfinity, 0)]
    public void PlayedTrackIsClampedAndUnknownDurationDoesNotInventProgress(double position, double? duration, double expected) =>
        Assert.Equal(expected, PlaybackTransportFeedback.Progress(position, duration));

    [Fact]
    public void TransportIsPassiveAndDoesNotReplaceBackOrStatsHandling()
    {
        var page = XDocument.Parse(Read("PlayerPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var transport = page.Descendants().Single(element => element.Attribute(x + "Name")?.Value == "TransportFeedback");
        Assert.Equal("False", transport.Attribute("IsHitTestVisible")?.Value);
        Assert.Equal("Collapsed", transport.Attribute("Visibility")?.Value);
        Assert.DoesNotContain(transport.Descendants(), element => element.Name.LocalName is "Button" or "Slider" or "ListView");
        Assert.DoesNotContain(transport.Descendants(), element => element.Attribute("IsTabStop")?.Value == "True");
        var code = Read("PlayerPage.Transport.cs.txt");
        Assert.DoesNotContain(".Focus(", code);
        Assert.Contains("_transportFeedback.Visible(Environment.TickCount64)", code);
        Assert.Contains("now.AddSeconds(wallRemaining).ToString(\"t\")", code);
        var lifecycle = Read("PlayerPage.cs.txt");
        Assert.Contains("UpdateTransport();", lifecycle);
        Assert.Contains("if (!_leaving) _ = StopAndGoBackAsync();", lifecycle);
        Assert.Contains("player.ToggleDebugStats()", Read("App.cs.txt"));
    }

    [Fact]
    public void ThemeUsesTheOfficialRestingGeometryAndPlayerPalette()
    {
        Assert.Equal("48,0,48,86", Setter("SkylinePlayerTransportStyle", "Margin"));
        Assert.Equal("270", Setter("SkylinePlayerTransportScrimStyle", "Height"));
        Assert.Equal("28", Setter("SkylinePlayerTrackHostStyle", "Height"));
        Assert.Equal("4", Setter("SkylinePlayerTrackStyle", "Height"));
        Assert.Equal("2", Setter("SkylinePlayerTrackRailStyle", "CornerRadius"));
        Assert.Equal("#38FFFFFF", Setter("SkylinePlayerTrackRailStyle", "Background"));
        Assert.Equal("#FFEDEDED", Setter("SkylinePlayerTrackPlayedStyle", "Background"));
        Assert.Equal("12.04", Setter("SkylinePlayerTimeStyle", "FontSize"));
        Assert.Equal("#99EDEDED", Setter("SkylinePlayerRemainingStyle", "Foreground"));
        Assert.Equal("40", Setter("SkylinePlayerSkipChipStyle", "Height"));
        Assert.Equal("20", Setter("SkylinePlayerSkipChipStyle", "CornerRadius"));
        Assert.Equal("#6B000000", Setter("SkylinePlayerSkipChipStyle", "Background"));
    }

    private static string? Setter(string key, string property)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var theme = XDocument.Parse(Read("Skyline.xaml"));
        return theme.Root!.Elements().Single(element => element.Attribute(x + "Key")?.Value == key)
            .Elements().Single(element => element.Attribute("Property")?.Value == property).Attribute("Value")?.Value;
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
