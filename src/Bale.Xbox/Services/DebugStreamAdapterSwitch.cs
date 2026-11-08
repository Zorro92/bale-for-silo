#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>Memory-only stream adapter switch, captured per playback attempt; pooled after restart.</summary>
internal static class DebugStreamAdapterSwitch
{
    private static int _pooled = 1;

    internal static bool Pooled => Volatile.Read(ref _pooled) != 0;
    internal static string Mode => Pooled ? "pooled" : "builtin";

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("pooled" or "builtin")) return false;
        Interlocked.Exchange(ref _pooled, mode == "pooled" ? 1 : 0);
        return true;
    }
}
#endif
