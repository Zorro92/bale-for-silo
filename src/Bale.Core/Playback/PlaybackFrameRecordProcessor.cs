namespace Bale.Core.Playback;

/// <summary>
/// Processes a native observer's ordered timeline without retaining WinRT objects. The caller
/// serializes snapshot writes, record processing and reads; the existing meter/trace keep their semantics.
/// </summary>
public sealed class PlaybackFrameRecordProcessor
{
    private readonly PlaybackFrameDeliveryMeter _meter;
    private readonly PlaybackLateFrameTrace _lateFrameTrace = new();
    private readonly PlaybackFrameEnvironmentSnapshot[] _snapshots;
    private int _nextSnapshot;
    private int _snapshotCount;
    private bool _seeking;

    public PlaybackFrameRecordProcessor(int snapshotCapacity = 512, bool collectAheadStatistics = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(snapshotCapacity);
        _snapshots = new PlaybackFrameEnvironmentSnapshot[snapshotCapacity];
        _meter = new(collectAheadStatistics);
    }

    public void AddSnapshot(in PlaybackFrameEnvironmentSnapshot snapshot)
    {
        _snapshots[_nextSnapshot] = snapshot;
        _nextSnapshot = (_nextSnapshot + 1) % _snapshots.Length;
        _snapshotCount = Math.Min(_snapshotCount + 1, _snapshots.Length);
    }

    /// <summary>
    /// Latest snapshot at or before delivery + 50 ms, allowing the managed sampling thread to resume
    /// just after a GC pause. If delivery predates retained history, use the oldest available baseline.
    /// </summary>
    public PlaybackFrameEnvironmentSnapshot SnapshotFor(double nowMilliseconds)
    {
        for (var i = 0; i < _snapshotCount; i++)
        {
            var index = (_nextSnapshot - 1 - i + _snapshots.Length) % _snapshots.Length;
            if (_snapshots[index].NowMilliseconds <= nowMilliseconds + 50) return _snapshots[index];
        }
        return _snapshotCount == 0 ? default
            : _snapshots[(_nextSnapshot - _snapshotCount + _snapshots.Length) % _snapshots.Length];
    }

    public void Process(in PlaybackFrameRecord record)
    {
        switch (record.Kind)
        {
            case 1:
                var timestamp = TimeSpan.FromTicks(record.Timestamp);
                var duration = TimeSpan.FromTicks(record.Duration);
                var position = TimeSpan.FromTicks(record.Position);
                var late = _meter.Observe(timestamp, duration, position, (record.Flags & 1) != 0,
                    (record.Flags & 2) != 0, (long)record.NowMilliseconds);
                if (!_seeking)
                {
                    var snapshot = SnapshotFor(record.NowMilliseconds);
                    _lateFrameTrace.Sample(record.NowMilliseconds, timestamp, duration, position, late,
                        snapshot.DemandWaits, snapshot.DemandWaitTime, snapshot.GcPauseTotal,
                        snapshot.Gen0, snapshot.Gen1, snapshot.Gen2);
                }
                break;
            case 2:
                _meter.InvalidateTimeline();
                _lateFrameTrace.Reset();
                break;
            case 3:
            case 4:
                _seeking = record.Kind == 3;
                _meter.SetSeeking(_seeking);
                _lateFrameTrace.Reset();
                break;
        }
    }

    /// <param name="nowMilliseconds">The same Stopwatch/QPC-millisecond clock used by native records.</param>
    public PlaybackFrameDeliverySnapshot? Read(TimeSpan position, long nowMilliseconds) =>
        _meter.Read(position, nowMilliseconds);

    public string DescribeLateFrameTrace() => _lateFrameTrace.Describe();

    public void ResetLateFrameTrace() => _lateFrameTrace.Reset();
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
