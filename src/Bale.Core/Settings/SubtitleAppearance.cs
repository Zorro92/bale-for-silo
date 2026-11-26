using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Bale.Core.Settings;

/// <summary>The appearance's size ladder (subtitle-appearance.json fontSize).</summary>
public enum SubtitleFontSize { Small, Medium, Large, XLarge, XXLarge }

/// <summary>What sits behind or around the text (subtitle-appearance.json backgroundStyle).</summary>
public enum SubtitleBackgroundStyle { None, Box, Shadow, Outline }

/// <summary>Where cues sit (subtitle-appearance.json position).</summary>
public enum SubtitlePosition { Bottom, LowerThird, Top }

/// <summary>
/// <c>playback.subtitle_appearance</c>: how text subtitles are drawn (settings contract schema subtitle-appearance.json;
/// silo-android shared SubtitleAppearance.kt). A stored value is a sparse override merged over the contract's complete
/// default (<see cref="Default"/>); the scopes do not merge (a device value replaces the profile's object).
/// </summary>
public sealed partial record SubtitleAppearance(
    SubtitleFontSize FontSize = SubtitleFontSize.Large,
    string FontFamily = SubtitleAppearance.SansSerif,
    string FontColor = "#ffffff",
    int TextOpacity = 100,
    string BackgroundColor = "#000000",
    SubtitleBackgroundStyle BackgroundStyle = SubtitleBackgroundStyle.Box,
    int BackgroundOpacity = 75,
    bool TextOutline = false,
    string TextOutlineColor = "#000000",
    SubtitlePosition Position = SubtitlePosition.Bottom)
{
    public const string Key = "playback.subtitle_appearance";

    public const string SansSerif = "sans-serif";
    public const string Serif = "serif";
    public const string Monospace = "monospace";

    /// <summary>
    /// The manifest's default_value: a 75% black box since contract revision 6, which every client should adopt for
    /// sparse decoding (Android's local fallback still says shadow; the server sends the complete default either way).
    /// </summary>
    public static SubtitleAppearance Default { get; } = new();

    /// <summary>Contract revision 14 added textOpacity; an older server refuses an object that carries it (SubtitleAppearance.kt).</summary>
    public const int TextOpacityRevision = 14;

    /// <summary>SubtitleAppearance.kt supportsTextOpacity: an unknown revision counts as supported.</summary>
    public static bool SupportsTextOpacity(long? contractRevision) => contractRevision is not { } revision || revision >= TextOpacityRevision;

    /// <summary>SubtitleAppearance.kt sanitized(): hex colours, background opacity 0–100 and text opacity 1–100.</summary>
    public SubtitleAppearance Sanitized() => this with
    {
        FontFamily = ValidFamily(FontFamily) ? FontFamily : Default.FontFamily,
        FontColor = Hex(FontColor) ?? Default.FontColor,
        BackgroundColor = Hex(BackgroundColor) ?? Default.BackgroundColor,
        TextOutlineColor = Hex(TextOutlineColor) ?? Default.TextOutlineColor,
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 100),
        TextOpacity = Math.Clamp(TextOpacity, 1, 100),
    };

    /// <summary>
    /// A stored or effective value: each property present and valid replaces the default's, anything else (a missing
    /// property, a wrong type, a value outside the schema) keeps the default's, so one bad field never drops the rest.
    /// </summary>
    public static SubtitleAppearance FromWire(UntypedNode? node)
    {
        if (node is not UntypedObject value) return Default;
        var fields = value.GetValue();
        var appearance = Default;
        if (Text(fields, "fontSize") is { } size && FontSizeFromWire(size) is { } fontSize) appearance = appearance with { FontSize = fontSize };
        if (Text(fields, "fontFamily") is { } family && ValidFamily(family)) appearance = appearance with { FontFamily = family };
        if (Hex(Text(fields, "fontColor")) is { } fontColor) appearance = appearance with { FontColor = fontColor };
        if (Hex(Text(fields, "backgroundColor")) is { } background) appearance = appearance with { BackgroundColor = background };
        if (Text(fields, "backgroundStyle") is { } style && BackgroundStyleFromWire(style) is { } backgroundStyle)
            appearance = appearance with { BackgroundStyle = backgroundStyle };
        if (Number(fields, "backgroundOpacity") is { } backgroundOpacity) appearance = appearance with { BackgroundOpacity = (int)Math.Clamp(backgroundOpacity, 0, 100) };
        if (Number(fields, "textOpacity") is { } textOpacity) appearance = appearance with { TextOpacity = (int)Math.Clamp(textOpacity, 1, 100) };
        if (fields.TryGetValue("textOutline", out var outline) && PlaybackSettingsFeed.Boolean(outline) is { } textOutline)
            appearance = appearance with { TextOutline = textOutline };
        if (Hex(Text(fields, "textOutlineColor")) is { } outlineColor) appearance = appearance with { TextOutlineColor = outlineColor };
        if (Text(fields, "position") is { } position && PositionFromWire(position) is { } subtitlePosition)
            appearance = appearance with { Position = subtitlePosition };
        return appearance;
    }

    /// <summary>
    /// The complete object a write sends (SubtitleAppearance.kt toJsonString of sanitized()), in the schema's property
    /// order; without textOpacity for a server older than revision 14 (wireObjectForRevision).
    /// </summary>
    public UntypedObject ToWire(bool includeTextOpacity = true)
    {
        var safe = Sanitized();
        var fields = new Dictionary<string, UntypedNode>
        {
            ["fontSize"] = new UntypedString(Wire(safe.FontSize)),
            ["fontFamily"] = new UntypedString(safe.FontFamily),
            ["fontColor"] = new UntypedString(safe.FontColor),
            ["backgroundColor"] = new UntypedString(safe.BackgroundColor),
            ["backgroundStyle"] = new UntypedString(Wire(safe.BackgroundStyle)),
            ["backgroundOpacity"] = new UntypedInteger(safe.BackgroundOpacity),
        };
        if (includeTextOpacity) fields["textOpacity"] = new UntypedInteger(safe.TextOpacity);
        fields["textOutline"] = new UntypedBoolean(safe.TextOutline);
        fields["textOutlineColor"] = new UntypedString(safe.TextOutlineColor);
        fields["position"] = new UntypedString(Wire(safe.Position));
        return new UntypedObject(fields);
    }

    public static string Wire(SubtitleFontSize size) => size switch
    {
        SubtitleFontSize.Small => "small",
        SubtitleFontSize.Medium => "medium",
        SubtitleFontSize.XLarge => "xlarge",
        SubtitleFontSize.XXLarge => "xxlarge",
        _ => "large",
    };

    public static SubtitleFontSize? FontSizeFromWire(string? value) => value switch
    {
        "small" => SubtitleFontSize.Small,
        "medium" => SubtitleFontSize.Medium,
        "large" => SubtitleFontSize.Large,
        "xlarge" => SubtitleFontSize.XLarge,
        "xxlarge" => SubtitleFontSize.XXLarge,
        _ => null,
    };

    public static string Wire(SubtitleBackgroundStyle style) => style switch
    {
        SubtitleBackgroundStyle.None => "none",
        SubtitleBackgroundStyle.Shadow => "shadow",
        SubtitleBackgroundStyle.Outline => "outline",
        _ => "box",
    };

    public static SubtitleBackgroundStyle? BackgroundStyleFromWire(string? value) => value switch
    {
        "none" => SubtitleBackgroundStyle.None,
        "box" => SubtitleBackgroundStyle.Box,
        "shadow" => SubtitleBackgroundStyle.Shadow,
        "outline" => SubtitleBackgroundStyle.Outline,
        _ => null,
    };

    public static string Wire(SubtitlePosition position) => position switch
    {
        SubtitlePosition.LowerThird => "lower-third",
        SubtitlePosition.Top => "top",
        _ => "bottom",
    };

    public static SubtitlePosition? PositionFromWire(string? value) => value switch
    {
        "bottom" => SubtitlePosition.Bottom,
        "lower-third" => SubtitlePosition.LowerThird,
        "top" => SubtitlePosition.Top,
        _ => null,
    };

    /// <summary>A schema hexColor (#rrggbb) in lower case, the spelling the TV palettes use; null otherwise.</summary>
    public static string? Hex(string? value) => value is not null && HexColor().IsMatch(value) ? value.ToLowerInvariant() : null;

    /// <summary>The schema's fontFamily: 1–64 characters, no control characters, quotes, separators or brackets.</summary>
    public static bool ValidFamily(string? value) => value is { Length: > 0 and <= 64 } && FamilyName().IsMatch(value);

    private static string? Text(IDictionary<string, UntypedNode> fields, string name) =>
        fields.TryGetValue(name, out var node) ? PlaybackSettingsFeed.Text(node) : null;

    private static long? Number(IDictionary<string, UntypedNode> fields, string name) =>
        fields.TryGetValue(name, out var node) ? PlaybackSettingsFeed.Integer(node) : null;

    /// <summary>The red, green and blue of a #rrggbb colour (callers pass a sanitized one).</summary>
    public static (byte Red, byte Green, byte Blue) Rgb(string hex)
    {
        var value = Hex(hex) ?? "#000000";
        var packed = int.Parse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex("^[^\\x00-\\x1f\"'(){};:,\\\\/ ][^\\x00-\\x1f\"'(){};:,\\\\/]*$")]
    private static partial Regex FamilyName();
}
