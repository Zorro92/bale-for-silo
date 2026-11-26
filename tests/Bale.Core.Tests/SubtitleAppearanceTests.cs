using Bale.Core.Settings;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Bale.Core.Tests;

/// <summary>
/// playback.subtitle_appearance (contract schema subtitle-appearance.json), the Appearance rows' choices
/// (TvSubtitleAppearanceOptions.kt), the pickers (TvSettingsScreen.kt SubtitlePicker), the text style bale_gpu draws
/// converted SubRip/WebVTT with, and Use Device Settings (DeviceCaptioningAppearance.kt).
/// </summary>
public class SubtitleAppearanceTests
{
    private static UntypedObject Object(params (string Key, UntypedNode Value)[] fields) =>
        new(fields.ToDictionary(field => field.Key, field => field.Value));

    [Fact]
    public void TheDefaultIsTheManifestsBoxAtSeventyFivePercent()
    {
        // Manifest default_value (revision 6: a 75% opaque box, not Android's local shadow fallback).
        Assert.Equal(new SubtitleAppearance(SubtitleFontSize.Large, "sans-serif", "#ffffff", 100, "#000000", SubtitleBackgroundStyle.Box, 75,
            false, "#000000", SubtitlePosition.Bottom), SubtitleAppearance.Default);
        Assert.Equal("playback.subtitle_appearance", SubtitleAppearance.Key);
    }

    [Fact]
    public void AStoredObjectIsASparseOverrideAndABadFieldKeepsOnlyItsOwnDefault()
    {
        var appearance = SubtitleAppearance.FromWire(Object(
            ("fontSize", new UntypedString("xxlarge")), ("fontFamily", new UntypedString("Helvetica Neue")),
            ("fontColor", new UntypedString("#FF0000")), ("textOpacity", new UntypedInteger(0)), ("backgroundColor", new UntypedString("red")),
            ("backgroundStyle", new UntypedString("outline")), ("backgroundOpacity", new UntypedInteger(140)), ("textOutline", new UntypedBoolean(true)),
            ("textOutlineColor", new UntypedString("#1E3A5F")), ("position", new UntypedString("lower-third")), ("extra", new UntypedString("x"))));
        Assert.Equal(new SubtitleAppearance(SubtitleFontSize.XXLarge, "Helvetica Neue", "#ff0000", 1, "#000000", SubtitleBackgroundStyle.Outline,
            100, true, "#1e3a5f", SubtitlePosition.LowerThird), appearance);
        Assert.Equal(SubtitleAppearance.Default with { Position = SubtitlePosition.Top },
            SubtitleAppearance.FromWire(Object(("position", new UntypedString("top")), ("fontSize", new UntypedString("huge")),
                ("fontFamily", new UntypedString("a;b")), ("textOutline", new UntypedString("yes")))));
        Assert.Equal(SubtitleAppearance.Default, SubtitleAppearance.FromWire(null));
        Assert.Equal(SubtitleAppearance.Default, SubtitleAppearance.FromWire(new UntypedString("box")));
    }

    [Fact]
    public void SanitizedKeepsColoursAndOpacitiesInsideTheSchema()
    {
        var odd = new SubtitleAppearance(FontColor: "white", BackgroundColor: "#12345", TextOutlineColor: "#ABCDEF", TextOpacity: 0,
            BackgroundOpacity: -5, FontFamily: " serif");
        Assert.Equal(new SubtitleAppearance(FontColor: "#ffffff", BackgroundColor: "#000000", TextOutlineColor: "#abcdef", TextOpacity: 1,
            BackgroundOpacity: 0), odd.Sanitized());
        Assert.True(SubtitleAppearance.ValidFamily("ヒラギノ角ゴ ProN"));
        Assert.False(SubtitleAppearance.ValidFamily(""));
        Assert.False(SubtitleAppearance.ValidFamily(new string('a', 65)));
        Assert.False(SubtitleAppearance.ValidFamily("Comic\"Sans"));
    }

    [Fact]
    public void TheWireObjectIsCompleteAndDropsTextOpacityForAnOlderServer()
    {
        var fields = SubtitleAppearance.Default.ToWire().GetValue();
        Assert.Equal(["fontSize", "fontFamily", "fontColor", "backgroundColor", "backgroundStyle", "backgroundOpacity", "textOpacity", "textOutline",
            "textOutlineColor", "position"], fields.Keys);
        Assert.DoesNotContain("textOpacity", SubtitleAppearance.Default.ToWire(includeTextOpacity: false).GetValue().Keys);
        Assert.True(SubtitleAppearance.SupportsTextOpacity(null));
        Assert.True(SubtitleAppearance.SupportsTextOpacity(14));
        Assert.False(SubtitleAppearance.SupportsTextOpacity(13));
        var round = new SubtitleAppearance(SubtitleFontSize.Small, "monospace", "#22c55e", 55, "#7f1d1d", SubtitleBackgroundStyle.None, 20, true,
            "#14532d", SubtitlePosition.Top);
        Assert.Equal(round, SubtitleAppearance.FromWire(round.ToWire()));
    }

