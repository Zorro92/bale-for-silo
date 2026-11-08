#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>Memory-only AV1 claim bound, captured per playback attempt; 1080p after restart.</summary>
internal static class DebugAv1ClaimSwitch
{
    private static int _maxHeight = 1080;

    internal static int MaxHeight => Volatile.Read(ref _maxHeight);

    internal static bool TrySet(string? height)
    {
        if (height is not ("1080" or "2160")) return false;
        Interlocked.Exchange(ref _maxHeight, height == "2160" ? 2160 : 1080);
        return true;
    }
}
#endif
