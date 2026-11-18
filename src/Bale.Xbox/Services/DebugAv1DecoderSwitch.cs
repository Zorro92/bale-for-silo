#if DEBUG_SERVER
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only software AV1 decoder, captured per playback attempt; dav1d after restart, as in release builds (which
/// never set bale_av1_decoder). rav1g decodes AV1 in D3D12 compute shaders on the fork's GpuDecoderSampleProvider
/// (wrapper 2.1.0-bale.44; owner-approved 2026-11-18 for testing). Only software AV1 routes change: see
/// Rav1gPolicy.Route, FfmpegSource.ApplyRav1g and docs/player-plan.md, "rav1g (debug)".
/// </summary>
internal static class DebugAv1DecoderSwitch
{
    private static string _mode = "dav1d";

    internal static string Mode => Volatile.Read(ref _mode);

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("dav1d" or "rav1g")) return false;
        Volatile.Write(ref _mode, mode);
        return true;
    }
}

/// <summary>
/// The fork's "Bale rav1g:" log lines (BaleRav1g.h, GpuDecoderSampleProvider.h) for the stats row. FFmpeg's log
/// provider is installed only in debug builds (FfmpegLogging), on native threads; lines are fixed diagnostics.
/// The mount that configures rav1g resets it, so the row reads that open's lines (one rav1g stream at a time).
/// </summary>
internal static class DebugRav1gLog
{
    private static readonly object Gate = new();
    private static string? _loaded;
    private static string? _active;
    private static string? _fallback;
    private static string? _failed;
    private static string? _output;

    /// <summary>Clears the per-stream lines; the library's one "loaded" line per process stays.</summary>
    internal static void Reset()
    {
        lock (Gate) _active = _fallback = _failed = _output = null;
    }

    /// <summary>Never throws: called from the native log callback.</summary>
    internal static void Observe(string? message)
    {
        if (message is null || !message.StartsWith(Rav1gPolicy.LogPrefix, StringComparison.Ordinal)) return;
        try
        {
            if (Rav1gPolicy.ParseLog(message) is not { } line) return;
            lock (Gate)
            {
                switch (line.Kind)
                {
                    case "loaded": _loaded = line.Detail; break;
                    case "active": _active = line.Detail; break;
                    // An open that fell back has no stream failure of its own; the first of each is the cause.
                    case "fallback": _fallback ??= line.Detail; break;
                    case "failed": _failed ??= line.Detail; break;
                    case "output": _output = line.Detail; break;
                }
            }
        }
        catch { }
    }

    internal static (string? Loaded, string? Active, string? Fallback, string? Failed, string? Output) Snapshot()
    {
        lock (Gate) return (_loaded, _active, _fallback, _failed, _output);
    }
}

/// <summary>
/// Memory-only rav1g provider knobs for console tests (fork 2.1.0-bale.52; the fork's bale/README.md rav1g sections),
/// applied only on the rav1g route and reset by an app restart: <c>bale_gpu_decode_inflight</c> (the feeder thread's
/// units in flight, 1-8, fork default 3), <c>bale_rav1g_perf=1</c> (rav1g's own CPU/GPU ms in the delivery line) and
/// <c>bale_rav1g_parse</c> (frames in parse, 1-2; try 2 only when the decode call is still the limit). Unset = the
/// fork's defaults.
/// </summary>
internal static class DebugRav1gTuning
{
    private static readonly object Gate = new();
    private static int? _inflight;
    private static bool? _perf;
    private static int? _parse;

    /// <summary>Applies one key=value; "default" clears it. False (and no change) for an unknown key or bad value.</summary>
    internal static bool TrySet(string key, string? value)
    {
        lock (Gate)
        {
            var clear = value == "default";
            switch (key)
            {
                case "inflight":
                    if (clear) { _inflight = null; return true; }
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var inflight) || inflight is < 1 or > 8) return false;
                    _inflight = inflight;
                    return true;
                case "perf":
                    if (clear) { _perf = null; return true; }
                    if (value is not ("on" or "off")) return false;
                    _perf = value == "on";
                    return true;
                case "parse":
                    if (clear) { _parse = null; return true; }
                    if (value is not ("1" or "2")) return false;
                    _parse = value == "2" ? 2 : 1;
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>The fork's option keys for the current knobs (only those set).</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> Options()
    {
        lock (Gate)
        {
            var options = new List<KeyValuePair<string, string>>(3);
            if (_inflight is { } inflight) options.Add(new("bale_gpu_decode_inflight", inflight.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (_perf is { } perf) options.Add(new("bale_rav1g_perf", perf ? "1" : "0"));
            if (_parse is { } parse) options.Add(new("bale_rav1g_parse", parse.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return options;
        }
    }

    internal static string Describe()
    {
        lock (Gate)
            return "rav1g-tune inflight=" + (_inflight?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default")
                + " perf=" + (_perf is { } perf ? perf ? "on" : "off" : "default")
                + " parse=" + (_parse?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default");
    }
}
#endif
