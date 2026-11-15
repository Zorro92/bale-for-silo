using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// Samples cumulative network counters once a second, keeping the last five samples. Idle playback
/// from cache expires the estimate instead of misreporting zero as the network's capacity.
/// Implemented in Rust (native/bale-core/src/meters.rs).
/// </summary>
public sealed class PlaybackThroughputMeter
{
    private readonly ThroughputHandle _handle;

    public PlaybackThroughputMeter()
    {
        BaleCoreMeters.EnsureLoaded();
        _handle = BaleCoreMeters.bale_core_throughput_create();
    }

    /// <summary>Bits per second over the window, or null when nothing moved.</summary>
    public long? Sample(long downloadedBytes, TimeSpan downloadTime) =>
        BaleCoreMeters.bale_core_throughput_sample(_handle, downloadedBytes, downloadTime.Ticks) is var bps
            && bps != BaleCoreMeters.None ? bps : null;
}
