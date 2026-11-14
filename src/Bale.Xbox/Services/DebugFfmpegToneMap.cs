#if DEBUG_SERVER
using System.Globalization;
using FFmpegInteropX;

namespace Bale.Xbox.Services;

internal enum DebugToneMap { Off = 0, Bt2390 = 1, Hable = 2 }

internal static class DebugFfmpegToneMap
{
    internal const string MinimumWrapperVersion = "2.1.0-bale.25";

    // The current fork rejects unknown bale_* keys in TryParseBaleOption with E_INVALIDARG.
    // c3a9adbf (wrapper bale.24), Source/FFmpegMediaSource.cpp. They never reach FFmpeg.
    internal static bool Supported(string version)
    {
        const string prefix = "2.1.0-bale.";
        return version.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(version.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            && revision >= 25;
    }

    internal static string Name(DebugToneMap? toneMap) => toneMap switch
    {
        DebugToneMap.Off => "off",
        DebugToneMap.Bt2390 => "bt2390",
        DebugToneMap.Hable => "hable",
        _ => "default",
    };

    internal static string? ValidationError(DebugFfmpegInputTuning tuning, string version)
    {
        if (tuning.ToneMap is null) return null;
        if (tuning.Decoder != DebugFfmpegDecoder.Auto)
            return "tonemap requires decoder=auto in the same tuning; clear it with tonemap=default before changing decoder.";
        if (!Supported(version))
            return "tonemap requires FFmpegInteropX wrapper 2.1.0-bale.25 or later; this package rejects unknown bale_* keys. No tuning changed.";
        return null;
    }

    internal static void Apply(MediaSourceConfig config, DebugFfmpegInputTuning tuning, string version)
    {
        if (ValidationError(tuning, version) is { } error) throw new InvalidDataException(error);
        if (tuning.ToneMap is { } toneMap)
            config.FFmpegOptions["bale_tonemap"] = ((int)toneMap).ToString(CultureInfo.InvariantCulture);
        // No override retains the production route's choice (or leaves the key absent).
    }
}
#endif
