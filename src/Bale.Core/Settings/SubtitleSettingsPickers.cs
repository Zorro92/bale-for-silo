using System.Globalization;

namespace Bale.Core.Settings;

/// <summary>The picker sheets of Settings › Subtitles (TvSettingsScreen.kt SubtitlePicker; Metadata Language is not offered).</summary>
public enum SubtitlePicker
{
    Language, Behavior, FontSize, FontFamily, FontColor, TextOpacity, OutlineColor, BackgroundStyle, BackgroundOpacity, BackgroundColor,
    Position,
}

/// <summary>A picker sheet: its title, its rows in order and the checked row's id (null when none is).</summary>
public sealed record SettingsPickerModel(string Title, IReadOnlyList<SettingsChoice> Options, string? SelectedId)
{
    /// <summary>TvSettingsPickerSheet: focus starts on the checked row, else the first.</summary>
    public int FocusIndex => Math.Max(0, Options.ToList().FindIndex(option => option.Id == SelectedId));
}

/// <summary>
/// TvSubtitleSettingsPane's pickers (TvSettingsScreen.kt:2022–2172): each sheet's choices and checked row from the
/// current values, and what a chosen row changes. Ids are the stored spellings (a language tag, "" for None, wire
/// enums, #rrggbb colours, percentages), so a value set by another client and kept in a list is checked.
/// </summary>
public static class SubtitleSettingsPickers
{
    /// <summary>The row labels, which are also the sheet titles (settings_control_* and settings_subtitles_*).</summary>
    public static string Title(SubtitlePicker picker) => picker switch
    {
        SubtitlePicker.Language => SettingsLanguages.SubtitleTitle,
        SubtitlePicker.Behavior => SubtitleModes.Title,
        SubtitlePicker.FontSize => "Font Size",
        SubtitlePicker.FontFamily => "Font Family",
        SubtitlePicker.FontColor => "Font Color",
        SubtitlePicker.TextOpacity => "Text Opacity",
        SubtitlePicker.OutlineColor => "Outline Color",
        SubtitlePicker.BackgroundStyle => "Background Style",
        SubtitlePicker.BackgroundOpacity => "Background Opacity",
        SubtitlePicker.BackgroundColor => "Background Color",
        _ => "Position",
    };

    public static SettingsPickerModel Build(SubtitlePicker picker, SubtitlePreferences preferences, IReadOnlyList<string> suggestions,
        SubtitleAppearance appearance)
    {
        var title = Title(picker);
        return picker switch
        {
            SubtitlePicker.Language => new(title, SettingsLanguages.SubtitleOptions(preferences.Language, suggestions), preferences.Language),
            SubtitlePicker.Behavior => new(title, [.. SubtitleModes.All.Select(mode => new SettingsChoice(SubtitleModes.Wire(mode), SubtitleModes.Label(mode)))],
                SubtitleModes.Wire(preferences.Mode)),
            SubtitlePicker.FontSize => new(title, [.. SubtitleAppearanceOptions.FontSizes.Select(option =>
                new SettingsChoice(SubtitleAppearance.Wire(option.Value), option.Label))], SubtitleAppearance.Wire(appearance.FontSize)),
            SubtitlePicker.FontFamily => new(title, Choices(SubtitleAppearanceOptions.FontFamilies), appearance.FontFamily),
            SubtitlePicker.FontColor => new(title, Choices(SubtitleAppearanceOptions.FontColors), appearance.FontColor),
            SubtitlePicker.TextOpacity => Percentages(title, SubtitleAppearanceOptions.TextOpacitySteps, appearance.TextOpacity),
            SubtitlePicker.OutlineColor => new(title, Choices(SubtitleAppearanceOptions.OutlineColors), appearance.TextOutlineColor),
            SubtitlePicker.BackgroundStyle => new(title, [.. SubtitleAppearanceOptions.BackgroundStyles.Select(option =>
                new SettingsChoice(SubtitleAppearance.Wire(option.Value), option.Label))], SubtitleAppearance.Wire(appearance.BackgroundStyle)),
            SubtitlePicker.BackgroundOpacity => Percentages(title, SubtitleAppearanceOptions.BackgroundOpacitySteps, appearance.BackgroundOpacity),
            SubtitlePicker.BackgroundColor => new(title, Choices(SubtitleAppearanceOptions.BackgroundColors), appearance.BackgroundColor),
            _ => new(title, [.. SubtitleAppearanceOptions.Positions.Select(option =>
                new SettingsChoice(SubtitleAppearance.Wire(option.Value), option.Label))], SubtitleAppearance.Wire(appearance.Position)),
        };
    }

    /// <summary>The appearance a chosen row of an appearance picker makes; null for the other pickers or an unknown id.</summary>
    public static SubtitleAppearance? Apply(SubtitlePicker picker, string id, SubtitleAppearance appearance) => picker switch
    {
        SubtitlePicker.FontSize when SubtitleAppearance.FontSizeFromWire(id) is { } size => appearance with { FontSize = size },
        SubtitlePicker.FontFamily when SubtitleAppearanceOptions.FontFamilies.Any(option => option.Value == id) => appearance with { FontFamily = id },
        SubtitlePicker.FontColor when SubtitleAppearance.Hex(id) is { } color => appearance with { FontColor = color },
        SubtitlePicker.TextOpacity when Percent(id) is { } opacity && opacity >= 1 => appearance with { TextOpacity = opacity },
        SubtitlePicker.OutlineColor when SubtitleAppearance.Hex(id) is { } color => appearance with { TextOutlineColor = color },
        SubtitlePicker.BackgroundStyle when SubtitleAppearance.BackgroundStyleFromWire(id) is { } style => appearance with { BackgroundStyle = style },
        SubtitlePicker.BackgroundOpacity when Percent(id) is { } opacity => appearance with { BackgroundOpacity = opacity },
        SubtitlePicker.BackgroundColor when SubtitleAppearance.Hex(id) is { } color => appearance with { BackgroundColor = color },
        SubtitlePicker.Position when SubtitleAppearance.PositionFromWire(id) is { } position => appearance with { Position = position },
        _ => null,
    };

    private static IReadOnlyList<SettingsChoice> Choices(IReadOnlyList<(string Value, string Label)> options) =>
        [.. options.Select(option => new SettingsChoice(option.Value, option.Label))];

    private static SettingsPickerModel Percentages(string title, IReadOnlyList<int> steps, int current) => new(title,
        [.. SubtitleAppearanceOptions.PercentOptions(steps, current).Select(value =>
            new SettingsChoice(value.ToString(CultureInfo.InvariantCulture), SubtitleAppearanceOptions.Percent(value)))],
        current.ToString(CultureInfo.InvariantCulture));

    private static int? Percent(string id) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value <= 100 ? value : null;
}
