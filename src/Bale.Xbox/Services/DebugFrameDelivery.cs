#if DEBUG_SERVER
using System.Diagnostics;
using Bale.Core.Playback;
using FFmpegInteropX;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Bale.Xbox.Services;

/// <summary>A passive observer of FFmpeg's decoded video delivery; never changes or retains a sample.</summary>
internal sealed class DebugFrameDelivery : IDisposable
{
    private readonly MediaStreamSource _source;
    private readonly MediaPlaybackSession _session;
    private readonly PlaybackFrameDeliveryMeter _meter;
    private readonly HttpRangeStream? _stream;
    private readonly PlaybackLateFrameTrace _lateFrameTrace = new();
    private readonly object _timelineGate = new();
    private bool _seeking;
    private string? _outputPixelFormat;
    private string? _outputColour;
    private int _unavailable;
    private int _requests;
    private int _videoSamples;
    private const int CollectEvery = 24;
    private readonly Dictionary<object, bool> _kinds = new(ReferenceEqualityComparer.Instance);

    internal DebugFrameDelivery(FFmpegMediaSource source, MediaPlaybackSession session,
        bool collectAheadStatistics = false, HttpRangeStream? stream = null)
    {
        _meter = new(collectAheadStatistics);
        _stream = stream;
        _session = session;
        _source = source.GetMediaStreamSource();
        // FFmpegInteropX Source/FFmpegMediaSource.cpp OnSampleRequested (pinned 2.1.0 wrapper):
        // its handler synchronously assigns Request.Sample. Subscribe AFTER CreateMediaPlaybackItem
        // installed that handler, observing its output without replacing the item/subtitle pipeline.
        _source.Starting += OnStarting;
        try { _source.SampleRequested += OnSampleRequested; }
        catch
        {
            _source.Starting -= OnStarting;
            throw;
        }
        Log.Info("player: frame delivery observer attached");
    }

    internal string? OutputPixelFormat => Volatile.Read(ref _outputPixelFormat);

    /// <summary>The decoded output's MF colour attributes (matrix, transfer, primaries, range), as set by the wrapper.</summary>
    internal string? OutputColour => Volatile.Read(ref _outputColour);

    internal void SetSeeking(bool seeking)
    {
        lock (_timelineGate)
        {
            _seeking = seeking;
            _meter.SetSeeking(seeking);
            _lateFrameTrace.Reset();
        }
    }

    internal string DescribeLateFrameTrace() => _lateFrameTrace.Describe();

    internal void ResetLateFrameTrace()
    {
        lock (_timelineGate) _lateFrameTrace.Reset();
    }

    internal PlaybackFrameDeliverySnapshot? Read()
    {
        if (Volatile.Read(ref _unavailable) != 0) return null;
        try { return _meter.Read(_session.Position, Environment.TickCount64); }
        catch (Exception ex) { Unavailable(ex); return null; }
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        lock (_timelineGate)
        {
            _meter.InvalidateTimeline();
            _lateFrameTrace.Reset();
        }
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        if (Volatile.Read(ref _unavailable) != 0) return;
        MediaStreamSourceSampleRequest? request = null;
        MediaStreamSample? sample = null;
        try
        {
            request = args.Request;
            sample = request.Sample;
            if (sample is null || !IsVideo(request.StreamDescriptor)) return;
            // Serialize timeline resets with the meter/trace pair so a delivery cannot straddle a
            // trace reset.
            lock (_timelineGate)
            {
                var nowMilliseconds = Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);
                var timestamp = sample.Timestamp;
                var duration = sample.Duration;
                var position = _session.Position;
                var late = _meter.Observe(timestamp, duration, position,
                    _session.PlaybackState == MediaPlaybackState.Playing, sample.Discontinuous, Environment.TickCount64);
                if (!_seeking)
                    _lateFrameTrace.Sample(nowMilliseconds, timestamp, duration, position, late,
                        _stream?.DemandWaits, _stream?.DemandWaitTime, GC.GetTotalPauseDuration(),
                        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            }
            // Outside the gate so a trace read/reset never waits on a collection; the next sample's
            // trace delta records this pause.
            // Each managed sample wrapper keeps its native decoded frame (~6 MB at 1080p P010)
            // alive until the GC finalizes it. Observing adds almost no managed allocation, so
            // without this a bounded probe reached 3.4 GB commit and E_OUTOFMEMORY in ~19 s.
            // A gen0 pass about once a second lets the finalizer release the frames.
            if (++_videoSamples % CollectEvery == 0) GC.Collect(0, GCCollectionMode.Forced, blocking: false);
        }
        catch (Exception ex) { Unavailable(ex); }
        finally
        {
            // Release the sample and request references now instead of at finalization. Finalizable
            // wrappers that outlive gen0 were promoted until full blocking collections paused delivery
            // for ~340-400 ms every 5-10 s (frame-trace class=gc, 1080p AV1). The descriptor stays
            // wrapped: _kinds caches it by reference.
            Release(sample);
            Release(request);
        }
    }

    private static void Release(object? wrapper)
    {
        try { if (wrapper is WinRT.IWinRTObject winrt) winrt.NativeObject.Dispose(); }
        catch { }
    }

    // Native AOT: CsWinRT hands the descriptor back as a plain IInspectable wrapper, so a C# type
    // test (is VideoStreamDescriptor) is always false. QueryInterface once per descriptor instead;
    // the wrappers are cached per native object, so the lookup stays tiny (audio + video).
    private bool IsVideo(IMediaStreamDescriptor? descriptor)
    {
        if (descriptor is null) return false;
        lock (_kinds)
        {
            if (_kinds.TryGetValue(descriptor, out var known)) return known;
            bool video;
            try { video = WinRT.CastExtensions.As<VideoStreamDescriptor>(descriptor) is not null; }
            catch (InvalidCastException) { video = false; }
            if (video)
            {
                // This is the decoded MSS output subtype (e.g. NV12/P010), not container bit depth.
                // A missing optional format must not disable the frame-delivery measurements.
                try
                {
                    var subtype = WinRT.CastExtensions.As<VideoStreamDescriptor>(descriptor).EncodingProperties.Subtype;
                    if (!string.IsNullOrWhiteSpace(subtype))
                        Volatile.Write(ref _outputPixelFormat, DebugScreenText.Compact(subtype, 40));
                }
                catch { }
                try
                {
                    var properties = WinRT.CastExtensions.As<VideoStreamDescriptor>(descriptor).EncodingProperties.Properties;
                    Volatile.Write(ref _outputColour, DebugVideoColour.Describe(properties));
                }
                catch { }
            }
            if (_kinds.Count < 16) _kinds[descriptor] = video;
            if (Interlocked.Increment(ref _requests) <= 4) Log.Info($"player: frame delivery descriptor video={video} known={_kinds.Count}");
            return video;
        }
    }

    private void Unavailable(Exception ex)
    {
        if (Interlocked.Exchange(ref _unavailable, 1) == 0)
            Log.Info($"player: frame delivery unavailable hr=0x{unchecked((uint)ex.HResult):X8}");
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _unavailable, 1);
        _source.Starting -= OnStarting;
        _source.SampleRequested -= OnSampleRequested;
    }
}
#endif
