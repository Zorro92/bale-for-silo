namespace Bale.Core.Playback;

/// <summary>
/// Observes decoded video sample delivery, not rendering. A late frame has a presentation timestamp
/// behind the player clock when delivered. Display cadence and actual renderer drops are not measured.
/// </summary>
public sealed class PlaybackFrameDeliveryMeter
{
    public const int RecentWindowMilliseconds = 10_000;
    private readonly object _gate = new();
    private readonly Queue<long> _recent = new();
    private readonly List<double>? _ahead;
    private long _delivered;
    private long _late;
    private bool _observed;
    private bool _seeking;
    private bool _ignoreNext = true;
    private TimeSpan? _deliveredEnd;

    // Retain per-frame horizons only for a bounded debug probe, never the long-running player.
    public PlaybackFrameDeliveryMeter(bool collectAheadStatistics = false) =>
        _ahead = collectAheadStatistics ? [] : null;

    public void SetSeeking(bool seeking)
    {
        lock (_gate)
        {
            _seeking = seeking;
            InvalidateTimelineCore();
        }
    }

    public void InvalidateTimeline()
    {
        lock (_gate) InvalidateTimelineCore();
    }

    private void InvalidateTimelineCore()
    {
        _deliveredEnd = null;
        _ignoreNext = true;
    }

    /// <param name="nowMilliseconds">Monotonic wall time, shared with Read.</param>
    public void Observe(TimeSpan timestamp, TimeSpan duration, TimeSpan position, bool playing,
        bool discontinuous, long nowMilliseconds)
    {
        lock (_gate)
        {
            if (_seeking) return;
            _observed = true;
            _delivered++;
            _deliveredEnd = timestamp + duration;
            // Exclude preroll, the first frame after a timeline reset, and non-playing delivery.
            if (playing && !_ignoreNext && !discontinuous)
            {
                _ahead?.Add(Math.Max(0, (_deliveredEnd.Value - position).TotalSeconds));
                if (timestamp < position)
                {
                    _late++;
                    _recent.Enqueue(nowMilliseconds);
                }
            }
            _ignoreNext = !playing;
            Trim(nowMilliseconds);
        }
    }

    public PlaybackFrameDeliverySnapshot? Read(TimeSpan position, long nowMilliseconds)
    {
        lock (_gate)
        {
            Trim(nowMilliseconds);
            double? minimum = null;
            double? median = null;
            if (_ahead is { Count: > 0 })
            {
                var sorted = _ahead.ToArray();
                Array.Sort(sorted);
                minimum = sorted[0];
                var middle = sorted.Length / 2;
                median = sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
            }
            return !_observed ? null : new(_late, _recent.Count,
                _deliveredEnd is { } end ? Math.Max(0, (end - position).TotalSeconds) : null,
                _delivered, minimum, median);
        }
    }

    private void Trim(long nowMilliseconds)
    {
        while (_recent.TryPeek(out var time) && nowMilliseconds - time >= RecentWindowMilliseconds)
            _recent.Dequeue();
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
