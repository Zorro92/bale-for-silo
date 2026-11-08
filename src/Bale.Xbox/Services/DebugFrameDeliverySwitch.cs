#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>Memory-only observer mode, captured per playback attempt; native after restart.</summary>
internal static class DebugFrameDeliverySwitch
{
    private static string _mode = "native";

    internal static string Mode => Volatile.Read(ref _mode);

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("native" or "managed" or "off" or "on")) return false;
        Interlocked.Exchange(ref _mode, mode == "on" ? "native" : mode);
        return true;
    }
}
#endif
