#if DEBUG_SERVER
using System.Globalization;

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

    internal static string Failure(string step, int hresult) => $"{step}: 0x{unchecked((uint)hresult):X8}";

    internal static double Ratio(TimeSpan start, TimeSpan end, TimeSpan wall) =>
        wall > TimeSpan.Zero ? (end - start).TotalSeconds / wall.TotalSeconds : 0;

    internal static TimeSpan CpuTime(long kernelTicks, long userTicks) => TimeSpan.FromTicks(checked(kernelTicks + userTicks));

    internal static string Measurements(TimeSpan start, TimeSpan end, TimeSpan wall, int buffering,
        TimeSpan? cpuStart, TimeSpan? cpuEnd, TimeSpan maximum, string playbackState)
    {
        var cpu = CpuCores(cpuStart, cpuEnd, wall);
        return string.Create(CultureInfo.InvariantCulture,
            $"PlaybackState: {playbackState}\nPosition: start={start.TotalSeconds:0.###}s end={end.TotalSeconds:0.###}s"
            + $" max={maximum.TotalSeconds:0.###}s wall={wall.TotalSeconds:0.###}s ratio={Ratio(start, maximum, wall):0.###}\n"
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

    internal static string Verdict(bool opened, TimeSpan start, TimeSpan end, TimeSpan wall, IEnumerable<string> failures)
    {
        var basic = DebugPlaytest.Verdict(opened, start, end, failures);
        if (!basic.StartsWith("Verdict: PLAYS", StringComparison.Ordinal)) return basic;
        var ratio = Ratio(start, end, wall);
        return ratio < 0.9 ? "Verdict: SLOW - below real time (ratio < 0.9)\n"
            : ratio > 1.1 ? "Verdict: PLAYS - clock faster than real time (ratio > 1.1)\n"
            : "Verdict: PLAYS - real time (ratio 0.9–1.1)\n";
    }
}
#endif
