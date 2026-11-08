#if DEBUG_SERVER
using System.Diagnostics;
using System.Runtime.InteropServices;
using Bale.Core.Playback;
using FFmpegInteropX;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// Rust releases every sample in its native callback. This thread only sees blittable records and
/// samples GC/stream counters independently of delivery, without forced collections or managed events.
/// </summary>
internal sealed unsafe partial class NativeFrameDelivery : IDebugFrameDelivery
{
    private const uint ExpectedAbi = 4;
    private const int DrainCapacity = 512;
    private const int FailureHResult = unchecked((int)0x80004005);
    private readonly MediaStreamSource _source;
    private readonly MediaPlaybackSession _session;
    private readonly HttpRangeStream? _stream;
    private readonly PlaybackFrameRecordProcessor _processor = new();
    private readonly FrameRecord[] _records = new FrameRecord[DrainCapacity];
    private readonly object _gate = new();
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;
    private nint _observer;
    private int _disposed;
    private int _unavailable;
    private bool _descriptorRead;
    private string? _outputPixelFormat;
    private string? _outputColour;

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameRecord
    {
        public uint Kind;
        public uint Flags;
        public double NowMilliseconds;
        public long Timestamp;
        public long Duration;
        public long Position;
    }

    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_gpu_abi_version();
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern int bale_frame_observer_create(nint source, nint session, nint* observer);
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern int bale_frame_observer_mark(nint observer, uint kind);
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern uint bale_frame_observer_drain(nint observer, FrameRecord* records, uint capacity, ulong* dropped);
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern int bale_frame_observer_status(nint observer);
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern nint bale_frame_observer_video_descriptor(nint observer);
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern void bale_frame_observer_destroy(nint observer);

    internal NativeFrameDelivery(FFmpegMediaSource source, MediaPlaybackSession session, HttpRangeStream? stream = null)
    {
        if (bale_gpu_abi_version() != ExpectedAbi)
            throw new InvalidOperationException("The native frame observer ABI does not match.");
        _source = source.GetMediaStreamSource();
        _session = session;
        _stream = stream;
        _processor.AddSnapshot(CaptureSnapshot());
        nint observer = 0;
        var hr = bale_frame_observer_create(((WinRT.IWinRTObject)_source).NativeObject.ThisPtr,
            ((WinRT.IWinRTObject)_session).NativeObject.ThisPtr, &observer);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        if (observer == 0) throw new InvalidOperationException("The native frame observer was not created.");
        _observer = observer;
        _thread = new Thread(DrainLoop) { IsBackground = true, Name = "bale-frame-drain" };
        try { _thread.Start(); }
        catch
        {
            bale_frame_observer_destroy(_observer);
            _observer = 0;
            _stop.Dispose();
            throw;
        }
    }

    public string? OutputPixelFormat => Volatile.Read(ref _outputPixelFormat);
    public string? OutputColour => Volatile.Read(ref _outputColour);

    public void SetSeeking(bool seeking)
    {
        lock (_gate)
        {
            if (!Available) return;
            try
            {
                var hr = bale_frame_observer_mark(_observer, seeking ? 3u : 4u);
                if (hr != 0) Unavailable(hr);
            }
            catch (Exception ex) { Unavailable(ex.HResult); }
        }
    }

    public PlaybackFrameDeliverySnapshot? Read()
    {
        lock (_gate)
        {
            if (!Available) return null;
            try
            {
                DrainCore();
                return Available ? _processor.Read(_session.Position, (long)NowMilliseconds) : null;
            }
            catch (Exception ex) { Unavailable(ex.HResult); return null; }
        }
    }

    public string DescribeLateFrameTrace()
    {
        lock (_gate)
        {
            if (Available)
            {
                try { DrainCore(); }
                catch (Exception ex) { Unavailable(ex.HResult); }
            }
            return Available ? _processor.DescribeLateFrameTrace() : "frame delivery unavailable\n";
        }
    }

