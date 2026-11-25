using System.Xml.Linq;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Storage;

namespace Bale.Core.Tests;

[Collection("Player seek")]
public sealed class Dav1dGpuFilmGrainTests
{
    private static PlaybackRouteDecision Route(string? codec = "av1", bool software = true) =>
        new(true, software, codec, "aac", 0, 1920, 1080);

    private static Rav1gRouteDecision? Decision(bool rav1g) => rav1g
        ? new(true, 250, null, null, false, new(null, null, null, null, null)) : null;

    [Fact]
    public void SettingDefaultsOffAndPersistsInDeviceLocalSettings()
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        const string key = nameof(AppSettings.Dav1dGpuFilmGrain);
        var previous = values[key];
        try
        {
            values.Remove(key);
            Assert.False(AppSettings.Dav1dGpuFilmGrain);
            foreach (var enabled in new[] { true, false })
            {
                AppSettings.Dav1dGpuFilmGrain = enabled;
                Assert.Equal(enabled, values[key]);
                Assert.Equal(enabled, AppSettings.Dav1dGpuFilmGrain);
            }
            // Read the stored value, rather than a cached flag or a profile/account-scoped key.
            values[key] = true;
            Assert.True(AppSettings.Dav1dGpuFilmGrain);
        }
        finally
        {
            if (previous is null) values.Remove(key);
            else values[key] = previous;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void D3d11UsesCapturedSettingWithoutRegisteringZeroCopy(bool enabled, bool rav1g)
    {
        var registrations = ZeroCopyRuntime.Registrations;
        var logStart = Log.Messages.Count;
        var config = FfmpegSource.Configuration(Route(), new(), Decision(rav1g), dav1dGpuFilmGrain: enabled);
        Assert.Equal(enabled ? "3" : "0", config.FFmpegOptions["bale_film_grain_mode"]);
        AssertNoZeroCopy(config);
        Assert.Equal(registrations, ZeroCopyRuntime.Registrations);
        Assert.DoesNotContain("bale_dav1d_export_film_grain", config.FFmpegOptions.Keys);
        if (rav1g)
        {
            Assert.Equal("1", config.FFmpegOptions["bale_av1_decoder"]);
            Assert.Equal("1080", config.FFmpegOptions["bale_av1_fallback_max_height"]);
        }
        var log = Assert.Single(Log.Messages.Skip(logStart), line => line.StartsWith("player: dav1d grain request=", StringComparison.Ordinal));
        Assert.Equal(enabled ? "player: dav1d grain request=gpu-d3d11 (setting=on)"
            : "player: dav1d grain request=cpu (setting=off)", log);
    }

    [Theory]
    [InlineData(false, "dav1d", "0", false, "cpu")]
    [InlineData(true, "dav1d", "0", false, "cpu")]
    [InlineData(false, "on", "0", false, "cpu")]
    [InlineData(true, "on", "0", false, "cpu")]
    [InlineData(false, "off", "0", true, "off")]
    [InlineData(true, "off", "3", true, "off")]
    [InlineData(false, "gpu", "2", false, "gpu-zerocopy")]
    [InlineData(true, "gpu", "2", false, "gpu-zerocopy")]
    [InlineData(false, "default", "0", false, "cpu")]
    [InlineData(true, "default", "3", false, "gpu-d3d11")]
    public void DebugGrainOverrideWinsForDav1dAndInOpenRav1gFallback(bool enabled, string grain,
        string expectedMode, bool exportOnly, string request)
    {
        Assert.True(DebugFfmpegPlaytest.TryParseGrain(grain, out var selection, out _));
        foreach (var rav1g in new[] { false, true })
        {
            var logStart = Log.Messages.Count;
            var config = FfmpegSource.Configuration(Route(), new(FilmGrain: selection), Decision(rav1g), dav1dGpuFilmGrain: enabled);
            Assert.Equal(expectedMode, config.FFmpegOptions["bale_film_grain_mode"]);
            Assert.Equal(exportOnly, config.FFmpegOptions.ContainsKey("bale_dav1d_export_film_grain"));
            if (exportOnly) Assert.Equal("1", config.FFmpegOptions["bale_dav1d_export_film_grain"]);
            AssertNoZeroCopy(config);
            Assert.Equal($"player: dav1d grain request={request} (setting={(enabled ? "on" : "off")})",
                Assert.Single(Log.Messages.Skip(logStart)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DebugZeroCopyKeepsModeTwoAndFailedRegistrationKeepsTheSetting(bool rav1g)
    {
        var previous = ZeroCopyRuntime.Available;
        try
        {
            ZeroCopyRuntime.Available = true;
            var config = FfmpegSource.Configuration(Route(), new(), Decision(rav1g), "zerocopy", dav1dGpuFilmGrain: true);
            Assert.Equal("2", config.FFmpegOptions["bale_film_grain_mode"]);
            Assert.DoesNotContain("bale_zero_copy", config.FFmpegOptions.Keys);
            Assert.Equal("6", config.FFmpegOptions["bale_d3d11_upload"]);
            ZeroCopyRuntime.Available = false;
            var fallback = FfmpegSource.Configuration(Route(), new(), Decision(rav1g), "zerocopy", dav1dGpuFilmGrain: true);
            Assert.Equal("3", fallback.FFmpegOptions["bale_film_grain_mode"]);
            AssertNoZeroCopy(fallback);
        }
        finally { ZeroCopyRuntime.Available = previous; }
    }

    [Fact]
    public void SettingDoesNotAffectOtherCodecsOrSystemVideo()
    {
        foreach (var route in new[] { Route("mpeg4"), Route("hevc", false), Route(null, false) })
        {
            var logStart = Log.Messages.Count;
            var config = FfmpegSource.Configuration(route, new(), dav1dGpuFilmGrain: true);
            Assert.DoesNotContain("bale_film_grain_mode", config.FFmpegOptions.Keys);
            AssertNoZeroCopy(config);
            Assert.Empty(Log.Messages.Skip(logStart));
        }
        var system = FfmpegSource.Configuration(Route(), new(Decoder: DebugFfmpegDecoder.System), dav1dGpuFilmGrain: true);
        Assert.DoesNotContain("bale_film_grain_mode", system.FFmpegOptions.Keys);
        AssertNoZeroCopy(system);
    }

    [Fact]
    public void PlayerCapturesSettingOncePerAttemptInDebugAndProduction()
    {
        var player = Read("PlayerPage.cs.txt");
        var attempt = player[player.IndexOf("private sealed class Attempt", StringComparison.Ordinal)..];
        const string capture = "public bool Dav1dGpuFilmGrain { get; } = AppSettings.Dav1dGpuFilmGrain;";
        Assert.Contains(capture, attempt[..attempt.IndexOf("#if DEBUG_SERVER", StringComparison.Ordinal)]);
        Assert.DoesNotContain("AppSettings.Dav1dGpuFilmGrain", player.Replace(capture, "", StringComparison.Ordinal));
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("FfmpegSource.Configuration(media.Route, attempt.FfmpegTuning, media.Rav1g, attempt.Dav1dPath,\n                    attempt.Dav1dGpuFilmGrain, attempt.SdrWhiteNits)", media);
        Assert.Contains("FfmpegSource.Configuration(media.Route, attempt.Dav1dGpuFilmGrain, attempt.SdrWhiteNits)", media);
        Assert.DoesNotContain("AppSettings.Dav1dGpuFilmGrain", media);
        Assert.DoesNotContain("AppSettings", Read("FfmpegSource.cs.txt"));
        var production = DebugFfmpegDecoderTests.ProductionSource();
        Assert.Contains("Configuration(PlaybackRouteDecision route, bool dav1dGpuFilmGrain = false,\n        int sdrWhiteNits = SdrBrightness.DefaultNits)", production);
        Assert.Contains("ApplySoftwareVideo(config, dav1dGpuFilmGrain);", production);
    }

    [Fact]
    public void PlaybackEndsWithExperimentalRowUsingTheExistingToggleAndFocusPattern()
    {
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "SettingsPage.xaml"));
        var pane = page.Descendants().Single(element => (string?)element.Attribute(xaml + "Name") == "PlaybackPane");
        var groups = pane.Elements(ui + "StackPanel").ToArray();
        Assert.Equal("EPISODES", (string?)groups[^3].Element(ui + "TextBlock")?.Attribute("Text"));
        Assert.Equal("HDR ON SDR TVS", (string?)groups[^2].Element(ui + "TextBlock")?.Attribute("Text"));
        var experimental = groups[^1];
        var header = experimental.Element(ui + "TextBlock")!;
        Assert.Equal("EXPERIMENTAL", (string?)header.Attribute("Text"));
        Assert.Equal("{StaticResource SkylineSettingsGroupHeaderStyle}", (string?)header.Attribute("Style"));
        var row = Assert.Single(experimental.Elements(ui + "Button"));
        var autoPlay = pane.Descendants(ui + "Button").Single(element => (string?)element.Attribute(xaml + "Name") == "AutoPlayButton");
        Assert.Equal((string?)autoPlay.Attribute("Style"), (string?)row.Attribute("Style"));
        Assert.Equal("Dav1dGpuFilmGrainButton", (string?)row.Attribute(xaml + "Name"));
        Assert.Equal("OnDav1dGpuFilmGrainClick", (string?)row.Attribute("Click"));
        Assert.Equal("GPU film grain (AV1 fallback)", (string?)row.Descendants(ui + "TextBlock").First().Attribute("Text"));
        Assert.Equal(autoPlay.Descendants(ui + "TextBlock").Select(element => (string?)element.Attribute("Style")),
            row.Descendants(ui + "TextBlock").Select(element => (string?)element.Attribute("Style")));
        var description = experimental.Elements(ui + "TextBlock").Last();
        Assert.Equal("Applies AV1 film grain on the GPU when the software decoder plays. May help slower consoles.", (string?)description.Attribute("Text"));
        Assert.Equal("{StaticResource SkylineSettingsFooterStyle}", (string?)description.Attribute("Style"));
        Assert.Contains(pane.Nodes().OfType<XComment>(), comment => comment.Value.Contains("deviation from the official Silo apps", StringComparison.Ordinal));
        var code = Read("SettingsPage.cs.txt");
        var actions = code[code.IndexOf("private Control[] DetailActions", StringComparison.Ordinal)..code.IndexOf("private SettingsCategory CategoryOf", StringComparison.Ordinal)];
        Assert.Contains("AutoPlayButton, AutoPlayProfileButton,\n                SdrBrightnessButton, Dav1dGpuFilmGrainButton,", actions);
        Assert.Contains("FocusTarget(actions[Math.Clamp(index + vertical, 0, actions.Length - 1)]);", code);
        Assert.Contains("_returnTarget = Dav1dGpuFilmGrainButton;", code);
        Assert.Contains("AppSettings.Dav1dGpuFilmGrain = !AppSettings.Dav1dGpuFilmGrain;", code);
        Assert.Contains("Dav1dGpuFilmGrainValue.Text = AppSettings.Dav1dGpuFilmGrain ? \"On\" : \"Off\";", code);
    }

    private static void AssertNoZeroCopy(FFmpegInteropX.MediaSourceConfig config)
    {
        Assert.DoesNotContain("bale_zero_copy", config.FFmpegOptions.Keys);
        Assert.DoesNotContain("bale_d3d11_upload", config.FFmpegOptions.Keys);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
