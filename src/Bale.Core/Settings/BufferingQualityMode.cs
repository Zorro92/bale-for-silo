namespace Bale.Core.Settings;

/// <summary>
/// What Silo does on Auto when playback keeps buffering: <c>playback.buffering_quality_mode</c>
/// (silo-server docs/design/2026-10-17-buffering-quality.md, "The setting"; silo-android
/// shared/.../domain/player/BufferingQualityMode.kt).
/// </summary>
public enum BufferingQualityMode
{
    /// <summary>Detect and report stalls only. Starts and replans send <c>learned_limit: ignore</c>.</summary>
    Never,
    /// <summary>Offer "Lower quality" / "Keep quality" before changing anything.</summary>
    Ask,
    /// <summary>Lower Auto at once and offer an undo (the contract default).</summary>
    Always,
}

public static class BufferingQualityModes
{
    public const string Key = "playback.buffering_quality_mode";
    public const BufferingQualityMode Default = BufferingQualityMode.Always;

    /// <summary>Settings → Playback → Streaming, the row and the picker title (settings_bufferingQuality_title).</summary>
    public const string Title = "Lower quality when buffering";

    /// <summary>The footer under the row (settings_bufferingQuality_description).</summary>
    public const string Description =
        "What Silo does on Auto when playback keeps buffering. Silo always asks before lowering a quality you picked.";

    /// <summary>The picker's order (BufferingQualityMode.entries).</summary>
    public static IReadOnlyList<BufferingQualityMode> All { get; } =
        [BufferingQualityMode.Never, BufferingQualityMode.Ask, BufferingQualityMode.Always];

    public static string Wire(BufferingQualityMode mode) => mode switch
    {
        BufferingQualityMode.Never => "never",
        BufferingQualityMode.Ask => "ask",
        _ => "always",
    };

    /// <summary>A stored or wire value; null for absent or unrecognised input.</summary>
    public static BufferingQualityMode? FromWire(string? value) => value switch
    {
        "never" => BufferingQualityMode.Never,
        "ask" => BufferingQualityMode.Ask,
        "always" => BufferingQualityMode.Always,
        _ => null,
    };

    /// <summary>The label each mode is offered under (the manifest's labels).</summary>
    public static string Label(BufferingQualityMode mode) => mode switch
    {
        BufferingQualityMode.Never => "Never",
        BufferingQualityMode.Ask => "Ask first",
        _ => "Automatically",
    };
}
