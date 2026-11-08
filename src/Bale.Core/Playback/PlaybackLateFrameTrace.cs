using System.Globalization;
using System.Text;

namespace Bale.Core.Playback;

/// <summary>
/// Bounded delivery diagnostics correlating late samples with stream waits, GC pauses and delivery
/// gaps. Classes describe concurrent evidence, not proven causes or renderer drops.
/// </summary>
public sealed class PlaybackLateFrameTrace
{
    private const int Capacity = 300;
    private readonly object _gate = new();
    private readonly Queue<LateEvent> _events = new(Capacity);
    private Observation? _previous;
    private double _startMilliseconds;
    private long _samples;
    private long _late;
    private long _stream;
    private long _gc;
    private long _gap;
    private long _other;
    private double _maxGapMilliseconds;
    private long _longGaps;

    /// <param name="nowMilliseconds">Monotonic wall time; the first sample establishes the trace start.</param>
    /// <param name="late">Whether the delivery meter counted this sample as late.</param>
    /// <param name="gcPauseTotal">Cumulative pause duration supplied by the caller, not read by Core.</param>
    public void Sample(double nowMilliseconds, TimeSpan timestamp, TimeSpan duration, TimeSpan position,
        bool late, long? demandWaits, TimeSpan? demandWaitTime, TimeSpan gcPauseTotal, int gen0, int gen1, int gen2)
    {
        lock (_gate)
        {
            _samples++;
            if (_previous is { } previous)
            {
                var gap = nowMilliseconds - previous.WallMilliseconds;
                var longGap = gap > 2 * duration.TotalMilliseconds;
                _maxGapMilliseconds = Math.Max(_maxGapMilliseconds, gap);
                if (longGap) _longGaps++;
                if (late)
                {
                    var waits = demandWaits - previous.DemandWaits;
                    var waitMilliseconds = (demandWaitTime - previous.DemandWaitTime)?.TotalMilliseconds;
                    var gcMilliseconds = (gcPauseTotal - previous.GcPauseTotal).TotalMilliseconds;
                    var classification = waitMilliseconds > 0 ? "stream"
                        : gcMilliseconds >= 4 ? "gc" : longGap ? "gap" : "other";
                    _late++;
                    switch (classification)
                    {
                        case "stream": _stream++; break;
                        case "gc": _gc++; break;
                        case "gap": _gap++; break;
                        default: _other++; break;
                    }
                    // Dequeue before enqueue so the preallocated ring never grows, even on late bursts.
                    if (_events.Count == Capacity) _events.Dequeue();
                    _events.Enqueue(new(nowMilliseconds - _startMilliseconds, timestamp.TotalMilliseconds,
                        (position - timestamp).TotalMilliseconds, (previous.End - position).TotalMilliseconds,
                        gap, waits, waitMilliseconds, gcMilliseconds, gen0 - previous.Gen0, gen1 - previous.Gen1,
                        gen2 - previous.Gen2, classification));
                }
            }
            else
            {
                // A baseline counts as a sample, but cannot produce a classified late event or gap.
                _startMilliseconds = nowMilliseconds;
            }
            // Every delivery advances the baseline, not just the deliveries that were late.
            _previous = new(nowMilliseconds, timestamp + duration, demandWaits, demandWaitTime,
                gcPauseTotal, gen0, gen1, gen2);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _events.Clear();
            _previous = null;
            _startMilliseconds = 0;
            _samples = _late = _stream = _gc = _gap = _other = _longGaps = 0;
            _maxGapMilliseconds = 0;
        }
    }

    public string Describe()
    {
        lock (_gate)
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture,
                $"samples={_samples} late={_late} stream={_stream} gc={_gc} gap={_gap} other={_other} max-gap={_maxGapMilliseconds:0.0} long-gaps={_longGaps}\n");
            foreach (var entry in _events)
            {
                var waits = entry.DemandWaits is { } count && entry.DemandWaitMilliseconds is { } milliseconds
                    ? string.Create(CultureInfo.InvariantCulture, $"{count}/{milliseconds:0.0}") : "-";
                text.Append(CultureInfo.InvariantCulture,
                    $"t={entry.TimeMilliseconds:0.0} pts={entry.PtsMilliseconds:0.0} late={entry.LatenessMilliseconds:0.0} ahead={entry.AheadMilliseconds:0.0} gap={entry.GapMilliseconds:0.0} dw={waits} gc={entry.GcMilliseconds:0.0} g0={entry.Gen0} g1={entry.Gen1} g2={entry.Gen2} class={entry.Classification}\n");
            }
            return text.ToString();
        }
    }

    private readonly record struct Observation(double WallMilliseconds, TimeSpan End, long? DemandWaits,
        TimeSpan? DemandWaitTime, TimeSpan GcPauseTotal, int Gen0, int Gen1, int Gen2);

    private readonly record struct LateEvent(double TimeMilliseconds, double PtsMilliseconds,
        double LatenessMilliseconds, double AheadMilliseconds, double GapMilliseconds, long? DemandWaits,
        double? DemandWaitMilliseconds, double GcMilliseconds, int Gen0, int Gen1, int Gen2, string Classification);
}
