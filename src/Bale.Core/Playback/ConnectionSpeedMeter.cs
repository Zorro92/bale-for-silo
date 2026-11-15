using Bale.Core.Native;

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
/// Implemented in Rust (native/bale-core/src/meters.rs).
/// </summary>
public sealed unsafe class ConnectionSpeedMeter
{
    public const int WindowMs = 500;
    public const int MinKbps = 100;
    public const int MaxKbps = 1_000_000;
    public const long QuickBytes = 100_000_000;
    public const long AccurateBytes = 1_000_000_000;
    /// <summary>A test whose first byte has not arrived this long after the request is abandoned.</summary>
    public static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(10);

    private readonly SpeedHandle _handle;

    public ConnectionSpeedMeter(ConnectionSpeedTestMode mode, int? targetKbps)
    {
        Mode = mode;
        // A target under 100 can never be reported, so it measures nothing; keep it to fail at the end.
        TargetKbps = targetKbps;
        BaleCoreMeters.EnsureLoaded();
        _handle = BaleCoreMeters.bale_core_speed_create(mode == ConnectionSpeedTestMode.Quick ? (byte)1 : (byte)0,
            targetKbps ?? 0, targetKbps is null ? (byte)0 : (byte)1);
    }

    public ConnectionSpeedTestMode Mode { get; }
    public int? TargetKbps { get; }
    public long RequestedBytes => Mode == ConnectionSpeedTestMode.Quick ? QuickBytes : AccurateBytes;
    public int LimitMs => Mode == ConnectionSpeedTestMode.Quick ? 3_000 : 10_000;

    /// <summary>Records <paramref name="bytes"/> arriving <paramref name="elapsedMs"/> after the first byte.</summary>
    public void Add(long elapsedMs, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMs);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        BaleCoreMeters.bale_core_speed_add(_handle, elapsedMs, bytes);
    }

    /// <summary>
    /// The time the test stops if it should stop by <paramref name="nowMs"/>, else null. A quick test with a
    /// target stops at the end of the first window after window 0 whose rate reaches it; every test stops at
    /// its limit. Call on the client's clock, not only when data arrives, so silent windows still close.
    /// </summary>
    public long? StopAt(long nowMs) =>
        BaleCoreMeters.bale_core_speed_stop_at(_handle, nowMs) is var stop && stop != BaleCoreMeters.None ? stop : null;

    /// <summary>
    /// The result when the test stopped at <paramref name="stopMs"/> (from <see cref="StopAt"/>, or the time
    /// the body ended cleanly, given as <paramref name="endedMs"/>), or null when it measured nothing.
    /// A transfer that failed (dropped, or ended short of its Content-Length) measured nothing: don't call this.
    /// </summary>
    public ConnectionSpeedResult? Result(long stopMs, long? endedMs = null)
    {
        NativeSpeedResult native;
        return BaleCoreMeters.bale_core_speed_result(_handle, stopMs, endedMs ?? 0, endedMs is null ? (byte)0 : (byte)1, &native) != 0
            ? new(native.Kbps, native.LowerBound != 0, native.Bytes, native.DurationMs) : null;
    }

    /// <summary>Diagnostics: kbps per 500 ms window so far (window 0 first).</summary>
    public IReadOnlyList<long> WindowKbps
    {
        get
        {
            byte* buffer = null;
            nuint length = 0;
            BaleCoreMeters.bale_core_speed_window_kbps(_handle, &buffer, &length);
            return System.Text.Json.JsonSerializer.Deserialize(BaleCoreMeters.TakeText(buffer, length),
                BaleCoreJson.Default.Int64Array) ?? [];
        }
    }
}
