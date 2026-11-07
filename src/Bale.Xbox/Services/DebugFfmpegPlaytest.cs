#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>Debug-only probe policy and invariant output, testable without WinRT.</summary>
internal static class DebugFfmpegPlaytest
{
    internal const int CreateTimeoutMs = 30000;
    internal const int OpenTimeoutMs = 15000;

    internal static bool TryParse(string? url, string? seconds, string? mode, out Uri uri,
        out int duration, out string decoderMode, out string error)
    {
        decoderMode = mode ?? "auto";
        if (!DebugPlaytest.TryParse(url, seconds, out uri, out duration, out error, maxDuration: 60)) return false;
        if (decoderMode is "auto" or "software" or "system") return true;
        error = "mode must be auto, software or system";
        return false;
    }

    internal static bool TryParseTuning(string? threads, string? output10bit, string? rate,
        string? ahead, string? sws, string? delay, string? prio, string? maxres, string? dpb, out DebugFfmpegTuning tuning, out string error)
    {
        tuning = new();
        error = string.Empty;
        uint? decoderThreads = null;
        bool? allow10bit = null;
        var playbackRate = 1.0;
        if (threads is not null)
        {
            if (!uint.TryParse(threads, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count > 16)
            {
                error = "threads must be an integer from 0 to 16";
                return false;
            }
            decoderThreads = count;
        }
        if (output10bit is not null)
        {
            if (output10bit is not ("on" or "off"))
            {
                error = "10bit must be on or off";
                return false;
            }
            allow10bit = output10bit == "on";
        }
        if (rate is not null && (!double.TryParse(rate, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out playbackRate)
            || !double.IsFinite(playbackRate) || playbackRate is < 0.5 or > 2.0))
        {
            error = "rate must be a decimal from 0.5 to 2.0";
            return false;
        }
        if (!TryNativeOption(ahead, 0, 64, out var decodeAheadFrames))
        {
            error = "ahead must be an integer from 0 to 64";
            return false;
        }
        if (!TryNativeOption(sws, 1, 16, out var swsThreads))
        {
            error = "sws must be an integer from 1 to 16";
            return false;
        }
        if (!TryNativeOption(delay, 0, 64, out var dav1dMaxFrameDelay))
        {
            error = "delay must be an integer from 0 to 64";
            return false;
        }
        if (!TryNativeOption(prio, 0, 2, out var decodeThreadPriority))
        {
            error = "prio must be an integer from 0 to 2";
            return false;
        }
        if (!TryNativeOption(maxres == "default" ? null : maxres, 0, 1, out var decoderUseMaxResolution))
        {
            error = "maxres must be an integer from 0 to 1 or default";
            return false;
        }
        if (!TryNativeOption(dpb == "default" ? null : dpb, 0, 16, out var decoderMaxDpbCount))
        {
            error = "dpb must be an integer from 0 to 16 or default";
            return false;
        }
        tuning = new(decoderThreads, allow10bit, playbackRate, decodeAheadFrames, swsThreads, dav1dMaxFrameDelay, decodeThreadPriority,
            decoderUseMaxResolution, decoderMaxDpbCount);
        return true;
    }

    /// <summary>hdr=on|off forces FFmpegInteropX HdrSupport Enabled/Disabled; default or omitted keeps Automatic.</summary>
    internal static bool TryParseHdr(string? hdr, out bool? enabled, out string error)
    {
        error = string.Empty;
        enabled = hdr switch { "on" => true, "off" => false, _ => null };
        if (hdr is null or "default" or "on" or "off") return true;
        error = "hdr must be on, off or default";
        return false;
    }

    /// <summary>grain=off sets bale_dav1d_export_film_grain=1 (dav1d skips grain synthesis; needs wrapper 2.1.0-bale.5);
    /// on, default or omitted keeps dav1d's normal grain synthesis.</summary>
    internal static bool TryParseGrain(string? grain, out bool? synthesize, out string error)
    {
        error = string.Empty;
        synthesize = grain switch { "on" => true, "off" => false, _ => null };
        if (grain is null or "default" or "on" or "off") return true;
        error = "grain must be on, off or default";
        return false;
    }

    private static bool TryNativeOption(string? value, int minimum, int maximum, out int? number)
    {
        number = null;
        if (value is null) return true;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum || parsed > maximum)
            return false;
        number = parsed;
        return true;
    }

