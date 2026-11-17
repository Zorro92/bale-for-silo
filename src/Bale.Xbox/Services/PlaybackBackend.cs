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

    protected void RaiseOpened() => MediaOpened?.Invoke(this, null);
    protected void RaiseEnded() => MediaEnded?.Invoke(this, null);
    protected void RaiseFailed(PlaybackBackendFailure failure) => MediaFailed?.Invoke(this, failure);

    /// <summary>Stops the pipeline and releases it. The source objects the mount built are the mount's to release.</summary>
    public abstract void Dispose();
}

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
