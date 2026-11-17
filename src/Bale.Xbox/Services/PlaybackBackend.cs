using Windows.Media.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// The player a mount drives: Windows MediaPlayer today (<see cref="MediaPlayerBackend"/>), IMFMediaEngine next
/// (owner, 2026-11-17: the engine becomes the main path). Shaped like MediaPlayer so the page's transport, seek,
/// subtitle-clock and Up Next code is shared. Events may arrive on any thread, as MediaPlayer's do: handlers
/// marshal to the UI thread themselves. Plain C#, so the Linux tests compile it with their own backend.
/// </summary>
internal abstract partial class PlaybackBackend : IDisposable
{
    public abstract PlaybackBackendSession PlaybackSession { get; }

    /// <summary>A short name for logs and stats ("MediaPlayer", "IMFMediaEngine").</summary>
    public abstract string Name { get; }

    public abstract void Play();

    public abstract void Pause();

    public event Action<PlaybackBackend, object?>? MediaOpened;
    public event Action<PlaybackBackend, object?>? MediaEnded;
    public event Action<PlaybackBackend, PlaybackBackendFailure>? MediaFailed;

    /// <summary>
    /// The mounted audio tracks in the player's own order (not catalog order), valid once opened. FFmpeg mounts
    /// keep FFmpegInteropX's AudioStreams order, which the page maps to catalog ordinals.
    /// </summary>
    public virtual IReadOnlyList<PlaybackBackendTrack> AudioTracks => [];

    public virtual int SelectedAudioIndex => -1;

    /// <summary>Selects mounted audio track <paramref name="index"/>; true when the player now reports it selected.</summary>
    public virtual bool SelectAudio(int index) => false;

    public virtual int VideoTrackCount => 0;

    public virtual int SelectedVideoIndex => -1;

    /// <summary>Selects mounted video track <paramref name="index"/> (the DV7 base layer); true when it is now selected.</summary>
    public virtual bool SelectVideo(int index) => false;

    protected void RaiseOpened() => MediaOpened?.Invoke(this, null);
    protected void RaiseEnded() => MediaEnded?.Invoke(this, null);
    protected void RaiseFailed(PlaybackBackendFailure failure) => MediaFailed?.Invoke(this, failure);

    /// <summary>Stops the pipeline and releases it. The source objects the mount built are the mount's to release.</summary>
    public abstract void Dispose();
}

/// <summary>
/// One mounted track as the player reports it: language tag, codec (a subtype name such as "AAC"), channel count,
/// the label the player gives it, and whether its decoder can play it.
/// </summary>
internal sealed record PlaybackBackendTrack(string? Language, string? Codec, long? Channels, string? Name, bool Decodable);

/// <summary>Why playback failed: an HRESULT (0 when unknown) and whether the decoder or the network is to blame.</summary>
internal sealed record PlaybackBackendFailure(int HResult, bool Decoder, bool Network);

/// <summary>The clock and state of a <see cref="PlaybackBackend"/>, shaped like MediaPlaybackSession.</summary>
internal abstract class PlaybackBackendSession
{
    /// <summary>The player position; setting it seeks, and <see cref="SeekCompleted"/> follows.</summary>
    public abstract TimeSpan Position { get; set; }

    public abstract MediaPlaybackState PlaybackState { get; }

    public abstract bool CanSeek { get; }

    public abstract double PlaybackRate { get; }

    public abstract uint NaturalVideoWidth { get; }

    public abstract uint NaturalVideoHeight { get; }

    /// <summary>Seekable player-time ranges in seconds.</summary>
    public abstract IReadOnlyList<(double Start, double End)> SeekableRanges();

    public event Action<PlaybackBackendSession, object?>? PlaybackStateChanged;
    public event Action<PlaybackBackendSession, object?>? SeekCompleted;
    public event Action<PlaybackBackendSession, object?>? BufferingStarted;
    public event Action<PlaybackBackendSession, object?>? BufferingEnded;
    public event Action<PlaybackBackendSession, object?>? NaturalVideoSizeChanged;
    public event Action<PlaybackBackendSession, object?>? PlaybackRateChanged;

    /// <summary>How many handlers watch the state (tests check that every waiter unsubscribes).</summary>
    internal int StateSubscriberCount => PlaybackStateChanged?.GetInvocationList().Length ?? 0;

    protected void RaiseStateChanged() => PlaybackStateChanged?.Invoke(this, null);
    protected void RaiseSeekCompleted() => SeekCompleted?.Invoke(this, null);
    protected void RaiseBufferingStarted() => BufferingStarted?.Invoke(this, null);
    protected void RaiseBufferingEnded() => BufferingEnded?.Invoke(this, null);
    protected void RaiseNaturalVideoSizeChanged() => NaturalVideoSizeChanged?.Invoke(this, null);
    protected void RaisePlaybackRateChanged() => PlaybackRateChanged?.Invoke(this, null);
}
