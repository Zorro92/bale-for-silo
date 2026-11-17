namespace Bale.Core.Playback;

/// <summary>
/// One plan's media throughput, the <c>plan_summary</c> <c>throughput_kbps</c> and a lowering's
/// <c>observed_throughput_kbps</c> (silo-server buffering-quality design, "Playback samples"): bytes over the time the
/// plan's media requests spent receiving them. Bale's transports count body bytes and the time spent inside body reads
/// (<see cref="MediaRangeStream.DownloadTime"/>, <see cref="MediaProgressiveStream.DownloadTime"/>), so time spent
/// waiting for the response headers and time the reader was held back by a full buffer are excluded, as the spec asks.
/// The transports keep no per-request split, so the 1,000 ms rule applies to the plan's total receiving time rather than
/// to each request. The page feeds the playing mount's cumulative counters once a tick; a new mount (seek remount,
/// reconnect) starts its own counters from zero.
/// </summary>
public sealed class PlanThroughput
{
    public const int MinKbps = 100;
    public const int MaxKbps = 1_000_000;
    /// <summary>Less receiving time than this measures nothing (a short burst reads well above the link).</summary>
    public const double MinReceivingMs = 1_000;

    private object? _source;
    private long _lastBytes;
    private double _lastMs;
    private long _bytes;
    private double _ms;

    /// <summary>A new plan: nothing measured yet, and the next stream observed starts from its own zero.</summary>
    public void Reset()
    {
        _source = null;
        _lastBytes = 0;
        _lastMs = 0;
        _bytes = 0;
        _ms = 0;
    }

    /// <summary>Adds what <paramref name="source"/>'s cumulative counters moved since the last call.</summary>
    public void Observe(object source, long downloadedBytes, TimeSpan downloadTime)
    {
        ArgumentNullException.ThrowIfNull(source);
        var ms = downloadTime.TotalMilliseconds;
        if (!ReferenceEquals(source, _source))
        {
            // A stream this plan has not seen: its counters began at zero with it.
            _source = source;
            _lastBytes = 0;
            _lastMs = 0;
        }
        if (downloadedBytes < _lastBytes || ms < _lastMs || !double.IsFinite(ms))
        {
            // Counters never go back; a reused stream object does. Start again from here.
            _lastBytes = downloadedBytes;
            _lastMs = double.IsFinite(ms) ? ms : 0;
            return;
        }
        _bytes += downloadedBytes - _lastBytes;
        _ms += ms - _lastMs;
        _lastBytes = downloadedBytes;
        _lastMs = ms;
    }

    /// <summary>The plan's rate in kbps, or null with under 1,000 ms receiving or outside 100–1,000,000.</summary>
    public int? Kbps
    {
        get
        {
            if (_ms < MinReceivingMs || _bytes <= 0) return null;
            var kbps = _bytes * 8 / _ms;
            if (!double.IsFinite(kbps) || kbps < MinKbps) return null;
            return (int)Math.Min(MaxKbps, Math.Floor(kbps));
        }
    }

    /// <summary>Body bytes counted for this plan (tests and stats).</summary>
    public long Bytes => _bytes;
}