    internal static string Settings(uint threads, bool allow10bit, int processorCount, DebugFfmpegTuning tuning, string mode)
    {
        var decode = tuning.DecodeTuning(mode);
        return string.Create(CultureInfo.InvariantCulture,
            $"Threads: {threads} (0=logical CPU count; {(tuning.Threads is null ? "default" : "override")})\n"
            + $"10-bit output: {(allow10bit ? "on" : "off")} ({(tuning.Allow10bit is null ? "default" : "override")})\n"
            + $"Logical CPUs: {processorCount}\nRate requested: {tuning.Rate:0.###}\n"
            + $"Decode ahead: {decode.DecodeAheadFrames ?? 0} (0=off; {(tuning.DecodeAheadFrames is null ? "default" : "override")})\n"
            + $"SWS threads: {decode.SwsThreads ?? 1} (1=upstream; {(tuning.SwsThreads is null ? "default" : "override")})\n"
            + $"dav1d max frame delay: {decode.Dav1dMaxFrameDelay ?? 0} (0=dav1d default; {(tuning.Dav1dMaxFrameDelay is null ? "default" : "override")})\n"
            + $"Decode thread priority: {tuning.DecodeThreadPriority ?? 0} (0=off, 1=above normal, 2=highest; {(tuning.DecodeThreadPriority is null ? "default" : "override")})\n"
            + $"Decoder use max resolution: {tuning.DecoderUseMaxResolution ?? 0} (0=unset, 1=on; {(tuning.DecoderUseMaxResolution is null ? "default" : "override")}; system video only)\n"
            + $"Decoder max DPB count: {tuning.DecoderMaxDpbCount ?? 0} (0=unset; {(tuning.DecoderMaxDpbCount is null ? "default" : "override")}; system video only)\n"
            + $"HDR support: {tuning.HdrSupport switch { true => "on (override)", false => "off (override)", null => "automatic (default)" }}\n"
            + $"Film grain: {tuning.FilmGrain switch { true => "synthesized (override)", false => "exported, not applied (override; libdav1d only)", null => "synthesized (default)" }}\n");
    }

    internal static string FrameDelivery(PlaybackFrameDeliverySnapshot? frames)
    {
        if (frames is null)
            return "Late frames total: unavailable\nTotal frames delivered: unavailable\nDelivered ahead: min=unavailable median=unavailable\n";
        var minimum = frames.MinDeliveredAheadSeconds is { } min ? min.ToString("0.###", CultureInfo.InvariantCulture) + "s" : "unavailable";
        var median = frames.MedianDeliveredAheadSeconds is { } middle ? middle.ToString("0.###", CultureInfo.InvariantCulture) + "s" : "unavailable";
        return string.Create(CultureInfo.InvariantCulture,
            $"Late frames total: {frames.LateFrames}\nTotal frames delivered: {frames.TotalFramesDelivered}\n"
            + $"Delivered ahead: min={minimum} median={median} (at delivery; not renderer queue depth)\n");
    }

    internal static string Failure(string step, int hresult) => $"{step}: 0x{unchecked((uint)hresult):X8}";

    internal static double Ratio(TimeSpan start, TimeSpan end, TimeSpan wall, double rate = 1.0) =>
        wall > TimeSpan.Zero && double.IsFinite(rate) && rate > 0 ? (end - start).TotalSeconds / (wall.TotalSeconds * rate) : 0;

