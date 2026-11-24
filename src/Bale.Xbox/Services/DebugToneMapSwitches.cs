#if DEBUG_SERVER
using System.Globalization;
using FFmpegInteropX;

namespace Bale.Xbox.Services;

/// <summary>Tone-map debug switches (debug builds only).</summary>
internal static class DebugToneMapSwitches
{
    private static readonly string[] Known = ["noselftest", "nostate", "nostaged", "gammachroma", "fixedpeak", "bilinearchroma"];

    /// <summary>The known words, comma-separated, for usage messages.</summary>
    public static string Words { get; } = string.Join(',', Known);

    /// <summary>The words bale_gpu's tone map accepts in BALE_TONEMAP_DEBUG.</summary>
    public static bool Valid(string flags) =>
        flags.Split(',', StringSplitOptions.RemoveEmptyEntries).All(word => Known.Contains(word));

    /// <summary>
    /// Overrides the display-sized stage output (fork bale_tonemap_output_width/height, wrapper 2.1.0-bale.38+)
    /// for the next open; 0x0 keeps the automatic size from the HDMI mode.
    /// </summary>
    public static int OutputWidth { get; set; }

    public static int OutputHeight { get; set; }

    public static bool ValidOutputSize(int width, int height) =>
        (width, height) == (0, 0) || width is >= 2 and <= 7680 && height is >= 2 and <= 4320 && width % 2 == 0 && height % 2 == 0;

    public static bool SizeSupported(string version) => Revision(version) >= 38;

    private static int Revision(string version)
    {
        const string prefix = "2.1.0-bale.";
        return version.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(version.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            ? revision : -1;
    }

    /// <summary>Applies the size override only when the console stage is on for this open.</summary>
    public static void ApplyOutput(MediaSourceConfig config, string version)
    {
        if (!config.FFmpegOptions.ContainsKey("bale_tonemap") || OutputWidth == 0 || !SizeSupported(version)) return;
        config.FFmpegOptions["bale_tonemap_output_width"] = OutputWidth.ToString(CultureInfo.InvariantCulture);
        config.FFmpegOptions["bale_tonemap_output_height"] = OutputHeight.ToString(CultureInfo.InvariantCulture);
    }
}
#endif
