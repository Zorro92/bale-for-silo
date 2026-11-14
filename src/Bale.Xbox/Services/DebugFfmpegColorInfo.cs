#if DEBUG_SERVER
using System.Globalization;
using FFmpegInteropX;

namespace Bale.Xbox.Services;

internal static class DebugFfmpegColorInfo
{
    internal const string MinimumWrapperVersion = "2.1.0-bale.26";

    // Earlier wrappers reject unknown bale_* keys rather than passing them to FFmpeg.
    internal static bool Supported(string version)
    {
        const string prefix = "2.1.0-bale.";
        return version.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(version.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            && revision >= 26;
    }

    internal static string Name(bool? colorInfo) => colorInfo is { } enabled ? enabled ? "on" : "off" : "default";

    internal static string? ValidationError(DebugFfmpegInputTuning tuning, string version)
        => tuning.ColorInfo is not null && !Supported(version)
            ? "colorinfo requires FFmpegInteropX wrapper 2.1.0-bale.26 or later; this package rejects unknown bale_* keys. No tuning changed."
            : null;

    internal static void Apply(MediaSourceConfig config, DebugFfmpegInputTuning tuning, string version)
    {
        if (ValidationError(tuning, version) is { } error) throw new InvalidDataException(error);
        // Fork d3956a29, bale/README.md: compressed system video shares the uncompressed
        // colour/HDR mapping and useHdr policy. No decoder override is required.
        if (tuning.ColorInfo is { } enabled)
            config.FFmpegOptions["bale_compressed_color_info"] = enabled ? "1" : "0";
        else config.FFmpegOptions.Remove("bale_compressed_color_info");
    }
}
#endif
