#if DEBUG_SERVER
using Bale.Core.Playback;
using System.Globalization;

namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only software AV1 decoder, captured per playback attempt. Debug builds start on rav1g (owner, 2026-11-19:
/// rav1g is the default for testing); `av1-decoder dav1d` switches back until restart. Release builds never set
/// bale_av1_decoder, so they keep dav1d. rav1g decodes AV1 in D3D12 compute shaders on the fork's GpuDecoderSampleProvider
/// (wrapper 2.1.0-bale.44; owner-approved 2026-11-18 for testing). Only software AV1 routes change: see
/// Rav1gPolicy.Route, FfmpegSource.ApplyRav1g and docs/player-plan.md, "rav1g (debug)".
/// </summary>
internal static class DebugAv1DecoderSwitch
{
    private static string _mode = "rav1g";

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

/// <summary>Only the fork's fixed numeric diagnostics, per source. Missing values stay unavailable, never zero.</summary>
internal static class DebugDecoderLog
{
    private static readonly object Gate = new();
    private static DebugDecoderSnapshot _value;
    // Delivery windows are 240 samples except the last partial one at teardown; weight each window's rate by
    // its samples so the playback's rate is total frames over total time, not whichever window came last.
    private static double _rateSamples, _rateSeconds;

    internal static void Reset() { lock (Gate) { _value = default; _rateSamples = 0; _rateSeconds = 0; } }
    internal static DebugDecoderSnapshot Snapshot() { lock (Gate) return _value; }

    internal static void Observe(string? message)
    {
        if (message is null) return;
        lock (Gate)
        {
            if (message.StartsWith("Bale rav1g: delivery ", StringComparison.Ordinal)
                && Number(message, "rate=") is { } rate)
            {
                if (Counter(message, "samples=") is { } samples and > 0 && rate > 0)
                {
                    _rateSamples += samples;
                    _rateSeconds += samples / rate;
                    rate = _rateSamples / _rateSeconds;
                }
                _value = _value with { DeliveryRate = rate };
            }
            if (message.StartsWith("Bale rav1g: destroyed (", StringComparison.Ordinal)
                && Counter(message, "discarded=") is { } discarded)
                _value = _value with { Discarded = discarded };
            if (message.StartsWith("Bale decode-ahead timing:", StringComparison.Ordinal)
                && Counter(message, "starved=") is { } starved)
                _value = _value with { Starved = starved };
        }
    }

    private static ReadOnlySpan<char> Token(string text, string key)
    {
        var at = text.IndexOf(key, StringComparison.Ordinal);
        if (at < 0 || at > 0 && text[at - 1] is not (' ' or '(')) return default;
        var value = text.AsSpan(at + key.Length);
        var length = 0;
        while (length < value.Length && (char.IsAsciiDigit(value[length]) || value[length] == '.')) length++;
        if (length < value.Length && value[length] is not (' ' or ')' or '/' or '\r' or '\n')) return default;
        return value[..length];
    }

    private static double? Number(string text, string key) => double.TryParse(Token(text, key), NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : null;

    private static ulong? Counter(string text, string key) => ulong.TryParse(Token(text, key), NumberStyles.None,
        CultureInfo.InvariantCulture, out var value) ? value : null;
}

internal readonly record struct DebugDecoderSnapshot(double? DeliveryRate, ulong? Discarded, ulong? Starved)
{
    internal string DecodeRateText(double? framesPerSecond) => (DeliveryRate?.ToString("0.0", CultureInfo.InvariantCulture) ?? "unavailable")
        + " / " + (framesPerSecond is > 0 and { } fps && double.IsFinite(fps) ? fps.ToString("0.###", CultureInfo.InvariantCulture) : "?") + " fps";
    internal string DiscardedText => Discarded?.ToString(CultureInfo.InvariantCulture) ?? "unavailable (on decoder close)";
    internal string StarvedText => Starved?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
}

/// <summary>
/// Memory-only rav1g provider knobs for console tests (fork 2.1.0-bale.52; the fork's bale/README.md rav1g sections),
/// applied only on the rav1g route and reset by an app restart: <c>bale_gpu_decode_inflight</c> (the feeder thread's
/// units in flight, 1-8, fork default 3), <c>bale_rav1g_perf=1</c> (rav1g's own CPU/GPU ms in the delivery line; <c>perf=frames</c> sets 2, bale.72: one
/// `Bale rav1g stage u= pts= take= submit= release= gpudone=` line per frame in QPC ms) and
/// <c>bale_rav1g_parse</c> (frames in parse, 1-2; try 2 only when the decode call is still the limit) and
/// <c>bale_gpu_output_textures</c> (bale.56: player-side texture cap, 0 = none) and <c>bale_gpu_output_images</c>
/// (bale.70: rav1g's output-image cap, 0-16, fork default 10). Unset = the
/// fork's defaults.
/// </summary>
internal static class DebugRav1gTuning
{
    private static readonly object Gate = new();
    private static int? _inflight;
    private static int? _perf;
    private static int? _parse;
    private static int? _outputTextures;
    private static int? _outputImages;

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
                    if (value is not ("on" or "off" or "frames")) return false;
                    _perf = value switch { "frames" => 2, "on" => 1, _ => 0 };
                    return true;
                case "outtex":
                    // bale_gpu_output_textures (fork bale.56): 0 = no limit, 1-3 act as 4, up to 64.
                    if (clear) { _outputTextures = null; return true; }
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var textures) || textures > 64) return false;
                    _outputTextures = textures;
                    return true;
                case "images":
                    // bale_gpu_output_images (fork bale.70): rav1g's output-image cap, 0-16 (fork default 10, 1-3 act as 4).
                    if (clear) { _outputImages = null; return true; }
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var images) || images > 16) return false;
                    _outputImages = images;
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
            var options = new List<KeyValuePair<string, string>>(4);
            if (_inflight is { } inflight) options.Add(new("bale_gpu_decode_inflight", inflight.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (_perf is { } perf) options.Add(new("bale_rav1g_perf", perf.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (_parse is { } parse) options.Add(new("bale_rav1g_parse", parse.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (_outputTextures is { } textures) options.Add(new("bale_gpu_output_textures", textures.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (_outputImages is { } images) options.Add(new("bale_gpu_output_images", images.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return options;
        }
    }

    internal static string Describe()
    {
        lock (Gate)
            return "rav1g-tune inflight=" + (_inflight?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default")
                + " perf=" + (_perf switch { 2 => "frames", 1 => "on", 0 => "off", _ => "default" })
                + " parse=" + (_parse?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default")
                + " outtex=" + (_outputTextures?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default")
                + " images=" + (_outputImages?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default");
    }
}
#endif