    [Fact]
    public void TheRowsOfferTheTvChoicesInOrderWithTheirLabels()
    {
        Assert.Equal(["Small", "Medium", "Large", "X-Large", "XX-Large"], SubtitleAppearanceOptions.FontSizes.Select(option => option.Label));
        Assert.Equal(["Sans-serif", "Serif", "Monospace"], SubtitleAppearanceOptions.FontFamilies.Select(option => option.Label));
        Assert.Equal(["No background", "Box", "Drop Shadow", "Outline"], SubtitleAppearanceOptions.BackgroundStyles.Select(option => option.Label));
        Assert.Equal(["Bottom", "Lower Third", "Top"], SubtitleAppearanceOptions.Positions.Select(option => option.Label));
        Assert.Equal(["#ffffff", "#facc15", "#22c55e", "#06b6d4", "#d946ef", "#ef4444", "#3b82f6", "#9ca3af", "#000000"],
            SubtitleAppearanceOptions.FontColors.Select(option => option.Value));
        // Apple outlineColors = backgroundColors: the outline palette is the dark one.
        Assert.Equal(["Black", "Dark Gray", "Navy", "Dark Red", "Dark Green"], SubtitleAppearanceOptions.OutlineColors.Select(option => option.Label));
        Assert.Equal(21, SubtitleAppearanceOptions.BackgroundOpacitySteps.Count);
        Assert.Equal(0, SubtitleAppearanceOptions.BackgroundOpacitySteps[0]);
        Assert.Equal(5, SubtitleAppearanceOptions.TextOpacitySteps[0]);
        Assert.Equal(100, SubtitleAppearanceOptions.TextOpacitySteps[^1]);
        // percentOptions: a value set elsewhere stays a choice.
        Assert.Equal([0, 5, 7, 10], SubtitleAppearanceOptions.PercentOptions([0, 5, 10], 7));
        Assert.Equal("Helvetica Neue", SubtitleAppearanceOptions.FontFamilyLabel("Helvetica Neue"));
        Assert.Equal("#123456", SubtitleAppearanceOptions.FontColorLabel("#123456"));
        Assert.Equal("Yellow", SubtitleAppearanceOptions.FontColorLabel("#FACC15"));
        Assert.Equal("75%", SubtitleAppearanceOptions.Percent(75));
        // TvSettingsSubtitlePreview: pointSize × 0.36 sp, at the TV text scale.
        Assert.Equal(17.34, SubtitleAppearanceOptions.PreviewFontSize(SubtitleFontSize.Large));
        Assert.Equal(11.15, SubtitleAppearanceOptions.PreviewFontSize(SubtitleFontSize.Small));
    }

