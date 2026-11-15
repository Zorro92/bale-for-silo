using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// Bounded delivery diagnostics correlating late samples with stream waits, GC pauses and delivery
/// gaps. Classes describe concurrent evidence, not proven causes or renderer drops. Implemented in
/// Rust (native/bale-core/src/frames.rs); thread-safe.
/// </summary>
public sealed unsafe class PlaybackLateFrameTrace
{
    private readonly TraceHandle _handle;

    public PlaybackLateFrameTrace()
    {
        BaleCoreMeters.EnsureLoaded();
        _handle = BaleCoreMeters.bale_core_trace_create();
    }

    /// <param name="nowMilliseconds">Monotonic wall time; the first sample establishes the trace start.</param>
    /// <param name="late">Whether the delivery meter counted this sample as late.</param>
    /// <param name="gcPauseTotal">Cumulative pause duration supplied by the caller, not read by Core.</param>
    public void Sample(double nowMilliseconds, TimeSpan timestamp, TimeSpan duration, TimeSpan position,
        bool late, long? demandWaits, TimeSpan? demandWaitTime, TimeSpan gcPauseTotal, int gen0, int gen1, int gen2)
    {
        var environment = BaleCoreMeters.ToNative(new PlaybackFrameEnvironmentSnapshot(nowMilliseconds, gcPauseTotal,
            gen0, gen1, gen2, demandWaits, demandWaitTime));
        BaleCoreMeters.bale_core_trace_sample(_handle, nowMilliseconds, timestamp.Ticks, duration.Ticks, position.Ticks,
            late ? (byte)1 : (byte)0, &environment);
    }

    public void Reset() => BaleCoreMeters.bale_core_trace_reset(_handle);

    /// <summary>A summary line, then one line per retained late event (the last 300).</summary>
    public string Describe()
    {
        byte* buffer = null;
        nuint length = 0;
        BaleCoreMeters.bale_core_trace_describe(_handle, &buffer, &length);
        return BaleCoreMeters.TakeText(buffer, length);
    }
}
