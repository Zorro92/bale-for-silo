#if DEBUG_SERVER
using System.Globalization;
using FFmpegInteropX;

namespace Bale.Xbox.Services;

/// <summary>Tone-map debug switches for the opens-to-failure work (debug builds only).</summary>
internal static class DebugToneMapSwitches
{
    private static readonly string[] Known = ["noselftest", "nostate"];

    /// <summary>The words bale_gpu's tone map accepts in BALE_TONEMAP_DEBUG.</summary>
    public static bool Valid(string flags) =>
        flags.Split(',', StringSplitOptions.RemoveEmptyEntries).All(word => Known.Contains(word));

    /// <summary>
    /// The fork's bale_tonemap_output bitmask: 1 = decoder-style output copies, 2 = no output colour
    /// attributes, 4 = no per-frame Flush (bale.33); 8 = fork stage without bale_gpu (bale.34,
    /// picture undefined). 0 leaves the key unset.
    /// </summary>
    public static int OutputMask { get; set; }

    public static bool ValidOutputMask(int mask) => mask is >= 0 and <= 15;

    // Older wrappers reject unknown bale_* keys with E_INVALIDARG; bit 8 needs bale.34.
    public static bool OutputSupported(string version, int mask = 0)
    {
        const string prefix = "2.1.0-bale.";
        return version.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(version.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            && revision >= (mask >= 8 ? 34 : 33);
    }

    /// <summary>Adds bale_tonemap_output only when the tone-map stage is on for this open.</summary>
    public static void ApplyOutput(MediaSourceConfig config, string version)
    {
        if (OutputMask == 0 || !config.FFmpegOptions.ContainsKey("bale_tonemap") || !OutputSupported(version, OutputMask)) return;
        config.FFmpegOptions["bale_tonemap_output"] = OutputMask.ToString(CultureInfo.InvariantCulture);
    }
}
#endif
