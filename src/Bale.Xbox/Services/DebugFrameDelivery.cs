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
    private readonly PlaybackFrameDeliveryMeter _meter = new();
    private int _unavailable;

    internal DebugFrameDelivery(FFmpegMediaSource source, MediaPlaybackSession session)
    {
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
    }

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
            if (args.Request.StreamDescriptor is not VideoStreamDescriptor || args.Request.Sample is not { } sample) return;
            _meter.Observe(sample.Timestamp, sample.Duration, _session.Position,
                _session.PlaybackState == MediaPlaybackState.Playing, sample.Discontinuous, Environment.TickCount64);
        }
        catch (Exception ex) { Unavailable(ex); }
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
