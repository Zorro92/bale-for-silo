namespace Bale.Core.Settings;

/// <summary>
/// The <c>Default</c> style bale_gpu's SubRip and WebVTT converter draws with (native/bale-gpu subs::convert
/// ConvertOptions, passed by <c>bale_sub_overlay_load_text_styled</c>), from the subtitle appearance. Only text
/// sidecars take it: ASS/SSA keeps its authored styling and image subtitles their pixels, as on Android TV
/// (SubtitleManager.kt applyAppearance: "ASS/SSA is rendered by libass and deliberately preserves the script's authored
/// typesetting ... matching the Apple player"). Sizes are script pixels on the converter's 1920×1080 script; colours
/// are ASS colours, 0xAABBGGRR with AA = transparency (0 = opaque).
/// </summary>
/// <param name="BorderStyle">1 = outline and shadow, 4 = libass's one box per event in <paramref name="BackColour"/>.</param>
/// <param name="Shadow">The drop-shadow depth with border style 1, the box's padding with 4.</param>
/// <param name="Alignment">Numpad alignment: 2 bottom centre, 8 top centre.</param>
public sealed record SubtitleTextStyle(string FontName, uint FontSize, uint PrimaryColour, uint OutlineColour, uint BackColour,
    uint BorderStyle, float Outline, float Shadow, uint Alignment, uint MarginV)
{
    /// <summary>The converter's script height (ConvertOptions play_res_y).</summary>
    public const uint ScriptHeight = 1080;

    /// <summary>The converter's outline width before this style existed (ConvertOptions::default, console-checked).</summary>
    public const float OutlineWidth = 3;

    /// <summary>A drop shadow's depth: just clear of the 3 px outline it replaces.</summary>
    public const float ShadowDepth = 2;

    /// <summary>The bottom and top margin: the converter's 54 px (5% of 1080, title-safe).</summary>
    public const uint EdgeMargin = 54;

    /// <summary>Lower Third: SubtitleManager.kt subtitleBottomPaddingFraction's 18% of the picture height.</summary>
    public const uint LowerThirdMargin = 194;

    /// <summary>
    /// Script font sizes for Small … XX-Large. Large is the converter's former 60 (console-checked as Android's size);
    /// the others keep Android TV's ladder ratios (AndroidSubtitleTextSizePolicy.kt Television: 18, 22, 26, 32 and 40 sp).
    /// </summary>
    public static uint FontSizeOf(SubtitleFontSize size) => size switch
    {
        SubtitleFontSize.Small => 42,
        SubtitleFontSize.Medium => 51,
        SubtitleFontSize.XLarge => 74,
        SubtitleFontSize.XXLarge => 92,
        _ => 60,
    };

    /// <summary>
    /// The three families as fonts the console has (the libass probe found Arial, Times New Roman and Courier New); any
    /// other family (an Apple client stores real names) goes to libass as it is, which falls back to Arial without it.
    /// </summary>
    public static string FontNameOf(string family) => family switch
    {
        SubtitleAppearance.SansSerif => "Arial",
        SubtitleAppearance.Serif => "Times New Roman",
        SubtitleAppearance.Monospace => "Courier New",
        _ => family,
    };

    /// <summary>
    /// SubtitleManager.kt buildCaptionStyle in ASS terms: Box draws one box behind each cue in the background colour
    /// and opacity (padded by an eighth of the font size), with the text outline when it is on; Drop Shadow draws a shadow
    /// in the outline colour and wins over the outline; Outline always outlines; No background outlines only when Text
    /// Outline is on. The text takes the font colour at the text opacity; the outline and shadow stay opaque.
    /// </summary>
    public static SubtitleTextStyle For(SubtitleAppearance appearance)
    {
        var safe = appearance.Sanitized();
        var size = FontSizeOf(safe.FontSize);
        var outlineColour = AssColour(safe.TextOutlineColor, 100);
        var box = safe.BackgroundStyle == SubtitleBackgroundStyle.Box && safe.BackgroundOpacity > 0;
        var shadow = safe.BackgroundStyle == SubtitleBackgroundStyle.Shadow;
        var outlined = !shadow && (safe.TextOutline || safe.BackgroundStyle == SubtitleBackgroundStyle.Outline);
        return new SubtitleTextStyle(
            FontNameOf(safe.FontFamily),
            size,
            AssColour(safe.FontColor, safe.TextOpacity),
            outlineColour,
            box ? AssColour(safe.BackgroundColor, safe.BackgroundOpacity) : shadow ? outlineColour : AssColour(safe.BackgroundColor, 0),
            box ? 4u : 1u,
            outlined ? OutlineWidth : 0,
            box ? size / 8f : shadow ? ShadowDepth : 0,
            safe.Position == SubtitlePosition.Top ? 8u : 2u,
            safe.Position == SubtitlePosition.LowerThird ? LowerThirdMargin : EdgeMargin);
    }

    /// <summary>
    /// An ASS colour from #rrggbb at <paramref name="opacityPercent"/>: alpha is percent × 255 / 100 as Android's
    /// buildCaptionStyle rounds it, and ASS stores its complement.
    /// </summary>
    public static uint AssColour(string hex, int opacityPercent)
    {
        var (red, green, blue) = SubtitleAppearance.Rgb(hex);
        var alpha = (uint)(Math.Clamp(opacityPercent, 0, 100) * 255 / 100);
        return ((255 - alpha) << 24) | ((uint)blue << 16) | ((uint)green << 8) | red;
    }
}