    [Fact]
    public void PickersCheckTheStoredValueAndApplyAChoice()
    {
        var appearance = SubtitleAppearance.Default with { TextOpacity = 42 };
        var preferences = new SubtitlePreferences("pt-BR", SubtitleMode.Off, true, true);
        var language = SubtitleSettingsPickers.Build(SubtitlePicker.Language, preferences, ["ja"], appearance);
        Assert.Equal("Language", language.Title);
        Assert.Equal("", language.Options[0].Id);
        Assert.Equal("None", language.Options[0].Label);
        Assert.Contains(language.Options, option => option.Id == "pt-BR");
        Assert.Equal("pt-BR", language.SelectedId);
        Assert.Equal(language.Options.ToList().FindIndex(option => option.Id == "pt-BR"), language.FocusIndex);

        var behavior = SubtitleSettingsPickers.Build(SubtitlePicker.Behavior, preferences, [], appearance);
        Assert.Equal(["off", "auto", "always"], behavior.Options.Select(option => option.Id));
        Assert.Equal(["Off", "Auto", "Always"], behavior.Options.Select(option => option.Label));
        Assert.Equal("off", behavior.SelectedId);

        var opacity = SubtitleSettingsPickers.Build(SubtitlePicker.TextOpacity, preferences, [], appearance);
        Assert.Equal("42", opacity.SelectedId);
        Assert.Contains(opacity.Options, option => option.Id == "42" && option.Label == "42%");
        Assert.Equal("5", opacity.Options[0].Id);

        // A family another client stored is not a choice here: none is checked and focus starts on the first row.
        var family = SubtitleSettingsPickers.Build(SubtitlePicker.FontFamily, preferences, [], appearance with { FontFamily = "Avenir" });
        Assert.Null(family.Options.FirstOrDefault(option => option.Id == family.SelectedId));
        Assert.Equal(0, family.FocusIndex);

        Assert.Equal(["Font Size", "Font Family", "Font Color", "Text Opacity", "Outline Color", "Background Style", "Background Opacity",
                "Background Color", "Position"],
            new[] { SubtitlePicker.FontSize, SubtitlePicker.FontFamily, SubtitlePicker.FontColor, SubtitlePicker.TextOpacity, SubtitlePicker.OutlineColor,
                SubtitlePicker.BackgroundStyle, SubtitlePicker.BackgroundOpacity, SubtitlePicker.BackgroundColor, SubtitlePicker.Position }
                .Select(SubtitleSettingsPickers.Title));

        Assert.Equal(SubtitleFontSize.Small, SubtitleSettingsPickers.Apply(SubtitlePicker.FontSize, "small", appearance)!.FontSize);
        Assert.Equal("serif", SubtitleSettingsPickers.Apply(SubtitlePicker.FontFamily, "serif", appearance)!.FontFamily);
        Assert.Equal("#facc15", SubtitleSettingsPickers.Apply(SubtitlePicker.FontColor, "#FACC15", appearance)!.FontColor);
        Assert.Equal(60, SubtitleSettingsPickers.Apply(SubtitlePicker.TextOpacity, "60", appearance)!.TextOpacity);
        Assert.Equal("#374151", SubtitleSettingsPickers.Apply(SubtitlePicker.OutlineColor, "#374151", appearance)!.TextOutlineColor);
        Assert.Equal(SubtitleBackgroundStyle.Shadow, SubtitleSettingsPickers.Apply(SubtitlePicker.BackgroundStyle, "shadow", appearance)!.BackgroundStyle);
        Assert.Equal(0, SubtitleSettingsPickers.Apply(SubtitlePicker.BackgroundOpacity, "0", appearance)!.BackgroundOpacity);
        Assert.Equal("#1e3a5f", SubtitleSettingsPickers.Apply(SubtitlePicker.BackgroundColor, "#1e3a5f", appearance)!.BackgroundColor);
        Assert.Equal(SubtitlePosition.LowerThird, SubtitleSettingsPickers.Apply(SubtitlePicker.Position, "lower-third", appearance)!.Position);
        Assert.Null(SubtitleSettingsPickers.Apply(SubtitlePicker.TextOpacity, "0", appearance));
        Assert.Null(SubtitleSettingsPickers.Apply(SubtitlePicker.BackgroundOpacity, "101", appearance));
        Assert.Null(SubtitleSettingsPickers.Apply(SubtitlePicker.FontFamily, "Avenir", appearance));
        Assert.Null(SubtitleSettingsPickers.Apply(SubtitlePicker.Language, "en", appearance));
        Assert.Null(SubtitleSettingsPickers.Apply(SubtitlePicker.Behavior, "off", appearance));
    }

    [Fact]
    public void TheDefaultDrawsAQuarterTransparentBlackBoxBehindWhiteText()
    {
        // ConvertOptions' 1920×1080 script: Large is the converter's former 60, the box one per cue (libass border style 4)
        // padded by an eighth of the size, 75% opaque (alpha 191, so ASS transparency 0x40), no outline.
        Assert.Equal(new SubtitleTextStyle("Arial", 60, 0x00FF_FFFF, 0x0000_0000, 0x4000_0000, 4, 0, 7.5f, 2, 54),
            SubtitleTextStyle.For(SubtitleAppearance.Default));
    }

    [Fact]
    public void BackgroundStylesFollowAndroidsCaptionStyle()
    {
        var yellow = SubtitleAppearance.Default with { FontColor = "#facc15", TextOpacity = 50, TextOutlineColor = "#1e3a5f" };
        // Box with Text Outline: the box and the outline, in the outline colour.
        var box = SubtitleTextStyle.For(yellow with { TextOutline = true, BackgroundColor = "#7f1d1d", BackgroundOpacity = 100 });
        Assert.Equal((4u, 3f, 0x001D_1D7Fu, 0x005F_3A1Eu), (box.BorderStyle, box.Outline, box.BackColour, box.OutlineColour));
        // 50% text: alpha 50 × 255 / 100 = 127, so ASS transparency 0x80.
        Assert.Equal(0x8015_CCFAu, box.PrimaryColour);
        // Drop Shadow wins over the outline: a shadow in the outline colour.
        var shadow = SubtitleTextStyle.For(yellow with { BackgroundStyle = SubtitleBackgroundStyle.Shadow, TextOutline = true });
        Assert.Equal((1u, 0f, 2f, 0x005F_3A1Eu), (shadow.BorderStyle, shadow.Outline, shadow.Shadow, shadow.BackColour));
        // Outline always outlines; No background only with Text Outline.
        var outline = SubtitleTextStyle.For(yellow with { BackgroundStyle = SubtitleBackgroundStyle.Outline });
        Assert.Equal((1u, 3f, 0f), (outline.BorderStyle, outline.Outline, outline.Shadow));
        var plain = SubtitleTextStyle.For(yellow with { BackgroundStyle = SubtitleBackgroundStyle.None });
        Assert.Equal((1u, 0f, 0f), (plain.BorderStyle, plain.Outline, plain.Shadow));
        var outlined = SubtitleTextStyle.For(yellow with { BackgroundStyle = SubtitleBackgroundStyle.None, TextOutline = true });
        Assert.Equal(3f, outlined.Outline);
        // A fully transparent box draws nothing: no box at all.
        var clear = SubtitleTextStyle.For(yellow with { BackgroundOpacity = 0 });
        Assert.Equal((1u, 0f), (clear.BorderStyle, clear.Shadow));
    }

