#if DEBUG_SERVER
using System.Globalization;
using System.Runtime.InteropServices;

namespace Bale.Xbox.Services;

/// <summary>Independent snapshot ABI: never enters FFmpegInteropX or queries D3D from the UI thread.</summary>
internal sealed class DebugStageGpuTiming(ulong? baselineSession, bool requested)
{
    // Event-query timing (2026-11-24, docs/gpu-tone-map.md): slot 0 is the total, an upper bound of
    // the stage's elapsed GPU time; slot 1 its end resolution. Slots 2-3 and Disjoint stay zero (they
    // held input copy/luma/chroma and disjoint counts under the timestamp queries).
    [StructLayout(LayoutKind.Sequential)]
    internal struct Stats
    {
        public ulong Session, Window;
        public uint State, Frames, Valid, Disjoint, Unavailable, Pending;
        public int Error;
        public uint Reserved;
        public double TotalAvg, ResolutionAvg, UnusedAvg2, UnusedAvg3;
        public double TotalMax, ResolutionMax, UnusedMax2, UnusedMax3;
    }

    private ulong _lastSession, _lastWindow;
    private ulong _disabledSession;
    private string _row = !requested ? "off (next playback: gpu-timing on)" : baselineSession is null
        ? "unavailable (native snapshot busy at mount)" : "waiting for a measured stage";
    internal static bool Enabled { get; private set; }
    internal static bool TryParse(string? mode, out bool enabled)
    {
        enabled = mode == "on";
        return mode is "on" or "off";
    }

    internal static string? Set(string? mode)
    {
        if (!TryParse(mode, out var enabled)) return "expected on|off";
        try
        {
            var hr = bale_tonemap_debug_timing_enable(enabled ? 1u : 0u);
            if (hr < 0) return string.Create(CultureInfo.InvariantCulture, $"GPU timing unavailable (0x{hr:X8})");
            Enabled = enabled;
            return null;
        }
        catch (DllNotFoundException) { return "GPU timing unavailable (native DLL missing)"; }
        catch (EntryPointNotFoundException) { return "GPU timing unavailable (requires a debug native build with stage-timing)"; }
    }

    internal static DebugStageGpuTiming Capture() => new(Read()?.Session, Enabled);

    internal string ReadRow(out string? logLine)
    {
        logLine = null;
        if (!requested) return _row;
        return Read() is { } stats ? Accept(stats, out logLine) : _row;
    }

    // Separated from the P/Invoke so Linux tests exercise stale snapshots, log deduplication and formatting.
    internal string Accept(Stats stats, out string? logLine)
    {
        logLine = null;
        if (!requested || baselineSession is null || stats.Session <= baselineSession.Value) return _row;
        if (stats.State == 2)
        {
            if (_disabledSession != stats.Session)
            {
                logLine = "Bale stage gpu: " + Disabled(stats);
                _disabledSession = stats.Session;
            }
        }
        else if (stats.Window > 0 && (stats.Session != _lastSession || stats.Window != _lastWindow))
        {
            logLine = "Bale stage gpu: " + DescribeWindow(stats);
            _lastSession = stats.Session;
            _lastWindow = stats.Window;
        }
        var state = stats.State switch
        {
            2 => Disabled(stats),
            3 => "closed",
            _ => "collecting",
        };
        return _row = stats.Window == 0 ? $"{state} (window: 240 frames)" : $"{DescribeWindow(stats)} · {state}";
    }

    private static string Disabled(Stats stats)
    {
        // Additive reason codes in the ABI's reserved word. Old native DLLs leave it zero.
        var reason = stats.Reserved switch
        {
            1 => "query creation failed",
            2 => "query data size mismatch",
            3 => "GetData failed",
            4 => "zero QPC frequency",
            5 => "invalid event sequence",
            6 => "timing panic contained",
            7 => "context is not immediate",
            8 => "device failure",
            9 => "no event poll thread",
            10 => "event never completed",
            _ => "query failure",
        };
        return string.Create(CultureInfo.InvariantCulture, $"disabled ({reason}; query HRESULT 0x{stats.Error:X8})");
    }

    internal static string DescribeWindow(Stats stats)
    {
        // Only the total is measured: per-pass event boundaries would time the CPU's recording.
        var timing = stats.Valid == 0 ? "total unavailable (no completed event pairs)" :
            string.Create(CultureInfo.InvariantCulture,
                $"total avg/max {stats.TotalAvg:0.000}/{stats.TotalMax:0.000} ms (upper bound) · end resolution {stats.ResolutionAvg:0.000}/{stats.ResolutionMax:0.000} ms");
        return string.Create(CultureInfo.InvariantCulture,
            $"frames={stats.Frames} {timing} · valid={stats.Valid} unavailable={stats.Unavailable} pending={stats.Pending} · session={stats.Session} window={stats.Window}");
    }

    private static unsafe Stats? Read()
    {
        try
        {
            Stats stats = default;
            return bale_tonemap_debug_timing_read(&stats) == 0 ? stats : null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    // Pointer-only signatures: DisableRuntimeMarshalling=true, Native AOT and CA1420.
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern int bale_tonemap_debug_timing_enable(uint enabled);
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe int bale_tonemap_debug_timing_read(Stats* stats);
}
#endif
