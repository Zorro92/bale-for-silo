using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// <see cref="PlaybackBackend"/> over Windows MediaPlayer. <see cref="Player"/> stays reachable for what only
/// MediaPlayer has: the MediaPlayerElement, its command manager (media remote), FFmpegInteropX's session hook
/// and the debug frame observers.
/// </summary>
internal sealed partial class MediaPlayerBackend : PlaybackBackend
{
    private readonly Session _session;
    private bool _disposed;

    public MediaPlayerBackend()
    {
        Player = new MediaPlayer { AutoPlay = false };
        _session = new Session(this, Player.PlaybackSession);
        Player.MediaOpened += OnOpened;
        Player.MediaEnded += OnEnded;
        Player.MediaFailed += OnFailed;
    }

    public MediaPlayer Player { get; }

    /// <summary>The item the page set as the player's source; its track lists back the track API.</summary>
    public MediaPlaybackItem? Item { get; set; }

    public override IReadOnlyList<PlaybackBackendTrack> AudioTracks
    {
        get
        {
            if (Item is not { } item) return [];
            return item.AudioTracks.Select(track =>
            {
                var format = track.GetEncodingProperties();
                return new PlaybackBackendTrack(track.Language, format.Subtype, format.ChannelCount, track.Name,
                    track.SupportInfo.DecoderStatus is MediaDecoderStatus.FullySupported or MediaDecoderStatus.Degraded);
            }).ToArray();
        }
    }

    public override int SelectedAudioIndex => Item?.AudioTracks.SelectedIndex ?? -1;

    public override bool SelectAudio(int index)
    {
        if (Item is not { } item || index < 0 || index >= item.AudioTracks.Count) return false;
        item.AudioTracks.SelectedIndex = index;
        return item.AudioTracks.SelectedIndex == index;
    }

    public override int VideoTrackCount => Item?.VideoTracks.Count ?? 0;

    public override int SelectedVideoIndex => Item?.VideoTracks.SelectedIndex ?? -1;

    public override bool SelectVideo(int index)
    {
        if (Item is not { } item || index < 0 || index >= item.VideoTracks.Count) return false;
        item.VideoTracks.SelectedIndex = index;
        return item.VideoTracks.SelectedIndex == index;
    }

    public override PlaybackBackendSession PlaybackSession => _session;

    public override string Name => "MediaPlayer";

    public override void Play()
    {
        Smoothness.Observe(PlaybackSmoothnessEvent.Play, ClockSeconds);
        Player.Play();
    }

    public override void Pause()
    {
        SampleSmoothnessPosition();
        Smoothness.Observe(PlaybackSmoothnessEvent.Pause, ClockSeconds);
        Player.Pause();
    }

    private void OnOpened(MediaPlayer sender, object args) => RaiseOpened();

    private void OnEnded(MediaPlayer sender, object args)
    {
        SampleSmoothnessPosition();
        Smoothness.Observe(PlaybackSmoothnessEvent.Ended, ClockSeconds);
        RaiseEnded();
    }

    internal override PlaybackSmoothnessSnapshot ReadSmoothness(double? framesPerSecond)
    {
        SampleSmoothnessPosition();
        // WinRT MediaPlayer has no renderer-drop counter. Keep its stall diagnostics, but do not claim a
        // complete missed-frame total when this attempt fell back from IMFMediaEngine.
        return base.ReadSmoothness(framesPerSecond) with { MissedFrames = null, MissedPercent = null };
    }

    private void SampleSmoothnessPosition()
    {
        // State/seek callbacks can already be in flight when Dispose detaches them.
        try { Smoothness.ObservePosition(_session.Position.TotalSeconds); }
        catch (Exception) { }
    }

