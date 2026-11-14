namespace Bale.Core.Playback;

/// <summary>Quick runs automatically; Accurate only from Test speed.</summary>
public enum ConnectionSpeedTestMode { Quick, Accurate }

/// <summary>What a speed test reports: <c>POST /api/v2/playback/bandwidth-probe/results</c> fields.</summary>
public sealed record ConnectionSpeedResult(int Kbps, bool LowerBound, long Bytes, int DurationMs);

/// <summary>
/// The connection speed test's arithmetic, from silo-server's buffering-quality design
/// (docs/design/2026-10-17-buffering-quality.md, "Measuring"; vectors in schemas/buffering-quality/v1).
/// The clock starts at the first byte of the body. Bytes count in the 500 ms window in which they arrive;
/// window 0 is ramp-up and never counts; a window in which nothing arrived counts at 0.
/// </summary>
public sealed class ConnectionSpeedMeter
{
    public const int WindowMs = 500;
    public const int MinKbps = 100;
    public const int MaxKbps = 1_000_000;
    public const long QuickBytes = 100_000_000;
    public const long AccurateBytes = 1_000_000_000;
    /// <summary>A test whose first byte has not arrived this long after the request is abandoned.</summary>
    public static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(10);

    private readonly List<long> _windows = [];
    private long _totalBytes;
    private int? _hitWindow;

    public ConnectionSpeedMeter(ConnectionSpeedTestMode mode, int? targetKbps)
    {
        Mode = mode;
        // A target under 100 can never be reported, so it measures nothing; keep it to fail at the end.
        TargetKbps = targetKbps;
    }

    public ConnectionSpeedTestMode Mode { get; }
    public int? TargetKbps { get; }
    public long RequestedBytes => Mode == ConnectionSpeedTestMode.Quick ? QuickBytes : AccurateBytes;
    public int LimitMs => Mode == ConnectionSpeedTestMode.Quick ? 3_000 : 10_000;
    private int SettledWindows => Mode == ConnectionSpeedTestMode.Quick ? 2 : 6;

    /// <summary>Records <paramref name="bytes"/> arriving <paramref name="elapsedMs"/> after the first byte.</summary>
    public void Add(long elapsedMs, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMs);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        var window = (int)(elapsedMs / WindowMs);
        while (_windows.Count <= window) _windows.Add(0);
        _windows[window] += bytes;
        _totalBytes += bytes;
    }

    /// <summary>
    /// The time the test stops if it should stop by <paramref name="nowMs"/>, else null. A quick test with a
    /// target stops at the end of the first window after window 0 whose rate reaches it; every test stops at
    /// its limit. Call on the client's clock, not only when data arrives, so silent windows still close.
    /// </summary>
    public long? StopAt(long nowMs)
    {
        if (Mode == ConnectionSpeedTestMode.Quick && TargetKbps is { } target)
        {
            var closed = (int)Math.Min(nowMs / WindowMs, LimitMs / WindowMs);
            for (var window = 1; window < closed; window++)
            {
                if (Rate(Window(window), WindowMs) >= target)
                {
                    _hitWindow = window;
                    return (window + 1) * (long)WindowMs;
                }
            }
        }
        return nowMs >= LimitMs ? LimitMs : null;
    }

    /// <summary>
    /// The result when the test stopped at <paramref name="stopMs"/> (from <see cref="StopAt"/>, or the time
    /// the body ended cleanly, given as <paramref name="endedMs"/>), or null when it measured nothing.
    /// A transfer that failed (dropped, or ended short of its Content-Length) measured nothing: don't call this.
    /// </summary>
    public ConnectionSpeedResult? Result(long stopMs, long? endedMs = null)
    {
        if (endedMs is { } ended) stopMs = Math.Min(stopMs, ended);
        if (_hitWindow is { } hit && (hit + 1) * (long)WindowMs <= stopMs)
            return Bound(new(ClampHigh(TargetKbps!.Value), true, Window(hit), WindowMs));

        // Complete windows after window 0 that fully elapsed before the stop; a window cut short doesn't count.
        var complete = (int)(stopMs / WindowMs) - 1;
        if (complete < 1)
        {
            // The whole probe arrived before the first complete window after window 0: average it from the
            // first byte to the end. The ramp is inside, so it can only read low: a lower bound.
            if (endedMs is not { } end || end <= 0 || _totalBytes < RequestedBytes) return null;
            var average = Rate(_totalBytes, end);
            if (Mode == ConnectionSpeedTestMode.Quick && TargetKbps is { } target && average >= target)
                return Bound(new(target, true, _totalBytes, (int)end));
            return Bound(new(ClampHigh(average), true, _totalBytes, (int)end));
        }
        var count = Math.Min(SettledWindows, complete);
        long bytes = 0;
        for (var window = complete - count + 1; window <= complete; window++) bytes += Window(window);
        var duration = count * WindowMs;
        var rate = Rate(bytes, duration);
        return Bound(new(ClampHigh(rate), rate > MaxKbps, bytes, duration));
    }

    private long Window(int window) => window < _windows.Count ? _windows[window] : 0;

    /// <summary>Diagnostics: kbps per 500 ms window so far (window 0 first).</summary>
    public IReadOnlyList<long> WindowKbps => _windows.Select(bytes => Rate(bytes, WindowMs)).ToArray();

    private static long Rate(long bytes, long durationMs) => durationMs <= 0 ? 0 : bytes * 8 / durationMs;

    private static int ClampHigh(long kbps) => (int)Math.Min(kbps, MaxKbps);

    // Results under 100 measured nothing (above 1,000,000 is clamped by the callers, as a lower bound).
    private static ConnectionSpeedResult? Bound(ConnectionSpeedResult result) => result.Kbps < MinKbps ? null : result;
}
