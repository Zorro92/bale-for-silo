using Windows.Foundation;
using Windows.Media.Playback;

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
        _session = new Session(Player.PlaybackSession);
        Player.MediaOpened += OnOpened;
        Player.MediaEnded += OnEnded;
        Player.MediaFailed += OnFailed;
    }

    public MediaPlayer Player { get; }

    public override PlaybackBackendSession PlaybackSession => _session;

    public override string Name => "MediaPlayer";

    public override void Play() => Player.Play();

    public override void Pause() => Player.Pause();

    private void OnOpened(MediaPlayer sender, object args) => RaiseOpened();

    private void OnEnded(MediaPlayer sender, object args) => RaiseEnded();

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
        private readonly TypedEventHandler<MediaPlaybackSession, object> _state, _seek, _bufferingStarted, _bufferingEnded, _size, _rate;

        public Session(MediaPlaybackSession native)
        {
            _native = native;
            _state = (_, _) => RaiseStateChanged();
            _seek = (_, _) => RaiseSeekCompleted();
            _bufferingStarted = (_, _) => RaiseBufferingStarted();
            _bufferingEnded = (_, _) => RaiseBufferingEnded();
            _size = (_, _) => RaiseNaturalVideoSizeChanged();
            _rate = (_, _) => RaisePlaybackRateChanged();
            native.PlaybackStateChanged += _state;
            native.SeekCompleted += _seek;
            native.BufferingStarted += _bufferingStarted;
            native.BufferingEnded += _bufferingEnded;
            native.NaturalVideoSizeChanged += _size;
            native.PlaybackRateChanged += _rate;
        }

        /// <summary>The native session, for FFmpegInteropX's FastSeek hook and the debug frame observers.</summary>
        public MediaPlaybackSession Native => _native;

        public override TimeSpan Position
        {
            get => _native.Position;
            set => _native.Position = value;
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