    private void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        int hresult;
        MediaPlayerError error;
        try
        {
            hresult = args.ExtendedErrorCode?.HResult ?? 0;
            error = args.Error;
        }
        catch (Exception)
        {
            hresult = 0;
            error = MediaPlayerError.Unknown;
        }
        RaiseFailed(new PlaybackBackendFailure(hresult,
            Decoder: error is MediaPlayerError.DecodingError or MediaPlayerError.SourceNotSupported,
            Network: error == MediaPlayerError.NetworkError));
    }

    /// <summary>Detaches every forwarder, pauses, drops the source and disposes the player (callers detach the element first).</summary>
    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SampleSmoothnessPosition();
        Smoothness.Observe(PlaybackSmoothnessEvent.Ended, ClockSeconds);
        Step(() =>
        {
            Player.MediaOpened -= OnOpened;
            Player.MediaEnded -= OnEnded;
            Player.MediaFailed -= OnFailed;
        });
        Step(_session.Detach);
        Step(Player.Pause);
        Step(() => Player.Source = null);
        Step(Player.Dispose);
    }

    private static void Step(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Info($"player: MediaPlayer teardown {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}"); }
    }

    private sealed class Session : PlaybackBackendSession
    {
        private readonly MediaPlaybackSession _native;
        private readonly MediaPlayerBackend _owner;
        private readonly TypedEventHandler<MediaPlaybackSession, object> _state, _seek, _bufferingStarted, _bufferingEnded, _size, _rate;

        public Session(MediaPlayerBackend owner, MediaPlaybackSession native)
        {
            _owner = owner;
            _native = native;
            _state = (_, _) =>
            {
                try
                {
                    var state = _native.PlaybackState;
                    owner.SampleSmoothnessPosition();
                    if (state == MediaPlaybackState.Playing) Observe(PlaybackSmoothnessEvent.Playing);
                    else if (state == MediaPlaybackState.Paused) Observe(PlaybackSmoothnessEvent.Pause);
                }
                catch (Exception) { } // A native callback may outlive detachment during Dispose.
                RaiseStateChanged();
            };
            _seek = (_, _) =>
            {
                Observe(PlaybackSmoothnessEvent.Seeked);
                owner.SampleSmoothnessPosition();
                try
                {
                    if (_native.PlaybackState == MediaPlaybackState.Playing) Observe(PlaybackSmoothnessEvent.Playing);
                }
                catch (Exception) { }
                RaiseSeekCompleted();
            };
            _bufferingStarted = (_, _) => { Observe(PlaybackSmoothnessEvent.BufferingStarted); RaiseBufferingStarted(); };
            _bufferingEnded = (_, _) => { Observe(PlaybackSmoothnessEvent.BufferingEnded); RaiseBufferingEnded(); };
            _size = (_, _) => RaiseNaturalVideoSizeChanged();
            _rate = (_, _) => RaisePlaybackRateChanged();
            native.PlaybackStateChanged += _state;
            native.SeekCompleted += _seek;
            native.BufferingStarted += _bufferingStarted;
            native.BufferingEnded += _bufferingEnded;
            native.NaturalVideoSizeChanged += _size;
            native.PlaybackRateChanged += _rate;
        }

        private void Observe(PlaybackSmoothnessEvent kind)
        {
            var network = false;
#if DEBUG_SERVER
            try { network = _owner.NetworkWaiting?.Invoke() == true; }
            catch (Exception) { }
#endif
            _owner.Smoothness.Observe(kind, ClockSeconds, network);
        }

        /// <summary>The native session, for FFmpegInteropX's FastSeek hook and the debug frame observers.</summary>
        public MediaPlaybackSession Native => _native;

        public override TimeSpan Position
        {
            get => _native.Position;
            set
            {
                // MediaOpened sets the start even when already there; a no-op need not raise SeekCompleted.
                if (Math.Abs(_native.Position.TotalSeconds - value.TotalSeconds) > 0.000001)
                    Observe(PlaybackSmoothnessEvent.Seeking);
                _native.Position = value;
            }
        }

        public override MediaPlaybackState PlaybackState => _native.PlaybackState;

        public override bool CanSeek => _native.CanSeek;

        public override double PlaybackRate => _native.PlaybackRate;

        public override uint NaturalVideoWidth => _native.NaturalVideoWidth;

        public override uint NaturalVideoHeight => _native.NaturalVideoHeight;

        public override IReadOnlyList<(double Start, double End)> SeekableRanges() =>
            _native.GetSeekableRanges().Select(range => (range.Start.TotalSeconds, range.End.TotalSeconds)).ToArray();

        public void Detach()
        {
            _native.PlaybackStateChanged -= _state;
            _native.SeekCompleted -= _seek;
            _native.BufferingStarted -= _bufferingStarted;
            _native.BufferingEnded -= _bufferingEnded;
            _native.NaturalVideoSizeChanged -= _size;
            _native.PlaybackRateChanged -= _rate;
        }
    }

    /// <summary>The native session behind a backend session this class created.</summary>
    public static MediaPlaybackSession? NativeSession(PlaybackBackendSession session) => (session as Session)?.Native;
}
