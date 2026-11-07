#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>Memory-only content matching, captured for the next playback attempt; off after restart.</summary>
internal static class DebugDisplayMatch
{
    private static int _enabled;

    internal static bool Enabled => Volatile.Read(ref _enabled) != 0;
    internal static string Mode => Enabled ? "on" : "off";

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("on" or "off")) return false;
        Interlocked.Exchange(ref _enabled, mode == "on" ? 1 : 0);
        return true;
    }
}
#endif
