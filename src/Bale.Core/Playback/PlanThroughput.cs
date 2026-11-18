namespace Bale.Core.Playback;

/// <summary>
/// One plan's media throughput, the <c>plan_summary</c> <c>throughput_kbps</c> and a lowering's
/// <c>observed_throughput_kbps</c> (silo-server buffering-quality design, "Playback samples"). The server keeps the highest
/// sample per device and connection and takes one at or above the target as proof the link carries it, so this must
/// never read high.
/// <para>
/// It follows silo-apple's <c>UpstreamReadMeter.swift</c>, which the spec names: the transports' cumulative body-byte count
/// is taken in windows of about a second, and a window that saw any bytes counts as receiving for its whole length. The
/// rate is therefore a lower bound: a reader that filled its buffer in part of a second and then parked reads low, never
/// high. Bale's range transports fetch 1 MiB blocks, and each on a warm connection arrives at burst rate, which the spec
/// warns reads up to twice the sustained link; timing only the reads themselves (the transports' <c>DownloadTime</c>,
/// still used by the HUD) overstated it for exactly that reason.
/// </para>
/// <para>
/// The page calls <see cref="Observe"/> on every UI tick (100 ms) and on state changes, so calls are gathered into
/// windows: one closes at the first call at least <see cref="WindowMs"/> after it opened and is charged its whole elapsed
/// time, so a second's bytes are never charged to a fraction of one (Apple charges each tick at least its nominal
/// interval). A window longer than <see cref="MaxWindowMs"/> (a held-up UI thread, a suspension) says nothing about how
/// long its bytes took and stays out, as Apple's <c>maxTickIntervalMs</c>. A window still open does not count.
/// </para>
/// <para>
/// A new mount (seek remount, reconnect) brings a new stream whose counters start at zero: inside an open window its bytes
/// from zero join that window, which keeps running, since the mount opened after the window's last call and its bytes
/// arrived within the window's time. The first stream seen with no window open (the first call after
/// <see cref="Reset"/>, which the page calls at adoption, before the plan's mount has read much) is only a baseline: its
/// earlier bytes took an unknown time, and Apple likewise drops a new reader's first total. Counters that go back on the
/// same stream (a reused stream object) become the new baseline; the open window keeps what it had.
/// </para>
/// </summary>
public sealed class PlanThroughput
{
    public const int MinKbps = 100;
    public const int MaxKbps = 1_000_000;
    /// <summary>Less receiving time than this measures nothing (a short burst reads well above the link).</summary>
    public const double MinReceivingMs = 1_000;
    /// <summary>A window closes at the first call this long after it opened (Apple's once-a-second tick).</summary>
    public const long WindowMs = 1_000;
    /// <summary>A window longer than this stays out of the rate (Apple's <c>maxTickIntervalMs</c>).</summary>
    public const long MaxWindowMs = 3_000;

    private object? _source;
    private long _lastBytes;
    private bool _windowOpen;
    private long _windowStartMs;
    private long _windowBytes;
    private long _bytes;
    private long _ms;

    /// <summary>A new plan: nothing measured yet, and the next stream observed is only a baseline.</summary>
    public void Reset()
    {
        _source = null;
        _lastBytes = 0;
        _windowOpen = false;
        _windowStartMs = 0;
        _windowBytes = 0;
        _bytes = 0;
        _ms = 0;
    }

    /// <summary>
    /// Takes <paramref name="source"/>'s cumulative body-byte count at wall-clock <paramref name="nowMs"/>
    /// (<see cref="Environment.TickCount64"/>).
    /// </summary>
    public void Observe(object source, long downloadedBytes, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!_windowOpen)
        {
            // First call of the plan, or after a gap: a baseline that opens the next window.
            OpenWindow(source, downloadedBytes, nowMs);
            return;
        }
        if (nowMs < _windowStartMs)
        {
            // The clock went back: the window's length is unknown.
            OpenWindow(source, downloadedBytes, nowMs);
            return;
        }
        if (!ReferenceEquals(source, _source))
        {
            // A new mount inside the window: its counters began at zero with it.
            _source = source;
            _lastBytes = 0;
        }
        if (downloadedBytes < _lastBytes)
        {
            // Counters never go back; a reused stream object does. Start again from here.
            _lastBytes = downloadedBytes;
        }
        else
        {
            _windowBytes += downloadedBytes - _lastBytes;
            _lastBytes = downloadedBytes;
        }

        var elapsed = nowMs - _windowStartMs;
        if (elapsed < WindowMs) return;
        if (elapsed <= MaxWindowMs && _windowBytes > 0)
        {
            _bytes += _windowBytes;
            _ms += elapsed;
        }
        // Back to back: the next window opens where this one closed.
        _windowStartMs = nowMs;
        _windowBytes = 0;
    }

    private void OpenWindow(object source, long downloadedBytes, long nowMs)
    {
        _source = source;
        _lastBytes = downloadedBytes;
        _windowOpen = true;
        _windowStartMs = nowMs;
        _windowBytes = 0;
    }

    /// <summary>The plan's rate in kbps, or null with under 1,000 ms receiving or outside 100–1,000,000.</summary>
    public int? Kbps
    {
        get
        {
            if (_ms < MinReceivingMs || _bytes <= 0) return null;
            var kbps = _bytes * 8.0 / _ms;
            if (!double.IsFinite(kbps) || kbps < MinKbps) return null;
            return (int)Math.Min(MaxKbps, Math.Floor(kbps));
        }
    }

    /// <summary>Body bytes counted for this plan, in closed windows (tests and stats).</summary>
    public long Bytes => _bytes;
}
