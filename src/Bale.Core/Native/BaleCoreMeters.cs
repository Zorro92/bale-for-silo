using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bale.Core.Native;

// The handle ABI (native/bale-core/src/abi_meters.rs): per-frame and per-read state that lives in
// Rust between calls. Plain structs and numbers, no JSON. Layouts must match the #[repr(C)] structs.

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDeliverySnapshot
{
    public long LateFrames;
    public long TotalFramesDelivered;
    public int RecentLateFrames;
    public uint Flags; // bit 0: DeliveredAheadSeconds set; bit 1: Min/Median set.
    public double DeliveredAheadSeconds;
    public double MinDeliveredAheadSeconds;
    public double MedianDeliveredAheadSeconds;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEnvironment
{
    public double NowMilliseconds;
    public long GcPauseTotalTicks;
    public long DemandWaits;
    public long DemandWaitTicks;
    public int Gen0;
    public int Gen1;
    public int Gen2;
    public uint Flags; // bit 0: DemandWaits set; bit 1: DemandWaitTicks set.
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFrameRecord
{
    public uint Kind;
    public uint Flags;
    public double NowMilliseconds;
    public long Timestamp;
    public long Duration;
    public long Position;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSpeedResult
{
    public int Kbps;
    public int DurationMs;
    public long Bytes;
    public byte LowerBound;
}

internal sealed class FramesHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() { BaleCoreMeters.bale_core_frames_destroy(handle); return true; }
}

internal sealed class DeliveryHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() { BaleCoreMeters.bale_core_delivery_destroy(handle); return true; }
}

internal sealed class TraceHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() { BaleCoreMeters.bale_core_trace_destroy(handle); return true; }
}

internal sealed class ThroughputHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() { BaleCoreMeters.bale_core_throughput_destroy(handle); return true; }
}

internal sealed class SpeedHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() { BaleCoreMeters.bale_core_speed_destroy(handle); return true; }
}

internal static unsafe class BaleCoreMeters
{
    private const string Library = "bale_core";
    /// <summary>"None" for an i64 result.</summary>
    public const long None = long.MinValue;

    /// <summary>Checks the library's ABI version once, like every JSON call does.</summary>
    public static void EnsureLoaded() => BaleCoreNative.EnsureLoaded();

    public static NativeEnvironment ToNative(in Playback.PlaybackFrameEnvironmentSnapshot snapshot) => new()
    {
        NowMilliseconds = snapshot.NowMilliseconds,
        GcPauseTotalTicks = snapshot.GcPauseTotal.Ticks,
        Gen0 = snapshot.Gen0,
        Gen1 = snapshot.Gen1,
        Gen2 = snapshot.Gen2,
        DemandWaits = snapshot.DemandWaits ?? 0,
        DemandWaitTicks = snapshot.DemandWaitTime?.Ticks ?? 0,
        Flags = (snapshot.DemandWaits is null ? 0u : 1u) | (snapshot.DemandWaitTime is null ? 0u : 2u),
    };

    public static Playback.PlaybackFrameEnvironmentSnapshot FromNative(in NativeEnvironment native) => new(
        native.NowMilliseconds, TimeSpan.FromTicks(native.GcPauseTotalTicks), native.Gen0, native.Gen1, native.Gen2,
        (native.Flags & 1) != 0 ? native.DemandWaits : null,
        (native.Flags & 2) != 0 ? TimeSpan.FromTicks(native.DemandWaitTicks) : null);

    public static Playback.PlaybackFrameDeliverySnapshot FromNative(in NativeDeliverySnapshot native) => new(
        native.LateFrames, native.RecentLateFrames, (native.Flags & 1) != 0 ? native.DeliveredAheadSeconds : null,
        native.TotalFramesDelivered, (native.Flags & 2) != 0 ? native.MinDeliveredAheadSeconds : null,
        (native.Flags & 2) != 0 ? native.MedianDeliveredAheadSeconds : null);

    /// <summary>Reads and releases a text buffer the library handed out.</summary>
    public static string TakeText(byte* buffer, nuint length)
    {
        try { return buffer is null ? string.Empty : Encoding.UTF8.GetString(buffer, checked((int)length)); }
        finally { BaleCoreNative.Free(buffer, length); }
    }

    [DllImport(Library, ExactSpelling = true)] public static extern FramesHandle bale_core_frames_create(uint capacity, byte collectAhead);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_frames_destroy(nint handle);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_frames_add_snapshot(FramesHandle handle, NativeEnvironment* snapshot);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_frames_snapshot_for(FramesHandle handle, double nowMs, NativeEnvironment* output);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_frames_process(FramesHandle handle, NativeFrameRecord* records, nuint count);
    [DllImport(Library, ExactSpelling = true)] public static extern byte bale_core_frames_read(FramesHandle handle, long position, long nowMs, NativeDeliverySnapshot* output);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_frames_describe(FramesHandle handle, byte** output, nuint* outputLength);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_frames_reset_trace(FramesHandle handle);

    [DllImport(Library, ExactSpelling = true)] public static extern DeliveryHandle bale_core_delivery_create(byte collectAhead);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_delivery_destroy(nint handle);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_delivery_set_seeking(DeliveryHandle handle, byte seeking);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_delivery_invalidate(DeliveryHandle handle);
    [DllImport(Library, ExactSpelling = true)] public static extern byte bale_core_delivery_observe(DeliveryHandle handle, long timestamp, long duration, long position, byte playing, byte discontinuous, long nowMs);
    [DllImport(Library, ExactSpelling = true)] public static extern byte bale_core_delivery_read(DeliveryHandle handle, long position, long nowMs, NativeDeliverySnapshot* output);

    [DllImport(Library, ExactSpelling = true)] public static extern TraceHandle bale_core_trace_create();
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_trace_destroy(nint handle);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_trace_sample(TraceHandle handle, double nowMs, long timestamp, long duration, long position, byte late, NativeEnvironment* environment);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_trace_reset(TraceHandle handle);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_trace_describe(TraceHandle handle, byte** output, nuint* outputLength);

    [DllImport(Library, ExactSpelling = true)] public static extern ThroughputHandle bale_core_throughput_create();
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_throughput_destroy(nint handle);
    [DllImport(Library, ExactSpelling = true)] public static extern long bale_core_throughput_sample(ThroughputHandle handle, long bytes, long ticks);

    [DllImport(Library, ExactSpelling = true)] public static extern SpeedHandle bale_core_speed_create(byte quick, int targetKbps, byte hasTarget);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_speed_destroy(nint handle);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_speed_add(SpeedHandle handle, long elapsedMs, long bytes);
    [DllImport(Library, ExactSpelling = true)] public static extern long bale_core_speed_stop_at(SpeedHandle handle, long nowMs);
    [DllImport(Library, ExactSpelling = true)] public static extern byte bale_core_speed_result(SpeedHandle handle, long stopMs, long endedMs, byte hasEnded, NativeSpeedResult* output);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_speed_window_kbps(SpeedHandle handle, byte** output, nuint* outputLength);
}
