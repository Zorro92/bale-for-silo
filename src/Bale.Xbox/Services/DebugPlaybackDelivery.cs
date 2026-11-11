#if DEBUG_SERVER
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>Memory-only HLS reproduction switch; captured once per attempt, absent in production.</summary>
internal static class DebugPlaybackDelivery
{
    private static string _mode = "auto";

    internal static string Mode => Volatile.Read(ref _mode);

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("auto" or "hls")) return false;
        Interlocked.Exchange(ref _mode, mode);
        return true;
    }

    internal static PlaybackStartRequestV3 Apply(string mode, PlaybackStartRequestV3 start) => mode != "hls"
        ? start
        : start with
        {
            ClientPlaybackContext = start.ClientPlaybackContext with
            {
                // Keep tracks, start position, decode evidence and the HLS recipe claims intact.
                // Disable every non-HLS delivery, including progressive if it is enabled later.
                Deliveries = start.ClientPlaybackContext.Deliveries.ToDictionary(pair => pair.Key, pair => pair.Key == "hls"
                    ? pair.Value
                    : pair.Value with { Enabled = false, SupportedOnDevice = false }),
            },
        };
}
#endif
