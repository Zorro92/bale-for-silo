#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>Memory-only AV1 claim frame-rate bound, captured per playback attempt; 30 fps after restart.</summary>
internal static class DebugAv1FpsSwitch
{
    private static int _maxFps = 30;

    internal static int MaxFps => Volatile.Read(ref _maxFps);

    internal static bool TrySet(string? fps)
    {
        if (fps is not ("30" or "60")) return false;
        Interlocked.Exchange(ref _maxFps, fps == "60" ? 60 : 30);
        return true;
    }
}
#endif
