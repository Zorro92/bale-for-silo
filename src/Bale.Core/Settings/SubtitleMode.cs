namespace Bale.Core.Settings;

/// <summary>
/// When subtitles turn on: <c>playback.subtitle_mode</c> (settings manifest: auto, always or off; default auto). The
/// order is the Behavior picker's (androidTvApp TvSettingsEnums.kt SubtitleMode.values()).
/// </summary>
public enum SubtitleMode
{
    /// <summary>No subtitles; forced ones still show when Show Forced Subtitles is on.</summary>
    Off,
    /// <summary>The preferred language when it differs from the audio's.</summary>
    Auto,
    /// <summary>Subtitles on every track.</summary>
    Always,
}

public static class SubtitleModes
{
    public const string Key = "playback.subtitle_mode";

    public const SubtitleMode Default = SubtitleMode.Auto;

    /// <summary>Settings › Subtitles › Profile: the row and the picker title (settings_control_behavior).</summary>
    public const string Title = "Behavior";

    public static IReadOnlyList<SubtitleMode> All { get; } = [SubtitleMode.Off, SubtitleMode.Auto, SubtitleMode.Always];

    public static string Wire(SubtitleMode mode) => mode switch
    {
        SubtitleMode.Off => "off",
        SubtitleMode.Always => "always",
        _ => "auto",
    };

    /// <summary>
    /// ProfileSettingsController.kt normalizeSubtitleMode: trimmed and lower-cased; anything else (the legacy empty string
    /// means unset, not a fourth mode) is null, which callers read as the default.
    /// </summary>
    public static SubtitleMode? FromWire(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => SubtitleMode.Off,
        "auto" => SubtitleMode.Auto,
        "always" => SubtitleMode.Always,
        _ => null,
    };

    /// <summary>TvSettingsScreen.kt subtitleModeLabel (the manifest's Off and Auto, settings_subtitles_mode_always).</summary>
    public static string Label(SubtitleMode mode) => mode switch
    {
        SubtitleMode.Off => "Off",
        SubtitleMode.Always => "Always",
        _ => "Auto",
    };
}