    [Fact]
    public void SizesFamiliesAndPositionsMapToTheScript()
    {
        Assert.Equal([42u, 51u, 60u, 74u, 92u], SubtitleAppearanceOptions.FontSizes.Select(option => SubtitleTextStyle.FontSizeOf(option.Value)));
        Assert.Equal(["Arial", "Times New Roman", "Courier New", "Helvetica Neue"],
            new[] { "sans-serif", "serif", "monospace", "Helvetica Neue" }.Select(SubtitleTextStyle.FontNameOf));
        var top = SubtitleTextStyle.For(SubtitleAppearance.Default with { Position = SubtitlePosition.Top });
        Assert.Equal((8u, 54u), (top.Alignment, top.MarginV));
        var third = SubtitleTextStyle.For(SubtitleAppearance.Default with { Position = SubtitlePosition.LowerThird });
        Assert.Equal((2u, 194u), (third.Alignment, third.MarginV));
        Assert.Equal(51 / 8f, SubtitleTextStyle.For(SubtitleAppearance.Default with { FontSize = SubtitleFontSize.Medium }).Shadow);
        Assert.Equal(0xFF00_0000u, SubtitleTextStyle.AssColour("#000000", 0));
        Assert.Equal(0x00FF_8000u, SubtitleTextStyle.AssColour("#0080ff", 100));
    }

    [Fact]
    public void UseDeviceSettingsTakesTheConsolesCaptionStyle()
    {
        var app = new SubtitleAppearance(SubtitleFontSize.Small, "serif", "#22c55e", 80, "#1e3a5f", SubtitleBackgroundStyle.Shadow, 40, true,
            "#7f1d1d", SubtitlePosition.Top);
        // Everything Default: the system's 100% size (Large) and a box; the app's colours, opacities and family stay;
        // the outline only for a uniform edge; position is never a system setting.
        Assert.Equal(app with { FontSize = SubtitleFontSize.Large, BackgroundStyle = SubtitleBackgroundStyle.Box, TextOutline = false },
            new SystemCaptionStyle().Apply(app));
        var system = new SystemCaptionStyle("#FFFF00", 25, 1.5, SystemCaptionFont.MonospacedWithoutSerifs, SystemCaptionEdge.Uniform, "#000080", 0);
        Assert.Equal(new SubtitleAppearance(SubtitleFontSize.XLarge, "monospace", "#ffff00", 25, "#000080", SubtitleBackgroundStyle.None, 0, true,
            "#7f1d1d", SubtitlePosition.Top), system.Apply(app));
        Assert.Equal(SubtitleFontSize.Small, new SystemCaptionStyle(FontScale: 0.5).Apply(app).FontSize);
        Assert.Equal(SubtitleFontSize.XXLarge, new SystemCaptionStyle(FontScale: 2).Apply(app).FontSize);
        Assert.Equal("serif", new SystemCaptionStyle(Font: SystemCaptionFont.ProportionalWithSerifs).Apply(app).FontFamily);
        Assert.Equal("sans-serif", new SystemCaptionStyle(Font: SystemCaptionFont.ProportionalWithoutSerifs).Apply(app).FontFamily);
        Assert.Equal("serif", new SystemCaptionStyle(Font: SystemCaptionFont.Cursive).Apply(app).FontFamily);
        // A transparent system font still leaves something to pick back up (the schema's text opacity floor).
        Assert.Equal(1, new SystemCaptionStyle(FontOpacity: 0).Apply(app).TextOpacity);
        Assert.Equal(SubtitleBackgroundStyle.Box, new SystemCaptionStyle(BackgroundOpacity: 75).Apply(app).BackgroundStyle);
        Assert.False(new SystemCaptionStyle(Edge: SystemCaptionEdge.DropShadow).Apply(app).TextOutline);
    }
}
