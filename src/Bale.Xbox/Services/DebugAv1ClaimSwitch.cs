#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only AV1 claim bound, captured per playback attempt. Debug builds start at 2160 (owner, 2026-11-19: 4K AV1
/// on rav1g for testing); `av1-claim 1080` lowers it until restart. Release builds claim 1080p60.
/// </summary>
internal static class DebugAv1ClaimSwitch
{
    private static int _maxHeight = 2160;

    internal static int MaxHeight => Volatile.Read(ref _maxHeight);

    internal static bool TrySet(string? height)
    {
        if (height is not ("1080" or "2160")) return false;
        Interlocked.Exchange(ref _maxHeight, height == "2160" ? 2160 : 1080);
        return true;
    }
}
#endif
