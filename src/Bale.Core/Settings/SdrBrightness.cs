using System.Globalization;

namespace Bale.Core.Settings;

/// <summary>
/// Settings → Playback → HDR on SDR TVs → SDR Brightness, an owner-requested deviation from the official Silo apps
/// (2026-11-24; they never tone-map on the device). The SDR white, in nits, that the console's tone map maps HDR video to
/// on an SDR TV, the same for Bale's GPU stage and rav1g's display pass: the fork's <c>bale_tonemap_white_nits</c>
/// (wrapper 2.1.0-bale.77, "SDR white (brightness)"). BT.2390 ends at 2.03 times this white, so a higher white maps more
/// of the source into the SDR range and the picture looks brighter. Device-local; a stored value is one choice's nits.
/// </summary>
public static class SdrBrightness
{
    /// <summary>The reference white: the server's tonemapx default, and the fork's own while the key is unset.</summary>
    public const int DefaultNits = 100;

    /// <summary>The whites the fork takes (it rejects more than 400 and treats 1-79 as 80); the debug override's range.</summary>
    public const int MinimumNits = 80;
    public const int MaximumNits = 400;

    /// <summary>The picker's order: Default first, then dimmer to brighter.</summary>
    public static IReadOnlyList<int> Choices { get; } = [DefaultNits, 80, 120, 150, 200, 250, 300];

    /// <summary>A stored value as one of the choices: absent, unknown and out-of-range values read as Default.</summary>
    public static int FromStored(int? nits) => nits is { } value && Choices.Contains(value) ? value : DefaultNits;

    /// <summary>The row's value and each option's label: "Default (100 nits)", "150 nits".</summary>
    public static string Label(int nits) => nits == DefaultNits
        ? string.Create(CultureInfo.InvariantCulture, $"Default ({DefaultNits} nits)")
        : string.Create(CultureInfo.InvariantCulture, $"{nits} nits");
}
