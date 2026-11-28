using System.Globalization;
using System.Xml.Linq;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

/// <summary>
/// The dav1d path applies grain on the GPU and has no setting (owner, 2026-11-27: GPU grain only). Debug grain
/// overrides and the zero-copy switch are the only other requests.
/// </summary>
[Collection("Player seek")]
public sealed class Dav1dGpuGrainTests
{
    private static PlaybackRouteDecision Route(string? codec = "av1", bool software = true) =>
        new(true, software, codec, "aac", 0, 1920, 1080);

    private static Rav1gRouteDecision? Decision(bool rav1g) => rav1g
        ? new(true, 250, null, null, false, new(null, null, null, null, null)) : null;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SoftwareAv1AlwaysRequestsGpuGrainWithoutZeroCopy(bool rav1g)
    {
        var registrations = ZeroCopyRuntime.Registrations;
        var logStart = Log.Messages.Count;
        var config = FfmpegSource.Configuration(Route(), new(), Decision(rav1g));
        Assert.Equal("3", config.FFmpegOptions["bale_film_grain_mode"]);
        AssertNoZeroCopy(config);
        Assert.Equal(registrations, ZeroCopyRuntime.Registrations);
        Assert.DoesNotContain("bale_dav1d_export_film_grain", config.FFmpegOptions.Keys);
        if (rav1g)
        {
            Assert.Equal("1", config.FFmpegOptions["bale_av1_decoder"]);
            Assert.Equal("1080", config.FFmpegOptions["bale_av1_fallback_max_height"]);
        }
        Assert.Equal("player: dav1d grain request=gpu-d3d11", Assert.Single(Log.Messages.Skip(logStart),
            line => line.StartsWith("player: dav1d grain request=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("dav1d", "0", false, "cpu")]
    [InlineData("on", "0", false, "cpu")]
    [InlineData("off", "3", true, "off")]
    [InlineData("gpu", "2", false, "gpu-zerocopy")]
    [InlineData("d3d11", "3", false, "gpu-d3d11")]
    [InlineData("default", "3", false, "gpu-d3d11")]
    public void DebugGrainOverrideWinsForDav1dAndInOpenRav1gFallback(string grain, string expectedMode,
        bool exportOnly, string request)
    {
        Assert.True(DebugFfmpegPlaytest.TryParseGrain(grain, out var selection, out _));
        foreach (var rav1g in new[] { false, true })
        {
            var logStart = Log.Messages.Count;
            var config = FfmpegSource.Configuration(Route(), new(FilmGrain: selection), Decision(rav1g));
            Assert.Equal(expectedMode, config.FFmpegOptions["bale_film_grain_mode"]);
            Assert.Equal(exportOnly, config.FFmpegOptions.ContainsKey("bale_dav1d_export_film_grain"));
            if (exportOnly) Assert.Equal("1", config.FFmpegOptions["bale_dav1d_export_film_grain"]);
            AssertNoZeroCopy(config);
            Assert.Equal($"player: dav1d grain request={request}", Assert.Single(Log.Messages.Skip(logStart)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DebugZeroCopyKeepsModeTwoAndFailedRegistrationFallsBackToGpuGrain(bool rav1g)
    {
        var previous = ZeroCopyRuntime.Available;
        try
        {
            ZeroCopyRuntime.Available = true;
            var config = FfmpegSource.Configuration(Route(), new(), Decision(rav1g), "zerocopy");
            Assert.Equal("2", config.FFmpegOptions["bale_film_grain_mode"]);
            Assert.DoesNotContain("bale_zero_copy", config.FFmpegOptions.Keys);
            Assert.Equal("6", config.FFmpegOptions["bale_d3d11_upload"]);
            ZeroCopyRuntime.Available = false;
            var fallback = FfmpegSource.Configuration(Route(), new(), Decision(rav1g), "zerocopy");
            Assert.Equal("3", fallback.FFmpegOptions["bale_film_grain_mode"]);
            AssertNoZeroCopy(fallback);
        }
        finally { ZeroCopyRuntime.Available = previous; }
    }

    [Fact]
    public void OtherCodecsAndSystemVideoGetNoAv1GrainKeys()
    {
        foreach (var route in new[] { Route("mpeg4"), Route("hevc", false), Route(null, false) })
        {
            var logStart = Log.Messages.Count;
            var config = FfmpegSource.Configuration(route, new());
            Assert.DoesNotContain("bale_film_grain_mode", config.FFmpegOptions.Keys);
            AssertNoZeroCopy(config);
            Assert.Empty(Log.Messages.Skip(logStart));
        }
        var system = FfmpegSource.Configuration(Route(), new(Decoder: DebugFfmpegDecoder.System));
        Assert.DoesNotContain("bale_film_grain_mode", system.FFmpegOptions.Keys);
        AssertNoZeroCopy(system);
    }

    [Fact]
    public void NoGrainSettingIsLeftInTheAppOrItsPlumbing()
    {
        // The stored LocalSettings value is never read, so no property or per-attempt capture remains.
        Assert.Null(typeof(AppSettings).GetProperty("Dav1dGpuFilmGrain"));
        Assert.DoesNotContain("GpuFilmGrain", Read("PlayerPage.cs.txt"));
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("FfmpegSource.Configuration(media.Route, attempt.FfmpegTuning, media.Rav1g, attempt.Dav1dPath,\n                    attempt.SdrWhiteNits)", media);
        Assert.Contains("FfmpegSource.Configuration(media.Route, media.Rav1g, attempt.SdrWhiteNits)", media);
        Assert.DoesNotContain("GpuFilmGrain", media);
        var production = DebugFfmpegDecoderTests.ProductionSource();
        Assert.Contains("Configuration(PlaybackRouteDecision route, Rav1gRouteDecision? rav1g = null,\n        int sdrWhiteNits = SdrBrightness.DefaultNits)", production);
        Assert.Contains("ApplySoftwareVideo(config);", production);
        Assert.Contains("config.FFmpegOptions[\"bale_film_grain_mode\"] = SoftwareDecodeTuning.FilmGrainMode.ToString(CultureInfo.InvariantCulture);", production);
        Assert.DoesNotContain("GpuFilmGrain", production);
        Assert.DoesNotContain("(setting=", production);
    }

    [Fact]
    public void SettingsPlaybackPaneHasNoExperimentalGroup()
    {
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "SettingsPage.xaml"));
        var pane = page.Descendants().Single(element => (string?)element.Attribute(xaml + "Name") == "PlaybackPane");
        var headers = pane.Elements(ui + "StackPanel")
            .Select(group => (string?)group.Element(ui + "TextBlock")?.Attribute("Text")).ToArray();
        Assert.Equal(new[] { "STREAMING", "CONNECTION", "EPISODES", "HDR ON SDR TVS" }, headers);
        Assert.DoesNotContain(page.Descendants(), element => (string?)element.Attribute("Text") == "EXPERIMENTAL"
            || (string?)element.Attribute(xaml + "Name") is "Dav1dGpuFilmGrainButton" or "Dav1dGpuFilmGrainValue");
        var code = Read("SettingsPage.cs.txt");
        Assert.DoesNotContain("Dav1dGpuFilmGrain", code);
        Assert.Contains("SkipCreditsProfileButton, SdrBrightnessButton,\n", code);
    }

    private static void AssertNoZeroCopy(FFmpegInteropX.MediaSourceConfig config)
    {
        Assert.DoesNotContain("bale_zero_copy", config.FFmpegOptions.Keys);
        Assert.DoesNotContain("bale_d3d11_upload", config.FFmpegOptions.Keys);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