    public void ResetLateFrameTrace()
    {
        lock (_gate)
        {
            if (!Available) return;
            try
            {
                // Buffered pre-reset deliveries must not reappear in the freshly cleared trace.
                DrainCore();
                _processor.ResetLateFrameTrace();
            }
            catch (Exception ex) { Unavailable(ex.HResult); }
        }
    }

    private static double NowMilliseconds => Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);
    private bool Available => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _unavailable) == 0 && _observer != 0;

    private PlaybackFrameEnvironmentSnapshot CaptureSnapshot() => new(NowMilliseconds, GC.GetTotalPauseDuration(),
        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), _stream?.DemandWaits, _stream?.DemandWaitTime);

    private void DrainLoop()
    {
        try
        {
            var lastDrain = NowMilliseconds;
            while (!_stop.WaitOne(25))
            {
                lock (_gate)
                {
                    if (!Available) break;
                    var snapshot = CaptureSnapshot();
                    _processor.AddSnapshot(snapshot);
                    if (snapshot.NowMilliseconds - lastDrain < 250) continue;
                    DrainCore();
                    lastDrain = snapshot.NowMilliseconds;
                }
            }
        }
        catch (Exception ex) { Unavailable(ex.HResult); }
        finally
        {
            // The worker owns final cleanup, even if Dispose's bounded Join times out. No caller
            // can destroy a handle while this worker is in an export or wrapping the descriptor.
            lock (_gate)
            {
                if (_observer != 0)
                {
                    bale_frame_observer_destroy(_observer);
                    _observer = 0;
                }
                GC.KeepAlive(_source);
                GC.KeepAlive(_session);
            }
            _stop.Dispose();
        }
    }

    // Called only under _gate, also serializing all exported calls with lifetime/cleanup.
    private void DrainCore()
    {
        var status = bale_frame_observer_status(_observer);
        if (status != 0) { Unavailable(status); return; }
        uint count;
        do
        {
            ulong dropped = 0;
            fixed (FrameRecord* records = _records)
                count = bale_frame_observer_drain(_observer, records, DrainCapacity, &dropped);
            // Losing a seek marker would silently corrupt the timeline. Fail closed, not with a
            // plausible-looking count that omits samples or keeps the wrong seeking state.
            if (dropped != 0) { Unavailable(FailureHResult); return; }
            for (var i = 0; i < count; i++)
            {
                ref readonly var record = ref _records[i];
                _processor.Process(new(record.Kind, record.Flags, record.NowMilliseconds,
                    record.Timestamp, record.Duration, record.Position));
                if (record.Kind == 1 && !_descriptorRead) ReadOutputDescriptor();
            }
        } while (count == DrainCapacity);
        status = bale_frame_observer_status(_observer);
        if (status != 0) Unavailable(status);
    }

    private void ReadOutputDescriptor()
    {
        _descriptorRead = true;
        nint pointer = 0;
        try
        {
            pointer = bale_frame_observer_video_descriptor(_observer);
            if (pointer == 0) return;
            var descriptor = WinRT.CastExtensions.As<VideoStreamDescriptor>(WinRT.MarshalInspectable<object>.FromAbi(pointer));
            // Optional output metadata is projected once, never per sample. Failure must not disable
            // measurements, nor prevent reading the other optional attribute.
            try
            {
                var subtype = descriptor.EncodingProperties.Subtype;
                if (!string.IsNullOrWhiteSpace(subtype))
                    Volatile.Write(ref _outputPixelFormat, DebugScreenText.Compact(subtype, 40));
            }
            catch { }
            try { Volatile.Write(ref _outputColour, DebugVideoColour.Describe(descriptor.EncodingProperties.Properties)); }
            catch { }
        }
        catch { }
        finally { if (pointer != 0) Marshal.Release(pointer); }
    }

    private void Unavailable(int hr)
    {
        if (Interlocked.Exchange(ref _unavailable, 1) == 0)
            Log.Info($"player: native frame delivery unavailable hr=0x{unchecked((uint)hr):X8}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // A failing worker may already have released both the observer and the stop event.
        try { _stop.Set(); }
        catch (ObjectDisposedException) { }
        _thread.Join(1000);
    }
}
#endif
