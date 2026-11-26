using System.Globalization;
using System.Xml.Linq;
using Bale.Core.Playback;
using Bale.Core.Settings;
using Bale.Xbox.Services;
using Windows.Storage;

namespace Bale.Core.Tests;

/// <summary>
/// Settings › Playback › SDR Brightness (owner's deviation, 2026-11-24): the SDR white of the console tone map, sent as the
/// fork's bale_tonemap_white_nits (wrapper bale.77). Shares the log and LocalSettings stand-ins with the film grain tests.
/// </summary>
[Collection("Player seek")]
public sealed class SdrBrightnessTests
{
    private const string Key = nameof(AppSettings.SdrWhiteNits);
    private const string WhiteLog = "player: tone map SDR white";
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] Options =
    [
        "SdrBrightnessDefaultOption", "SdrBrightness80Option", "SdrBrightness120Option", "SdrBrightness150Option",
        "SdrBrightness200Option", "SdrBrightness250Option", "SdrBrightness300Option",
    ];

    /// <summary>HDR10 HEVC on a known SDR display: the stage route (bale_tonemap=1).</summary>
    private static PlaybackRouteDecision ToneMap() => new(true, false, "hevc", "eac3", 0, 3840, 2160, ConsoleToneMap: true);

    private static PlaybackRouteDecision Plain(string codec = "hevc", bool software = false) =>
        new(true, software, codec, "eac3", 0, 3840, 2160);

    private static Rav1gRouteDecision Rav1g(bool toneMap) => toneMap
        ? new(true, 200, 1920, 1080, true, new Av1SourceFacts(10, 9, 16, 9, null))
        : new(true, 200, null, null, false, new Av1SourceFacts(10, 1, 1, 1, null));

    private static string? White(FFmpegInteropX.MediaSourceConfig config) =>
        config.FFmpegOptions.GetValueOrDefault("bale_tonemap_white_nits") as string;

    private static string[] WhiteLines(int start) =>
        Log.Messages.Skip(start).Where(line => line.StartsWith(WhiteLog, StringComparison.Ordinal)).ToArray();

    [Fact]
    public void ChoicesAreDefaultThenDimmerToBrighterInsideTheForkRange()
    {
        Assert.Equal(100, SdrBrightness.DefaultNits);
        Assert.Equal(new[] { 100, 80, 120, 150, 200, 250, 300 }, SdrBrightness.Choices);
        Assert.Equal((80, 400), (SdrBrightness.MinimumNits, SdrBrightness.MaximumNits));
        Assert.All(SdrBrightness.Choices, nits => Assert.InRange(nits, SdrBrightness.MinimumNits, SdrBrightness.MaximumNits));
        Assert.Equal(SdrBrightness.Choices.Count, SdrBrightness.Choices.Distinct().Count());
        // The memory-only debug override keeps its own copy of the fork's range (that file never names Settings).
        Assert.Equal(SdrBrightness.MinimumNits, DebugFfmpegInputTuning.MinimumSdrWhiteNits);
        Assert.Equal(SdrBrightness.MaximumNits, DebugFfmpegInputTuning.MaximumSdrWhiteNits);
    }

    [Theory]
    [InlineData(100, "Default (100 nits)")]
    [InlineData(80, "80 nits")]
    [InlineData(120, "120 nits")]
    [InlineData(150, "150 nits")]
    [InlineData(200, "200 nits")]
    [InlineData(250, "250 nits")]
    [InlineData(300, "300 nits")]
    public void LabelsNameDefaultAndEachLevelInAnyCulture(int nits, string label)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "fr-FR", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Assert.Equal(label, SdrBrightness.Label(nits));
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(null, 100)]
    [InlineData(100, 100)]
    [InlineData(80, 80)]
    [InlineData(150, 150)]
    [InlineData(300, 300)]
    [InlineData(175, 100)]
    [InlineData(79, 100)]
    [InlineData(400, 100)]
    [InlineData(0, 100)]
    [InlineData(-150, 100)]
    public void StoredValuesOutsideTheChoicesReadAsDefault(int? stored, int expected) =>
        Assert.Equal(expected, SdrBrightness.FromStored(stored));

    [Fact]
    public void SettingDefaultsToDefaultAndStoresIntNitsInDeviceLocalSettings()
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        var previous = values[Key];
        try
        {
            values.Remove(Key);
            Assert.Equal(SdrBrightness.DefaultNits, AppSettings.SdrWhiteNits);
            foreach (var nits in SdrBrightness.Choices.Where(nits => nits != SdrBrightness.DefaultNits))
            {
                AppSettings.SdrWhiteNits = nits;
                Assert.Equal(nits, Assert.IsType<int>(values[Key]));
                Assert.Equal(nits, AppSettings.SdrWhiteNits);
            }
            // Default stores no value (it keeps following Bale's default); an unknown level is never stored.
            AppSettings.SdrWhiteNits = SdrBrightness.DefaultNits;
            Assert.Null(values[Key]);
            AppSettings.SdrWhiteNits = 175;
            Assert.Null(values[Key]);
            Assert.Equal(SdrBrightness.DefaultNits, AppSettings.SdrWhiteNits);
            // Read the stored value each time, under one device-wide key (no cache, no profile or account scope).
            values[Key] = 250;
            Assert.Equal(250, AppSettings.SdrWhiteNits);
            // Wrong types and unknown levels read as Default, never as another level.
            foreach (var stored in new object[] { "150", 150L, 150.0, (short)150, true, 175, 0, 1000 })
            {
                values[Key] = stored;
                Assert.Equal(SdrBrightness.DefaultNits, AppSettings.SdrWhiteNits);
            }
        }
        finally
        {
            if (previous is null) values.Remove(Key);
            else values[Key] = previous;
        }
    }

    [Theory]
    [InlineData(80)]
    [InlineData(150)]
    [InlineData(300)]
    public void ToneMapMountsSendTheWhiteAndLogItOnce(int nits)
    {
        var start = Log.Messages.Count;
        var config = FfmpegSource.Configuration(ToneMap(), new(), sdrWhiteNits: nits);
        Assert.Equal("1", config.FFmpegOptions["bale_tonemap"]);
        Assert.Equal(nits.ToString(CultureInfo.InvariantCulture), White(config));
        Assert.Equal($"player: tone map SDR white={nits} nits", Assert.Single(WhiteLines(start)));
    }

    [Fact]
    public void DefaultAndWhitesTheForkWouldRefuseLeaveTheKeyUnset()
    {
        var start = Log.Messages.Count;
        foreach (var nits in new[] { SdrBrightness.DefaultNits, 0, 79, 401, 1000, -100 })
        {
            var config = FfmpegSource.Configuration(ToneMap(), new(), sdrWhiteNits: nits);
            Assert.Equal("1", config.FFmpegOptions["bale_tonemap"]);
            Assert.Null(White(config));
        }
        // Both overloads default to Default: unset key, the fork's 100 nits as before.
        Assert.Null(White(FfmpegSource.Configuration(ToneMap())));
        Assert.Null(White(FfmpegSource.Configuration(ToneMap(), new())));
        Assert.Empty(WhiteLines(start));
    }

    [Fact]
    public void Rav1gDisplayPassSdrDownscaleDv7AndHableGetTheWhiteWithTheStageKey()
    {
        // rav1g's display pass reads the same key when the route tone-maps (fork bale.77).
        var rav1g = FfmpegSource.Configuration(Plain("av1", software: true), new(), Rav1g(toneMap: true), sdrWhiteNits: 200);
        Assert.Equal("1", rav1g.FFmpegOptions["bale_rav1g_display"]);
        Assert.Equal("1", rav1g.FFmpegOptions["bale_tonemap"]);
        Assert.Equal("200", White(rav1g));
        // The SDR downscale shares the stage key; its scale-only pass applies no curve, so the white changes nothing there.
        var downscale = FfmpegSource.Configuration(new PlaybackRouteDecision(true, false, "vp9", "aac", 0, 3840, 2160,
            ConsoleDownscale: true), new(), sdrWhiteNits: 200);
        Assert.Equal("200", White(downscale));
        // DV7 on an SDR display tone-maps its HDR10 base layer through the same stage.
        Assert.Equal("200", White(FfmpegSource.Configuration(ToneMap() with { DolbyVision7BaseLayer = true }, new(), sdrWhiteNits: 200)));
        // A debug Hable curve maps to the same white.
        var hable = FfmpegSource.Configuration(ToneMap(), new(Decoder: DebugFfmpegDecoder.Auto, ToneMap: DebugToneMap.Hable),
            sdrWhiteNits: 200);
        Assert.Equal("2", hable.FFmpegOptions["bale_tonemap"]);
        Assert.Equal("200", White(hable));
    }

    [Fact]
    public void MountsWithoutTheToneMapNeverGetTheKey()
    {
        var start = Log.Messages.Count;
        foreach (var config in new[]
        {
            // System-decoded video (SDR, or HDR passed through to an HDR TV).
            FfmpegSource.Configuration(Plain(), new(), sdrWhiteNits: 150),
            // dav1d, and rav1g when its output fits the display without a tone map.
            FfmpegSource.Configuration(Plain("av1", software: true), new(), sdrWhiteNits: 150),
            FfmpegSource.Configuration(Plain("av1", software: true), new(), Rav1g(toneMap: false), sdrWhiteNits: 150),
            // Debug decoder overrides that drop the stage.
            FfmpegSource.Configuration(ToneMap(), new(Decoder: DebugFfmpegDecoder.System), sdrWhiteNits: 150),
            FfmpegSource.Configuration(ToneMap(), new(Decoder: DebugFfmpegDecoder.Software), sdrWhiteNits: 150),
            FfmpegSource.Configuration(Plain(), new(SdrWhiteNits: 250), sdrWhiteNits: 150),
        })
        {
            Assert.DoesNotContain("bale_tonemap", config.FFmpegOptions.Keys);
            Assert.Null(White(config));
        }
        Assert.Empty(WhiteLines(start));
    }

    [Theory]
    [InlineData(150, null, "150")]
    [InlineData(150, 200, "200")]
    [InlineData(100, 250, "250")]
    [InlineData(150, 100, null)]
    [InlineData(150, 80, "80")]
    [InlineData(300, 400, "400")]
    public void DebugWhiteOverrideWinsOverTheSetting(int setting, int? tune, string? expected) =>
        Assert.Equal(expected, White(FfmpegSource.Configuration(ToneMap(), new(SdrWhiteNits: tune), sdrWhiteNits: setting)));

    [Fact]
    public void PlayerCapturesTheSettingOncePerAttemptAndBothBuildsSendIt()
    {
        var player = Read("PlayerPage.cs.txt");
        var attempt = player[player.IndexOf("private sealed class Attempt", StringComparison.Ordinal)..];
        const string capture = "public int SdrWhiteNits { get; } = AppSettings.SdrWhiteNits;";
        Assert.Contains(capture, attempt[..attempt.IndexOf("#if DEBUG_SERVER", StringComparison.Ordinal)]);
        Assert.DoesNotContain("AppSettings.SdrWhiteNits", player.Replace(capture, "", StringComparison.Ordinal));
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("attempt.Dav1dGpuFilmGrain, attempt.SdrWhiteNits);", media);
        Assert.Contains("FfmpegSource.Configuration(media.Route, attempt.Dav1dGpuFilmGrain, attempt.SdrWhiteNits)", media);
        Assert.DoesNotContain("AppSettings.SdrWhiteNits", media);
        Assert.DoesNotContain("bale_tonemap_white_nits", media);
        // Release builds set the key too; only the override is debug-only.
        var production = DebugFfmpegDecoderTests.ProductionSource();
        Assert.Contains("int sdrWhiteNits = SdrBrightness.DefaultNits)", production);
        Assert.Contains("ApplySdrWhite(config, sdrWhiteNits);", production);
        Assert.Contains("config.FFmpegOptions[\"bale_tonemap_white_nits\"] = nits.ToString(CultureInfo.InvariantCulture);", production);
        Assert.Contains("|| !config.FFmpegOptions.ContainsKey(\"bale_tonemap\")) return;", production);
        Assert.DoesNotContain("tuning.SdrWhiteNits", production);
        Assert.Contains("if (tuning.SdrWhiteNits is { } white) sdrWhiteNits = white;", Read("FfmpegSource.cs.txt"));
    }

    [Fact]
    public void PlaybackPaneOffersTheValueRowInItsOwnGroupBeforeExperimental()
    {
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "SettingsPage.xaml"));
        var pane = Named(page, "PlaybackPane");
        var groups = pane.Elements(Ui + "StackPanel").ToArray();
        // The official groups keep their order; Bale's own groups follow them.
        Assert.Equal(new[] { "STREAMING", "CONNECTION", "EPISODES", "HDR ON SDR TVS", "EXPERIMENTAL" },
            groups.Select(candidate => (string?)candidate.Element(Ui + "TextBlock")?.Attribute("Text")));
        var group = groups[3];
        Assert.Equal("{StaticResource SkylineSettingsGroupStyle}", (string?)group.Attribute("Style"));
        Assert.Equal("{StaticResource SkylineSettingsGroupHeaderStyle}", (string?)group.Element(Ui + "TextBlock")!.Attribute("Style"));
        Assert.Contains("deviation from the official Silo apps", Assert.IsType<XComment>(group.PreviousNode).Value);
        var row = Assert.Single(group.Elements(Ui + "Button"));
        Assert.Equal("SdrBrightnessButton", (string?)row.Attribute(Xaml + "Name"));
        Assert.Equal("OnSdrBrightnessClick", (string?)row.Attribute("Click"));
        // The same value row as Lower quality when buffering: the label, then the value on the right.
        var buffering = Named(page, "BufferingQualityButton");
        Assert.Equal((string?)buffering.Attribute("Style"), (string?)row.Attribute("Style"));
        Assert.Equal(buffering.Descendants(Ui + "TextBlock").Select(text => (string?)text.Attribute("Style")),
            row.Descendants(Ui + "TextBlock").Select(text => (string?)text.Attribute("Style")));
        var texts = row.Descendants(Ui + "TextBlock").ToArray();
        Assert.Equal("SDR Brightness", (string?)texts[0].Attribute("Text"));
        Assert.Equal("SdrBrightnessValue", (string?)texts[1].Attribute(Xaml + "Name"));
        Assert.Equal("1", (string?)texts[1].Attribute("Grid.Column"));
        var footer = Assert.Single(group.Elements(Ui + "TextBlock").Skip(1));
        Assert.Equal("How bright HDR video looks when this Xbox tone-maps it for an SDR TV. Higher is brighter; Default matches "
            + "the reference white of 100 nits.", (string?)footer.Attribute("Text"));
        Assert.Equal("{StaticResource SkylineSettingsFooterStyle}", (string?)footer.Attribute("Style"));
    }

    [Fact]
    public void PickerIsTheBufferingSheetWithOneOptionPerChoiceInOrder()
    {
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "SettingsPage.xaml"));
        var picker = Named(page, "SdrBrightnessPicker");
        var buffering = Named(page, "BufferingPicker");
        Assert.Equal("Collapsed", (string?)picker.Attribute("Visibility"));
        Assert.Equal((string?)buffering.Attribute("Background"), (string?)picker.Attribute("Background"));
        // The same card, header ("Choose an option", "Back to close"), divider and option list as the buffering sheet.
        static string[] Shape(XElement sheet) => sheet.Descendants()
            .Where(element => !element.AncestorsAndSelf(Ui + "Button").Any())
            .Select(element => element.Name.LocalName + " " + (string?)element.Attribute("Style")).ToArray();
        Assert.Equal(Shape(buffering), Shape(picker));
        var title = picker.Descendants(Ui + "TextBlock").Select(text => (string?)text.Attribute("Text")).ToArray();
        Assert.Equal(new[] { "SDR Brightness", "Choose an option", "Back to close" }, title.Where(text => text is not null).Take(3));
        var options = picker.Descendants(Ui + "Button").ToArray();
        Assert.Equal(Options, options.Select(option => (string?)option.Attribute(Xaml + "Name")));
        Assert.Equal(SdrBrightness.Choices.Select(nits => nits.ToString(CultureInfo.InvariantCulture)),
            options.Select(option => (string?)option.Attribute("Tag")));
        foreach (var option in options)
        {
            Assert.Equal("{StaticResource SkylineSettingsPickerOptionStyle}", (string?)option.Attribute("Style"));
            Assert.Equal("OnSdrBrightnessOptionClick", (string?)option.Attribute("Click"));
            var label = Assert.Single(option.Elements());
            Assert.Equal(Ui + "TextBlock", label.Name);
            Assert.Equal((string?)option.Attribute(Xaml + "Name") + "Text", (string?)label.Attribute(Xaml + "Name"));
            Assert.Equal("{StaticResource SkylineSettingsRowTextStyle}", (string?)label.Attribute("Style"));
        }
        // Over the page and under the sign-out dialog, like the other sheets.
        var layers = Named(page, "Root").Elements().Select(element => (string?)element.Attribute(Xaml + "Name")).ToList();
        Assert.True(layers.IndexOf("BufferingPicker") < layers.IndexOf("SdrBrightnessPicker"));
        Assert.True(layers.IndexOf("SdrBrightnessPicker") < layers.IndexOf("SignOutPrompt"));
    }

    [Fact]
    public void CodeLabelsTheOptionsAndKeepsFocusInsideThePickerAndThePaneColumn()
    {
        var code = Read("SettingsPage.cs.txt");
        // One label source for the row's value and every option.
        Assert.Contains("private void UpdateSdrBrightness() => SdrBrightnessValue.Text = SdrBrightness.Label(AppSettings.SdrWhiteNits);", code);
        Assert.Contains("label.Text = SdrBrightness.Label(SdrBrightnessNits(option));", code);
        foreach (var name in Options) Assert.Contains($"({name}, {name}Text)", code);
        Assert.Contains("UpdateDav1dGpuFilmGrain();\n        UpdateSdrBrightness();\n        UpdateConnection();", code);
        // The row: Up/Down walk the pane's rows only and stop at its ends (never sideways); Left goes back to the rail.
        var actions = code[code.IndexOf("private Control[] DetailActions", StringComparison.Ordinal)..
            code.IndexOf("private SettingsCategory CategoryOf", StringComparison.Ordinal)];
        Assert.Contains("AutoPlayButton, AutoPlayProfileButton,\n                NextUpButton, IntroSkipButton, SkipCreditsButton, SkipCreditsProfileButton, SdrBrightnessButton, Dav1dGpuFilmGrainButton,", actions);
        Assert.Contains("FocusTarget(actions[Math.Clamp(index + vertical, 0, actions.Length - 1)]);", code);
        Assert.Contains("_returnTarget = SdrBrightnessButton;", code);
        // The sheet: the checked option takes focus; Up/Down stop at its ends; Left/Right/Tab do nothing; focus can't leave it.
        var keys = code[code.IndexOf("if (SdrBrightnessPicker.Visibility == Visibility.Visible)\n        {\n            if (vertical != 0)",
            StringComparison.Ordinal)..];
        keys = keys[..keys.IndexOf("return;", StringComparison.Ordinal)];
        Assert.Contains("var options = SdrBrightnessOptions;", keys);
        Assert.Contains("options[Math.Clamp(index + vertical, 0, options.Length - 1)].Focus(FocusState.Keyboard);", keys);
        Assert.Contains("if (horizontal != 0 || e.Key == VirtualKey.Tab) e.Handled = true;", keys);
        Assert.Contains("if (!e.TrySetNewFocusedElement(SelectedSdrBrightnessOption)) e.Cancel = true;", code);
        Assert.Contains("SdrBrightnessPicker.Visibility != Visibility.Visible || selected.Focus(FocusState.Programmatic));", code);
        // Back or a choice closes it and returns to the row; leaving the page hides it.
        Assert.Contains("if (SdrBrightnessPicker.Visibility == Visibility.Visible)\n        {\n            CloseSdrBrightnessPicker();\n            return true;",
            code);
        Assert.Contains("SdrBrightnessPicker.Visibility = Visibility.Collapsed;\n        FocusTarget(SdrBrightnessButton);", code);
        Assert.Contains("SdrBrightnessPicker.Visibility = Visibility.Collapsed;\n        SignOutPrompt.Hide();", code);
        // A choice is stored on this Xbox, a failed write says so, as the film grain row does.
        Assert.Contains("AppSettings.SdrWhiteNits = SdrBrightnessNits(sender);", code);
        Assert.Contains("PlaybackError.Text = \"Bale couldn’t save the SDR brightness preference on this Xbox. Try again.\";", code);
    }

    private static XElement Named(XDocument page, string name) =>
        page.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
