using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// Processes a native observer's ordered timeline without retaining WinRT objects. The caller
/// serializes snapshot writes, record processing and reads. Implemented in Rust
/// (native/bale-core/src/frames.rs: delivery meter, late-frame trace and snapshot ring in one handle).
/// </summary>
public sealed unsafe class PlaybackFrameRecordProcessor
{
    private readonly FramesHandle _handle;

    public PlaybackFrameRecordProcessor(int snapshotCapacity = 512, bool collectAheadStatistics = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(snapshotCapacity);
        BaleCoreMeters.EnsureLoaded();
        _handle = BaleCoreMeters.bale_core_frames_create((uint)snapshotCapacity, collectAheadStatistics ? (byte)1 : (byte)0);
    }

    public void AddSnapshot(in PlaybackFrameEnvironmentSnapshot snapshot)
    {
        var native = BaleCoreMeters.ToNative(snapshot);
        BaleCoreMeters.bale_core_frames_add_snapshot(_handle, &native);
    }

    /// <summary>
    /// Latest snapshot at or before delivery + 50 ms, allowing the managed sampling thread to resume
    /// just after a GC pause. If delivery predates retained history, use the oldest available baseline.
    /// </summary>
    public PlaybackFrameEnvironmentSnapshot SnapshotFor(double nowMilliseconds)
    {
        NativeEnvironment native;
        BaleCoreMeters.bale_core_frames_snapshot_for(_handle, nowMilliseconds, &native);
        return BaleCoreMeters.FromNative(native);
    }

    public void Process(in PlaybackFrameRecord record)
    {
        var native = new NativeFrameRecord
        {
            Kind = record.Kind, Flags = record.Flags, NowMilliseconds = record.NowMilliseconds,
            Timestamp = record.Timestamp, Duration = record.Duration, Position = record.Position,
        };
        BaleCoreMeters.bale_core_frames_process(_handle, &native, 1);
    }

    /// <param name="nowMilliseconds">The same Stopwatch/QPC-millisecond clock used by native records.</param>
    public PlaybackFrameDeliverySnapshot? Read(TimeSpan position, long nowMilliseconds)
    {
        NativeDeliverySnapshot native;
        return BaleCoreMeters.bale_core_frames_read(_handle, position.Ticks, nowMilliseconds, &native) != 0
            ? BaleCoreMeters.FromNative(native) : null;
    }

    public string DescribeLateFrameTrace()
    {
        byte* buffer = null;
        nuint length = 0;
        BaleCoreMeters.bale_core_frames_describe(_handle, &buffer, &length);
        return BaleCoreMeters.TakeText(buffer, length);
    }

    public void ResetLateFrameTrace() => BaleCoreMeters.bale_core_frames_reset_trace(_handle);
}

/// <param name="Kind">1 = video sample, 2 = Starting, 3 = seeking on, 4 = seeking off.</param>
/// <param name="Flags">Bit 0 = playing, bit 1 = discontinuous.</param>
/// <param name="NowMilliseconds">Stopwatch/QPC milliseconds, not Environment.TickCount64.</param>
/// <param name="Timestamp">Sample PTS in 100 ns ticks.</param>
/// <param name="Duration">Sample duration in 100 ns ticks.</param>
/// <param name="Position">Player position at delivery in 100 ns ticks.</param>
public readonly record struct PlaybackFrameRecord(uint Kind, uint Flags, double NowMilliseconds,
    long Timestamp = 0, long Duration = 0, long Position = 0);

/// <summary>Pre-sampled cumulative counters; recording and lookup require no per-frame allocations.</summary>
public readonly record struct PlaybackFrameEnvironmentSnapshot(double NowMilliseconds, TimeSpan GcPauseTotal,
    int Gen0, int Gen1, int Gen2, long? DemandWaits = null, TimeSpan? DemandWaitTime = null);