    internal static TimeSpan CpuTime(long kernelTicks, long userTicks) => TimeSpan.FromTicks(checked(kernelTicks + userTicks));

    internal static string Measurements(TimeSpan start, TimeSpan end, TimeSpan wall, int buffering,
        TimeSpan? cpuStart, TimeSpan? cpuEnd, TimeSpan maximum, string playbackState, double rate = 1.0)
    {
        var cpu = CpuCores(cpuStart, cpuEnd, wall);
        return string.Create(CultureInfo.InvariantCulture,
            $"PlaybackState: {playbackState}\nPosition: start={start.TotalSeconds:0.###}s end={end.TotalSeconds:0.###}s"
            + $" max={maximum.TotalSeconds:0.###}s wall={wall.TotalSeconds:0.###}s expected={wall.TotalSeconds * rate:0.###}s ratio={Ratio(start, maximum, wall, rate):0.###}\n"
            + $"Buffering entries: {buffering}\nCPU: {cpu}\n");
    }

    internal static string CpuCores(TimeSpan? start, TimeSpan? end, TimeSpan wall)
    {
        if (start is not { } first || end is not { } last || last < first || wall <= TimeSpan.Zero) return "unavailable";
        // A zero delta on an active player can mean unsupported/stale platform counters, not zero CPU usage.
        if (last == first) return "unavailable (process times did not advance)";
        return string.Create(CultureInfo.InvariantCulture, $"{(last - first).TotalSeconds / wall.TotalSeconds:0.000} cores");
    }

    internal static string CpuUsage(TimeSpan? start, TimeSpan? end, TimeSpan wall, int processorCount)
    {
        var cores = CpuCores(start, end, wall);
        if (processorCount <= 0) return cores + " · CPU count unavailable";
        var percent = start is { } first && end is { } last && last > first && wall > TimeSpan.Zero
            ? ((last - first).TotalSeconds / wall.TotalSeconds / processorCount * 100).ToString("0.0", CultureInfo.InvariantCulture) + "% of "
            : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{cores} · {percent}{processorCount} CPUs");
    }

    internal static string Verdict(bool opened, TimeSpan start, TimeSpan end, TimeSpan wall, IEnumerable<string> failures, double rate = 1.0)
    {
        var basic = DebugPlaytest.Verdict(opened, start, end, failures);
        if (!basic.StartsWith("Verdict: PLAYS", StringComparison.Ordinal)) return basic;
        var ratio = Ratio(start, end, wall, rate);
        var target = rate == 1.0 ? "real time" : "target rate";
        return ratio < 0.9 ? $"Verdict: SLOW - below {target} (ratio < 0.9)\n"
            : ratio > 1.1 ? $"Verdict: PLAYS - clock faster than {target} (ratio > 1.1)\n"
            : $"Verdict: PLAYS - {target} (ratio 0.9–1.1)\n";
    }
}

// Nullable settings use release software-video defaults or the wrapper's defaults for other modes.
internal sealed record DebugFfmpegTuning(uint? Threads = null, bool? Allow10bit = null, double Rate = 1.0,
    int? DecodeAheadFrames = null, int? SwsThreads = null, int? Dav1dMaxFrameDelay = null, int? DecodeThreadPriority = null,
    int? DecoderUseMaxResolution = null, int? DecoderMaxDpbCount = null, bool? HdrSupport = null, bool? FilmGrain = null)
{
    internal SoftwareDecodeTuning DecodeTuning(string mode)
    {
        // URI probes have no playback plan/dimensions before creation, so leave the size-dependent delay unset.
        var defaults = SoftwareDecodeTuning.ForVideo(mode == "software");
        return new(DecodeAheadFrames ?? defaults.DecodeAheadFrames, SwsThreads ?? defaults.SwsThreads,
            Dav1dMaxFrameDelay ?? defaults.Dav1dMaxFrameDelay);
    }
}
#endif
