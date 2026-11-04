using System.Runtime.InteropServices.WindowsRuntime;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;
#if DEBUG_SERVER
using FFmpegInteropX;
#endif

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    private sealed class Mount
    {
        public Mount(PlaybackMedia transport, CancellationToken cancellation)
        {
            Transport = transport;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            Token = Cancellation.Token;
        }

        public PlaybackMedia Transport { get; }
        public CancellationTokenSource Cancellation { get; }
        public CancellationToken Token { get; }
        public double StartPosition { get; set; }
        public bool Opened { get; set; }
        public bool Started { get; set; }
        public double? PlayingFrom { get; set; }
        public bool FailureQueued { get; set; }
        public MediaPlayer? Player { get; set; }
        public MediaSource? Source { get; set; }
        public HttpRangeStream? Stream { get; set; }
        public IRandomAccessStream? RandomStream { get; set; }
        public MemoryStream? Playlist { get; set; }
        public IInputStream? PlaylistInput { get; set; }
        public AdaptiveMediaSource? Adaptive { get; set; }
        public TypedEventHandler<AdaptiveMediaSource, AdaptiveMediaSourceDownloadRequestedEventArgs>? DownloadHandler { get; set; }
#if DEBUG_SERVER
        public FFmpegMediaSource? FfmpegSource { get; set; }
        public string FfmpegMode { get; set; } = "off";
        public TimeSpan? CpuTime { get; set; }
        public long? CpuTimestamp { get; set; }
        public string? LastCpuFailure { get; set; }
        public MediaPlaybackItem? Item { get; set; }
        public PlaybackThroughputMeter Throughput { get; } = new();
        public int BufferingCount { get; set; }
        public long BufferingTicks { get; set; }
        public long? BufferingSince { get; set; }
#endif
    }

    private async Task MountPlanAsync(Attempt attempt, double playerStart)
    {
        var transport = attempt.Client.Playback.OpenMedia(attempt.Plan!);
        var media = attempt.Media = new Mount(transport, attempt.Cancellation.Token) { StartPosition = playerStart };
        _delivery = transport.Delivery;
        _duration = transport.DurationSeconds;
#if DEBUG_SERVER
        ResetStats(attempt.Plan);
#endif
        // Delivery is a protocol identifier, never a URL or server-supplied diagnostic string.
        var delivery = _delivery is { Length: > 0 and < 80 } value
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? value : "unknown";
        Log.Info($"player: playable delivery={delivery}");
        if (transport.IsHls)
        {
            var (bytes, _) = await transport.GetBytesAsync(transport.Uri, null, null, media.Token);
            media.Playlist = new MemoryStream(bytes);
            media.PlaylistInput = media.Playlist.AsInputStream();
            var result = await AdaptiveMediaSource.CreateFromStreamAsync(media.PlaylistInput, transport.Uri,
                "application/vnd.apple.mpegurl");
            media.Adaptive = result.MediaSource;
            media.Token.ThrowIfCancellationRequested();
            if (result.Status != AdaptiveMediaSourceCreationStatus.Success || media.Adaptive is null)
                throw new InvalidDataException("The adaptive media source could not be opened.");
            media.DownloadHandler = (_, args) => OnDownloadRequested(attempt, media, args);
            media.Adaptive.DownloadRequested += media.DownloadHandler;
            media.Source = MediaSource.CreateFromAdaptiveMediaSource(media.Adaptive);
        }
        else
        {
            media.Stream = await transport.OpenStreamAsync(media.Token);
            media.Token.ThrowIfCancellationRequested();
            media.RandomStream = media.Stream.AsRandomAccessStream();
#if DEBUG_SERVER
            if (DebugFfmpegPlayback.UseForDirectStream(attempt.FfmpegMode, transport.IsHls))
            {
                // Keep auth, profile ownership and range cancellation in Core; FFmpeg receives bytes, not a bare URI.
                media.FfmpegMode = attempt.FfmpegMode;
                Log.Info($"player: FFmpeg {media.FfmpegMode} before create stream");
                var config = DebugFfmpegSource.Configuration(media.FfmpegMode);
                media.FfmpegSource = await FFmpegMediaSource.CreateFromStreamAsync(media.RandomStream, config).AsTask();
                media.Token.ThrowIfCancellationRequested();
                Log.Info("player: FFmpeg stream created; before item");
                media.Item = media.FfmpegSource.CreateMediaPlaybackItem();
            }
            else
#endif
            {
                media.Source = MediaSource.CreateFromStream(media.RandomStream, transport.MimeType ?? "video/mp4");
            }
        }

        media.Token.ThrowIfCancellationRequested();
        var player = media.Player = new MediaPlayer { AutoPlay = false };
        player.MediaOpened += OnMediaOpened;
        player.MediaFailed += OnMediaFailed;
        player.MediaEnded += OnMediaEnded;
        player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        Video.SetMediaPlayer(player);
        // Subscribe before Source: native opening/decoding can fail before MediaOpened is raised.
#if DEBUG_SERVER
        player.PlaybackSession.BufferingStarted += OnStatsBufferingStarted;
        player.PlaybackSession.BufferingEnded += OnStatsBufferingEnded;
        // The item exposes the actual selected native tracks, not the source/recipe's predictions.
        media.Item ??= new MediaPlaybackItem(media.Source);
        player.Source = media.Item;
#else
        player.Source = media.Source;
#endif
    }

    private async void OnDownloadRequested(Attempt attempt, Mount media, AdaptiveMediaSourceDownloadRequestedEventArgs args)
    {
        // Supplying every manifest/segment/key's bytes keeps profile proofs and bearer refresh in
        // Core. Never hand credentials to the native downloader, or follow a foreign child URI.
        try
        {
            var deferral = args.GetDeferral();
            try
            {
                media.Token.ThrowIfCancellationRequested();
                if (args.ResourceUri is not { } uri || !PlaybackMediaUrl.IsAllowedChild(uri, attempt.Client.Server))
                    throw new InvalidDataException("The adaptive resource is outside the playback server's media routes.");
                var (data, contentType) = await media.Transport.GetBytesAsync(uri,
                    (long?)args.ResourceByteRangeOffset, (long?)args.ResourceByteRangeLength, media.Token);
                args.Result.Buffer = data.AsBuffer();
                if (!string.IsNullOrWhiteSpace(contentType)) args.Result.ContentType = contentType;
            }
            catch (Exception ex)
            {
                // A failed interception must not fall through to an unauthenticated native fetch.
                try
                {
                    args.Result.Buffer = Array.Empty<byte>().AsBuffer();
                    args.Result.ExtendedStatus = unchecked((uint)ex.HResult);
                }
                catch (Exception resultError) { LogFailure("download result", resultError); }
                if (!media.Token.IsCancellationRequested)
                {
                    LogFailure("download", ex);
                    await DispatchAsync(() => QueueFailure(attempt, media, ex.HResult, isDecoder: false, IsNetworkFailure(ex)));
                }
            }
            finally { deferral.Complete(); }
        }
        catch (Exception ex)
        {
            // Even GetDeferral/Complete and result projection failures stay inside the handler.
            LogFailure("adaptive callback", ex);
            if (!media.Token.IsCancellationRequested)
                await DispatchAsync(() => QueueFailure(attempt, media, ex.HResult, isDecoder: false, IsNetworkFailure(ex)));
        }
    }

    private void OnMediaOpened(MediaPlayer sender, object args) => _ = DispatchAsync(() =>
    {
        if (_attempt is not { Media: { } media } attempt || !ReferenceEquals(media.Player, sender)
            || !IsCurrent(attempt, media) || media.FailureQueued) return;
        try
        {
#if DEBUG_SERVER
            if (media.FfmpegSource is { } ffmpeg) ffmpeg.PlaybackSession = sender.PlaybackSession;
#endif
            // TvVideoPlaybackStarter.kt's player coordinate may be zero on reanchored HLS even
            // when the source is minutes into the title. Set it once, only after native opening.
            sender.PlaybackSession.Position = TimeSpan.FromSeconds(media.StartPosition);
            media.Opened = true;
            Log.Info("player: opened");
            sender.Play();
            UpdatePlaybackState(attempt, media);
        }
        catch (Exception ex)
        {
            LogFailure("opened", ex);
            QueueFailure(attempt, media, ex.HResult, isDecoder: false, IsNetworkFailure(ex));
        }
    });

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        try
        {
            var hresult = args.ExtendedErrorCode?.HResult ?? 0;
            var decoder = args.Error is MediaPlayerError.DecodingError or MediaPlayerError.SourceNotSupported;
            var network = args.Error == MediaPlayerError.NetworkError;
            _ = DispatchAsync(() =>
            {
                if (_attempt is { Media: { } media } attempt && ReferenceEquals(media.Player, sender))
                    QueueFailure(attempt, media, hresult, decoder, network);
            });
        }
        catch (Exception ex) { LogFailure("failed callback", ex); }
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args) => _ = DispatchAsync(() =>
    {
        if (_attempt is { Media: { Player: { } player } media } attempt
            && ReferenceEquals(player.PlaybackSession, sender) && IsCurrent(attempt, media) && !media.FailureQueued)
            UpdatePlaybackState(attempt, media);
    });

    private void UpdatePlaybackState(Attempt attempt, Mount media)
    {
        var session = media.Player!.PlaybackSession;
        var state = session.PlaybackState;
        // ExoPlayer stays STATE_BUFFERING from prepare until ready, so PlayerSpinnerVisibility.kt sees
        // one continuous load. MediaPlayer reports Paused right after Play() and, after a start seek,
        // a brief Playing before Buffering; either restarted the 500 ms show delay mid-load. The first
        // load lasts until the clock moves while Playing (OnTick re-polls until then). Measure from the
        // first Playing sample, not StartPosition: the seek may land on an earlier keyframe.
        if (media.Opened && !media.Started && state == MediaPlaybackState.Playing)
        {
            var position = session.Position.TotalSeconds;
            media.PlayingFrom ??= position;
            if (position > media.PlayingFrom.Value + 0.05) media.Started = true;
        }
        _state = !media.Opened || !media.Started ? "loading" : state switch
        {
            MediaPlaybackState.Playing => "playing",
            MediaPlaybackState.Paused => "paused",
            MediaPlaybackState.Buffering => "buffering",
            _ => "loading",
        };
        SetLoading(_state is "loading" or "buffering");
        SetDisplayActive(_state != "paused");
    }

    private void OnMediaEnded(MediaPlayer sender, object args) => _ = DispatchAsync(() =>
    {
        if (_attempt is { Media: { } media } attempt && ReferenceEquals(media.Player, sender)
            && IsCurrent(attempt, media)) _ = StopAndGoBackAsync();
    });

    private void DisposeMount(Attempt attempt)
    {
        if (attempt.Media is not { } media) return;
        attempt.Media = null;
        media.Cancellation.Cancel();
        if (media.Player is { } player)
        {
            Cleanup("detach element", () => Video.SetMediaPlayer(null));
            Cleanup("detach callbacks", () =>
            {
                player.MediaOpened -= OnMediaOpened;
                player.MediaFailed -= OnMediaFailed;
                player.MediaEnded -= OnMediaEnded;
                player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
#if DEBUG_SERVER
                player.PlaybackSession.BufferingStarted -= OnStatsBufferingStarted;
                player.PlaybackSession.BufferingEnded -= OnStatsBufferingEnded;
#endif
            });
            Cleanup("pause pipeline", player.Pause);
            Cleanup("detach source", () => player.Source = null);
            Cleanup("dispose player", player.Dispose);
        }
        if (media.Adaptive is { } adaptive && media.DownloadHandler is { } handler)
            Cleanup("detach downloads", () => adaptive.DownloadRequested -= handler);
        Cleanup("dispose source", () => media.Source?.Dispose());
#if DEBUG_SERVER
        // The native source owns its MSS/item and must outlive the player, but not the authenticated stream.
        Cleanup("dispose FFmpeg source", () => media.FfmpegSource?.Dispose());
#endif
        Cleanup("dispose adaptive", () => media.Adaptive?.Dispose());
        Cleanup("dispose random stream", () => media.RandomStream?.Dispose());
        Cleanup("dispose stream", () => media.Stream?.Dispose());
        Cleanup("dispose playlist input", () => media.PlaylistInput?.Dispose());
        Cleanup("dispose playlist", () => media.Playlist?.Dispose());
        media.Cancellation.Dispose();
    }

    private static void Cleanup(string operation, Action action)
    {
        try { action(); }
        catch (Exception ex) { LogFailure(operation, ex); }
    }
}
