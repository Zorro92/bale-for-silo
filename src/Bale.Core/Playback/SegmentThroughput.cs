namespace Bale.Core.Playback;

/// <summary>
/// On HLS, the rate the playing plan's segments arrived at while they were arriving: the buffering controller's evidence
/// of a slow link there, and the figure a lowering sends as <c>observed_throughput_kbps</c> (silo-server buffering-quality
/// design, "Slow-link evidence"; silo-apple 3709c5fb <c>RelayPlanThroughput</c>, which takes the same share of
/// AetherEngine's relay timing). Never a <c>plan_summary</c> <c>throughput_kbps</c> sample: Apple sends none on server
/// HLS either, and the server keeps the highest sample as proof of what the link carries.
/// <para>
/// <see cref="PlanThroughput"/>'s one-second windows read HLS wrong: a Silo transcode answers a segment only once its
/// encoder has written all of it, so a slow encoder looks like a slow link to any clock that runs while the request
/// waits. The Rust reader instead times each segment body inside its reads (<see cref="MediaProgressiveStream.BodyTransfer"/>):
/// a body's first read, which waited for the server, the time between bodies and a pause on its full buffer never count,
/// and a body that received for under a second counts nothing. This takes the plan's share of those running totals. It
/// holds no clock and performs no I/O.
/// </para>
/// <para>
/// Each mount brings its own stream, counting from zero (a seek remount, a reconnect, the replacement a replan mounts),
/// and all of a new stream's totals are the plan's. The stream that was playing when <see cref="Reset"/> started a new
/// plan is only a baseline: what it counted so far was the old plan's, as Apple ties the relay's totals to the load that
/// counted them. Totals that go back on the same stream object become the new baseline.
/// </para>
/// </summary>
public sealed class SegmentThroughput
{
    private object? _source;
    private long _lastBytes;
    private long _lastUs;
    private long _bytes;
    private long _us;

    /// <summary>A new plan: nothing measured yet; the stream that played the old one goes on from where it was.</summary>
    public void Reset()
    {
        _bytes = 0;
        _us = 0;
    }

    /// <summary>Takes <paramref name="source"/>'s running transfer totals (<see cref="MediaProgressiveStream.BodyTransfer"/>).</summary>
    public void Observe(object source, long transferBytes, TimeSpan transferTime)
    {
        ArgumentNullException.ThrowIfNull(source);
        var us = transferTime.Ticks / TimeSpan.TicksPerMicrosecond;
        if (!ReferenceEquals(source, _source))
        {
            // A new mount: its counters began at zero with it.
            _source = source;
            _lastBytes = 0;
            _lastUs = 0;
        }
        if (transferBytes >= _lastBytes && us >= _lastUs)
        {
            _bytes += transferBytes - _lastBytes;
            _us += us - _lastUs;
        }
        // Counters never go back; a reused stream object does. Start again from here.
        _lastBytes = transferBytes;
        _lastUs = us;
    }

    /// <summary>
    /// The plan's rate in kbps, or null with under a second of receiving or outside 100–1,000,000 (the bounds the server
    /// takes, as for <see cref="PlanThroughput"/>).
    /// </summary>
    public int? Kbps
    {
        get
        {
            if (_us < PlanThroughput.MinReceivingMs * 1_000 || _bytes <= 0) return null;
            var kbps = _bytes * 8_000.0 / _us;
            if (!double.IsFinite(kbps) || kbps < PlanThroughput.MinKbps) return null;
            return (int)Math.Min(PlanThroughput.MaxKbps, Math.Floor(kbps));
        }
    }

    /// <summary>Segment body bytes counted for this plan (tests and stats).</summary>
    public long Bytes => _bytes;

    /// <summary>The time those bytes spent arriving (tests and stats).</summary>
    public TimeSpan Time => TimeSpan.FromMicroseconds(_us);
}
