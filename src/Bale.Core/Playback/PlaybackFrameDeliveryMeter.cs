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
    private long _late;
    private bool _observed;
    private bool _seeking;
    private bool _ignoreNext = true;
    private TimeSpan? _deliveredEnd;

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
            _deliveredEnd = timestamp + duration;
            // Exclude preroll, the first frame after a timeline reset, and non-playing delivery.
            if (playing && !_ignoreNext && !discontinuous && timestamp < position)
            {
                _late++;
                _recent.Enqueue(nowMilliseconds);
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
            return !_observed ? null : new(_late, _recent.Count,
                _deliveredEnd is { } end ? Math.Max(0, (end - position).TotalSeconds) : null);
        }
    }

    private void Trim(long nowMilliseconds)
    {
        while (_recent.TryPeek(out var time) && nowMilliseconds - time >= RecentWindowMilliseconds)
            _recent.Dequeue();
    }
}

/// <param name="DeliveredAheadSeconds">Last delivered sample end minus the clock, not a renderer queue depth.</param>
public sealed record PlaybackFrameDeliverySnapshot(long LateFrames, int RecentLateFrames, double? DeliveredAheadSeconds)
{
    public double RecentLateFramesPerSecond => RecentLateFrames / (PlaybackFrameDeliveryMeter.RecentWindowMilliseconds / 1000.0);
}
