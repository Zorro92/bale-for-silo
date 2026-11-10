#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>Memory-only overrides of release settings, captured per attempt and never applied to probes or native/HLS routes.</summary>
internal sealed record DebugFfmpegInputTuning(bool? ReadAhead = null, int? ReadSize = null, int? Prefetch = null, int? BufferMs = null,
    int? DecodeAheadFrames = null, int? Dav1dMaxFrameDelay = null, int? DecodeThreadPriority = null,
    int? DecoderUseMaxResolution = null, int? DecoderMaxDpbCount = null, DebugFilmGrain? FilmGrain = null)
{
    internal const int MinimumReadSize = 16 * 1024;
    internal const int MaximumReadSize = 1024 * 1024;
    // Decoded 1080p P010 frames are ~6 MB each: 5 s at 24 fps is ~750 MB, the most worth trying on Xbox.
    internal const int MaximumBufferMs = 5000;
    // Keep the extra native packet queue below its 100 MB / 60 s defaults on Xbox.
    internal const long ReadAheadCapBytes = 64L * 1024 * 1024;
    internal static readonly TimeSpan ReadAheadCapDuration = TimeSpan.FromSeconds(30);
    internal const string Usage = "expected [readahead=on|off|default] [readsize=16384..1048576|default] [prefetch=2..16|default] [buffer=0..5000|default] (MediaStreamSource.BufferTime ms) [ahead=0..64|default] [delay=0..64|default] [prio=0..2|default] (0=off, 1=above normal, 2=highest) [maxres=0|1|default] [dpb=0..16|default] (system video only; 0=unset) [grain=dav1d|gpu|off|on|default] (software video only; default=gpu (production), on/dav1d=dav1d, gpu=GPU, off=export-only; wrapper 2.1.0-bale.10+); software AV1 is fixed to zero-copy direct write (upload variant 6, wrapper 2.1.0-bale.17+); omitted keys keep their overrides; default restores the release setting (buffer=1000 for software video; software video: ahead=16, buffer=1000 ms, delay unset; above 1080p ahead=8, buffer=500 ms, delay=8; prio unset; system video: maxres/dpb unset)";
    private static readonly object Gate = new();
    private static DebugFfmpegInputTuning _current = new();

    internal static DebugFfmpegInputTuning Current => Volatile.Read(ref _current);

    internal string Description => $"readahead={(ReadAhead is { } enabled ? enabled ? "on" : "off" : "default")} "
        + $"readsize={ReadSize?.ToString(CultureInfo.InvariantCulture) ?? "default"} "
        + $"prefetch={Prefetch?.ToString(CultureInfo.InvariantCulture) ?? "default"}"
        + (BufferMs is { } buffer ? $" buffer={buffer.ToString(CultureInfo.InvariantCulture)}" : "")
        + (DecodeAheadFrames is { } ahead ? $" ahead={ahead.ToString(CultureInfo.InvariantCulture)}" : "")
        + (Dav1dMaxFrameDelay is { } delay ? $" delay={delay.ToString(CultureInfo.InvariantCulture)}" : "")
        + (DecodeThreadPriority is { } priority ? $" prio={priority.ToString(CultureInfo.InvariantCulture)}" : "")
        + (DecoderUseMaxResolution is { } maxres ? $" maxres={maxres.ToString(CultureInfo.InvariantCulture)}" : "")
        + (DecoderMaxDpbCount is { } dpb ? $" dpb={dpb.ToString(CultureInfo.InvariantCulture)}" : "")
        + (FilmGrain is { } grain ? " grain=" + DebugFfmpegPlaytest.GrainName(grain) : "");

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
                    case "buffer":
                        if (!TryNumber(value, 0, MaximumBufferMs, out var bufferMs)) return false;
                        next = next with { BufferMs = bufferMs };
                        break;
                    case "ahead":
                        if (!TryNumber(value, 0, 64, out var ahead)) return false;
                        next = next with { DecodeAheadFrames = ahead };
                        break;
                    case "delay":
                        if (!TryNumber(value, 0, 64, out var delay)) return false;
                        next = next with { Dav1dMaxFrameDelay = delay };
                        break;
                    case "prio":
                        if (!TryNumber(value, 0, 2, out var priority)) return false;
                        next = next with { DecodeThreadPriority = priority };
                        break;
                    case "maxres":
                        if (!TryNumber(value, 0, 1, out var maxres)) return false;
                        next = next with { DecoderUseMaxResolution = maxres };
                        break;
                    case "dpb":
                        if (!TryNumber(value, 0, 16, out var dpb)) return false;
                        next = next with { DecoderMaxDpbCount = dpb };
                        break;
                    case "grain":
                        if (!DebugFfmpegPlaytest.TryParseGrain(value, out var grain, out _)) return false;
                        next = next with { FilmGrain = grain };
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

    internal static string StreamReadsText(uint readSize, int prefetch) => string.Create(CultureInfo.InvariantCulture,
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
