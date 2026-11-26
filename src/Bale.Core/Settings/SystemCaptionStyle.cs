namespace Bale.Core.Settings;

/// <summary>The console's caption font (UWP ClosedCaptionStyle, same order).</summary>
public enum SystemCaptionFont
{
    Default, MonospacedWithSerifs, ProportionalWithSerifs, MonospacedWithoutSerifs, ProportionalWithoutSerifs, Casual, Cursive,
    SmallCapitals,
}

/// <summary>The console's caption edge (UWP ClosedCaptionEdgeEffect, same order).</summary>
public enum SystemCaptionEdge { Default, None, Raised, Depressed, Uniform, DropShadow }

/// <summary>
/// The console's own caption settings (Settings › Accessibility › Closed captions; UWP ClosedCaptionProperties), each
/// null or Default where the system leaves it to the app. Settings › Subtitles › Use Device Settings draws subtitles
/// in them, as Android TV follows the system captioning style (tvOS "Match Device Settings" parity).
/// </summary>
/// <param name="FontColor">#rrggbb, or null for Default.</param>
/// <param name="FontOpacity">Percent, or null for Default.</param>
/// <param name="FontScale">0.5, 1, 1.5 or 2; null for Default, which is 100%.</param>
/// <param name="BackgroundColor">#rrggbb, or null for Default.</param>
/// <param name="BackgroundOpacity">Percent, or null for Default.</param>
public sealed record SystemCaptionStyle(string? FontColor = null, int? FontOpacity = null, double? FontScale = null,
    SystemCaptionFont Font = SystemCaptionFont.Default, SystemCaptionEdge Edge = SystemCaptionEdge.Default,
    string? BackgroundColor = null, int? BackgroundOpacity = null)
{
    /// <summary>
    /// DeviceCaptioningAppearance.kt over the app's <paramref name="appearance"/>: size from the system scale (Default
    /// is 100%, Large), colours and opacities where the system sets them, a box unless the system background is fully
    /// transparent, the outline only for a uniform edge. Position is not a system setting and keeps the app's. Unlike
    /// Android (whose captioning typeface it does not map), the console's font style picks the family where it names one.
    /// </summary>
    public SubtitleAppearance Apply(SubtitleAppearance appearance) => (appearance with
    {
        FontSize = (FontScale ?? 1) switch
        {
            < 0.75 => SubtitleFontSize.Small,
            < 1.0 => SubtitleFontSize.Medium,
            < 1.25 => SubtitleFontSize.Large,
            < 1.75 => SubtitleFontSize.XLarge,
            _ => SubtitleFontSize.XXLarge,
        },
        FontFamily = Font switch
        {
            SystemCaptionFont.MonospacedWithSerifs or SystemCaptionFont.MonospacedWithoutSerifs => SubtitleAppearance.Monospace,
            SystemCaptionFont.ProportionalWithSerifs => SubtitleAppearance.Serif,
            SystemCaptionFont.ProportionalWithoutSerifs => SubtitleAppearance.SansSerif,
            _ => appearance.FontFamily,
        },
        FontColor = SubtitleAppearance.Hex(FontColor) ?? appearance.FontColor,
        TextOpacity = FontOpacity ?? appearance.TextOpacity,
        BackgroundColor = SubtitleAppearance.Hex(BackgroundColor) ?? appearance.BackgroundColor,
        BackgroundStyle = BackgroundOpacity == 0 ? SubtitleBackgroundStyle.None : SubtitleBackgroundStyle.Box,
        BackgroundOpacity = BackgroundOpacity ?? appearance.BackgroundOpacity,
        TextOutline = Edge == SystemCaptionEdge.Uniform,
    }).Sanitized();
}
