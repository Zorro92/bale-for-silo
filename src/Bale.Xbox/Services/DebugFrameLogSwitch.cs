#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only frame log switch (bale-debug.sh frame-log), captured per playback attempt; off after restart. On, the
/// native sample observer writes one bale.log line per record and the engine one per playback event
/// (<see cref="DebugFrameLog"/>), to join with the FFmpegInteropX fork's per-frame lines.
/// </summary>
internal static class DebugFrameLogSwitch
{
    private static string _mode = "off";

    internal static string Mode => Volatile.Read(ref _mode);

    internal static bool Enabled => Mode == "on";

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("on" or "off")) return false;
        Volatile.Write(ref _mode, mode);
        return true;
    }
}
#endif
