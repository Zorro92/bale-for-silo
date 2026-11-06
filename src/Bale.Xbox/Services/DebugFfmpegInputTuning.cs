#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>Memory-only FFmpeg input experiment, captured once per attempt and never applied to probes or native/HLS routes.</summary>
internal sealed record DebugFfmpegInputTuning(bool? ReadAhead = null, int? ReadSize = null, int? Prefetch = null)
{
    internal const int MinimumReadSize = 16 * 1024;
    internal const int MaximumReadSize = 1024 * 1024;
    // Keep the extra native packet queue below its 100 MB / 60 s defaults on Xbox.
    internal const long ReadAheadCapBytes = 64L * 1024 * 1024;
    internal static readonly TimeSpan ReadAheadCapDuration = TimeSpan.FromSeconds(30);
    internal const string Usage = "expected [readahead=on|off|default] [readsize=16384..1048576|default] [prefetch=2..16|default]; omitted keys keep their overrides";
    private static readonly object Gate = new();
    private static DebugFfmpegInputTuning _current = new();

    internal static DebugFfmpegInputTuning Current => Volatile.Read(ref _current);

    internal string Description => $"readahead={(ReadAhead is { } enabled ? enabled ? "on" : "off" : "default")} "
        + $"readsize={ReadSize?.ToString(CultureInfo.InvariantCulture) ?? "default"} "
        + $"prefetch={Prefetch?.ToString(CultureInfo.InvariantCulture) ?? "default"}";

    internal static bool TrySet(IReadOnlyDictionary<string, string> options, out DebugFfmpegInputTuning tuning)
    {
        lock (Gate)
        {
            tuning = _current;
            var next = tuning;
            foreach (var (key, value) in options)
            {
                switch (key)
                {
                    case "readahead":
                        if (value is not ("on" or "off" or "default")) return false;
                        next = next with { ReadAhead = value == "default" ? null : value == "on" };
                        break;
                    case "readsize":
                        if (!TryNumber(value, MinimumReadSize, MaximumReadSize, out var readSize)) return false;
                        next = next with { ReadSize = readSize };
                        break;
                    case "prefetch":
                        if (!TryNumber(value, HttpRangeStream.DefaultPrefetchBlocks, HttpRangeStream.MaximumPrefetchBlocks, out var prefetch)) return false;
                        next = next with { Prefetch = prefetch };
                        break;
                    default:
                        return false;
                }
            }
            // Publish a whole immutable snapshot only after every option passed validation.
            Volatile.Write(ref _current, next);
            tuning = next;
            return true;
        }
    }

    internal static string ReadAheadText(bool enabled, long bytes, TimeSpan duration) => string.Create(CultureInfo.InvariantCulture,
        $"{(enabled ? "on" : "off")} · {bytes / (1024.0 * 1024):0.#} MiB / {duration.TotalSeconds:0.#} s");

    internal static string StreamReadsText(int readSize, int prefetch) => string.Create(CultureInfo.InvariantCulture,
        $"{readSize} B · prefetch={prefetch}");

    private static bool TryNumber(string value, int minimum, int maximum, out int? number)
    {
        number = null;
        if (value == "default") return true;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum || parsed > maximum)
            return false;
        number = parsed;
        return true;
    }
}
#endif
