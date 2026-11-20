#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only AV1 claim bound, captured per playback attempt. Debug builds start at 2160 (owner, 2026-11-19: 4K AV1
/// on rav1g for testing, ignored with dav1d which stays at 1080); `av1-claim 1080` lowers it until restart. `2160-dav1d`
/// also lifts dav1d's 1080p cap, for memory/stress tests only (dav1d is not expected to play 4K smoothly). Release builds claim 1080p60.
/// </summary>
internal static class DebugAv1ClaimSwitch
{
    private static int _maxHeight = 2160;
    private static int _dav1d;

    internal static int MaxHeight => Volatile.Read(ref _maxHeight);

    /// <summary>True only after `2160-dav1d`: the 2160 claim then applies with dav1d too.</summary>
    internal static bool Dav1dToo => Volatile.Read(ref _dav1d) != 0;

    internal static string Status => Dav1dToo ? "2160-dav1d" : MaxHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static bool TrySet(string? height)
    {
        if (height is not ("1080" or "2160" or "2160-dav1d")) return false;
        // Two fields: a concurrent reader may briefly see the new height with the old dav1d flag; the
        // next attempt captures both, which is all a debug switch needs.
        Interlocked.Exchange(ref _dav1d, height == "2160-dav1d" ? 1 : 0);
        Interlocked.Exchange(ref _maxHeight, height == "1080" ? 1080 : 2160);
        return true;
    }
}
#endif
