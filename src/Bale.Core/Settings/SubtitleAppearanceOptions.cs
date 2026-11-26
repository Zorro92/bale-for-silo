using System.Globalization;

namespace Bale.Core.Settings;

/// <summary>
/// The Appearance rows' choices and labels, shared with the Android TV player HUD there
/// (androidTvApp TvSubtitleAppearanceOptions.kt; the palettes are silo-apple's SubtitleAppearance.fontColors /
/// backgroundColors / outlineColors). A stored value outside a list (set by the phone, web or an Apple client) is
/// labelled by its own value and stays selectable where the list is percentages.
/// </summary>
public static class SubtitleAppearanceOptions
{
    public static IReadOnlyList<(SubtitleFontSize Value, string Label)> FontSizes { get; } =
    [
        (SubtitleFontSize.Small, "Small"), (SubtitleFontSize.Medium, "Medium"), (SubtitleFontSize.Large, "Large"),
        (SubtitleFontSize.XLarge, "X-Large"), (SubtitleFontSize.XXLarge, "XX-Large"),
    ];

    public static IReadOnlyList<(string Value, string Label)> FontFamilies { get; } =
    [
        (SubtitleAppearance.SansSerif, "Sans-serif"), (SubtitleAppearance.Serif, "Serif"), (SubtitleAppearance.Monospace, "Monospace"),
    ];

    /// <summary>BACKGROUND_STYLES: No background, Box, Drop Shadow, Outline.</summary>
    public static IReadOnlyList<(SubtitleBackgroundStyle Value, string Label)> BackgroundStyles { get; } =
    [
        (SubtitleBackgroundStyle.None, "No background"), (SubtitleBackgroundStyle.Box, "Box"),
        (SubtitleBackgroundStyle.Shadow, "Drop Shadow"), (SubtitleBackgroundStyle.Outline, "Outline"),
    ];

    public static IReadOnlyList<(SubtitlePosition Value, string Label)> Positions { get; } =
    [
        (SubtitlePosition.Bottom, "Bottom"), (SubtitlePosition.LowerThird, "Lower Third"), (SubtitlePosition.Top, "Top"),
    ];

    /// <summary>FONT_COLORS (Apple fontColors).</summary>
    public static IReadOnlyList<(string Value, string Label)> FontColors { get; } =
    [
        ("#ffffff", "White"), ("#facc15", "Yellow"), ("#22c55e", "Green"), ("#06b6d4", "Cyan"), ("#d946ef", "Magenta"),
        ("#ef4444", "Red"), ("#3b82f6", "Blue"), ("#9ca3af", "Gray"), ("#000000", "Black"),
    ];

    /// <summary>BACKGROUND_COLORS (Apple backgroundColors).</summary>
    public static IReadOnlyList<(string Value, string Label)> BackgroundColors { get; } =
    [
        ("#000000", "Black"), ("#374151", "Dark Gray"), ("#1e3a5f", "Navy"), ("#7f1d1d", "Dark Red"), ("#14532d", "Dark Green"),
    ];

    /// <summary>OUTLINE_COLORS: the background palette (Apple outlineColors = backgroundColors), not the font palette.</summary>
    public static IReadOnlyList<(string Value, string Label)> OutlineColors => BackgroundColors;

    /// <summary>OPACITY_PERCENT_STEPS: 0–100 by 5 (Apple TVSettingsOptions.backgroundOpacity).</summary>
    public static IReadOnlyList<int> BackgroundOpacitySteps { get; } = [.. Enumerable.Range(0, 21).Select(step => step * 5)];

    /// <summary>TEXT_OPACITY_PERCENT_STEPS: 5–100 by 5; fully transparent text could not be picked back up.</summary>
    public static IReadOnlyList<int> TextOpacitySteps { get; } = [.. Enumerable.Range(1, 20).Select(step => step * 5)];

    /// <summary>percentOptions: the steps plus the saved value, so a value set elsewhere stays the checked choice.</summary>
    public static IReadOnlyList<int> PercentOptions(IReadOnlyList<int> steps, int current) => [.. steps.Append(current).Distinct().Order()];

    /// <summary>settings_subtitles_percent.</summary>
    public static string Percent(int value) => value.ToString(CultureInfo.InvariantCulture) + "%";

    public static string FontSizeLabel(SubtitleFontSize value) => FontSizes.First(option => option.Value == value).Label;

    public static string FontFamilyLabel(string value) => FontFamilies.FirstOrDefault(option => option.Value == value).Label ?? value;

    public static string BackgroundStyleLabel(SubtitleBackgroundStyle value) => BackgroundStyles.First(option => option.Value == value).Label;

    public static string PositionLabel(SubtitlePosition value) => Positions.First(option => option.Value == value).Label;

    public static string FontColorLabel(string hex) => ColorLabel(FontColors, hex);

    public static string BackgroundColorLabel(string hex) => ColorLabel(BackgroundColors, hex);

    public static string OutlineColorLabel(string hex) => ColorLabel(OutlineColors, hex);

    private static string ColorLabel(IReadOnlyList<(string Value, string Label)> palette, string hex) =>
        palette.FirstOrDefault(option => option.Value.Equals(hex, StringComparison.OrdinalIgnoreCase)).Label ?? hex;

    /// <summary>
    /// The Settings preview's text size (TvSettingsSubtitlePreview: SubtitleFontSizePreset.pointSize × 0.36 sp), at the TV
    /// app's 0.86 text scale (AGENTS.md "Unit mapping").
    /// </summary>
    public static double PreviewFontSize(SubtitleFontSize size) => Math.Round(PointSize(size) * 0.36 * 0.86, 2);

    /// <summary>SubtitleAppearance.kt pointSize: 36, 44, 56, 68 and 82.</summary>
    public static double PointSize(SubtitleFontSize size) => size switch
    {
        SubtitleFontSize.Small => 36,
        SubtitleFontSize.Medium => 44,
        SubtitleFontSize.XLarge => 68,
        SubtitleFontSize.XXLarge => 82,
        _ => 56,
    };
}
