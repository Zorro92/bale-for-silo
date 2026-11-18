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
#endif
