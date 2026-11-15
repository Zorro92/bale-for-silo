using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// Observes decoded video sample delivery, not rendering. A late frame has a presentation timestamp
/// behind the player clock when delivered. Display cadence and actual renderer drops are not measured.
/// Implemented in Rust (native/bale-core/src/frames.rs); thread-safe.
/// </summary>
public sealed unsafe class PlaybackFrameDeliveryMeter
{
    public const int RecentWindowMilliseconds = 10_000;
    private readonly DeliveryHandle _handle;

    // Retain per-frame horizons only for a bounded debug probe, never the long-running player.
    public PlaybackFrameDeliveryMeter(bool collectAheadStatistics = false)
    {
        BaleCoreMeters.EnsureLoaded();
        _handle = BaleCoreMeters.bale_core_delivery_create(collectAheadStatistics ? (byte)1 : (byte)0);
    }

    public void SetSeeking(bool seeking) => BaleCoreMeters.bale_core_delivery_set_seeking(_handle, seeking ? (byte)1 : (byte)0);

    public void InvalidateTimeline() => BaleCoreMeters.bale_core_delivery_invalidate(_handle);

    /// <param name="nowMilliseconds">Monotonic wall time, shared with Read.</param>
    /// <returns>Whether this sample counted as a late frame.</returns>
    public bool Observe(TimeSpan timestamp, TimeSpan duration, TimeSpan position, bool playing,
        bool discontinuous, long nowMilliseconds) =>
        BaleCoreMeters.bale_core_delivery_observe(_handle, timestamp.Ticks, duration.Ticks, position.Ticks,
            playing ? (byte)1 : (byte)0, discontinuous ? (byte)1 : (byte)0, nowMilliseconds) != 0;

    public PlaybackFrameDeliverySnapshot? Read(TimeSpan position, long nowMilliseconds)
    {
        NativeDeliverySnapshot native;
        return BaleCoreMeters.bale_core_delivery_read(_handle, position.Ticks, nowMilliseconds, &native) != 0
            ? BaleCoreMeters.FromNative(native) : null;
    }
}

/// <param name="DeliveredAheadSeconds">Last delivered sample end minus the clock, not a renderer queue depth.</param>
/// <param name="TotalFramesDelivered">Observed video samples, including preroll/non-playing delivery, excluding seeks.</param>
/// <param name="MinDeliveredAheadSeconds">Optional minimum horizon at delivery, with the same exclusions as late frames.</param>
/// <param name="MedianDeliveredAheadSeconds">Optional median horizon at delivery, not a renderer queue depth.</param>
public sealed record PlaybackFrameDeliverySnapshot(long LateFrames, int RecentLateFrames, double? DeliveredAheadSeconds,
    long TotalFramesDelivered = 0, double? MinDeliveredAheadSeconds = null, double? MedianDeliveredAheadSeconds = null)
{
    public double RecentLateFramesPerSecond => RecentLateFrames / (PlaybackFrameDeliveryMeter.RecentWindowMilliseconds / 1000.0);
}
