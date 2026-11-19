#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only engine output-depth ceiling for the next mount; display after restart, as in release builds.
/// source keeps P010 at rgb10 regardless of HDMI depth. Debug engine-target still wins over either mode.
/// </summary>
internal static class DebugOutputDepthSwitch
{
    private static string _mode = "display";

    internal static string Mode => Volatile.Read(ref _mode);

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("display" or "source")) return false;
        Volatile.Write(ref _mode, mode);
        return true;
    }
}
#endif
