#if DEBUG_SERVER
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
    private string? _outputPixelFormat;
    private int _unavailable;
    private int _requests;
    private readonly Dictionary<object, bool> _kinds = new(ReferenceEqualityComparer.Instance);

    internal DebugFrameDelivery(FFmpegMediaSource source, MediaPlaybackSession session, bool collectAheadStatistics = false)
    {
        _meter = new(collectAheadStatistics);
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

    internal void SetSeeking(bool seeking) => _meter.SetSeeking(seeking);

    internal PlaybackFrameDeliverySnapshot? Read()
    {
        if (Volatile.Read(ref _unavailable) != 0) return null;
        try { return _meter.Read(_session.Position, Environment.TickCount64); }
        catch (Exception ex) { Unavailable(ex); return null; }
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args) => _meter.InvalidateTimeline();

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        if (Volatile.Read(ref _unavailable) != 0) return;
        try
        {
            if (args.Request.Sample is not { } sample || !IsVideo(args.Request.StreamDescriptor)) return;
            _meter.Observe(sample.Timestamp, sample.Duration, _session.Position,
                _session.PlaybackState == MediaPlaybackState.Playing, sample.Discontinuous, Environment.TickCount64);
        }
        catch (Exception ex) { Unavailable(ex); }
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
