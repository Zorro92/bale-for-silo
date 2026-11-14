#if DEBUG_SERVER
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>Memory-only HLS/progressive reproduction switch; captured once per attempt, absent in production.</summary>
internal static class DebugPlaybackDelivery
{
    private static string _mode = "auto";

    internal static string Mode => Volatile.Read(ref _mode);

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("auto" or "hls" or "progressive")) return false;
        Interlocked.Exchange(ref _mode, mode);
        return true;
    }

    /// <summary>
    /// hls or progressive keeps only that delivery class enabled. Tracks, start position, decode
    /// evidence and that class's claims stay intact; a file the forced class cannot carry gets the
    /// server's terminal answer instead of quietly using another delivery.
    /// </summary>
    internal static PlaybackStartRequestV3 Apply(string mode, PlaybackStartRequestV3 start) => mode is not ("hls" or "progressive")
        ? start
        : start with
        {
            ClientPlaybackContext = start.ClientPlaybackContext with
            {
                Deliveries = start.ClientPlaybackContext.Deliveries.ToDictionary(pair => pair.Key, pair => pair.Key == mode
                    ? pair.Value
                    : pair.Value with { Enabled = false, SupportedOnDevice = false }),
            },
        };
}
#endif
