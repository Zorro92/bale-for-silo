using System.Runtime.InteropServices.WindowsRuntime;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using FFmpegInteropX;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    private MediaPlayerElement? _video;
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
        public bool StartPaused { get; set; }
        public TaskCompletionSource<bool> Opening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        public FFmpegMediaSource? FfmpegSource { get; set; }
        public PlaybackResources.MediaDevice? RendererDevice { get; set; }
        public PlaybackRouteDecision Route { get; set; } = null!;
        // Set by StartSubtitles for an in-container image track: the FFmpeg source feeds the overlay.
        public int? SubtitleStreamIndex { get; set; }
        public ulong SubtitleSinkToken { get; set; }
        public MediaPlaybackItem? Item { get; set; }
        public AudioTrack? SelectedAudioTrack { get; set; }
#if DEBUG_SERVER
        public string? FfmpegReadAhead { get; set; }
        public string? FfmpegStreamReads { get; set; }
        public string? FfmpegHdrSupport { get; set; }
        public string? FfmpegDecoderOverride { get; set; }
        public string? FfmpegToneMapOverride { get; set; }
        public bool SoftwareVideo { get; set; }
        public string? TrackVideoMediaType { get; set; }
        public DebugVideoMediaType? MssVideoMediaType { get; set; }
        public IDebugFrameDelivery? FrameDelivery { get; set; }
        public TimeSpan? CpuTime { get; set; }
        public long? CpuTimestamp { get; set; }
        public string? LastCpuFailure { get; set; }
        public PlaybackThroughputMeter Throughput { get; } = new();
        public int BufferingCount { get; set; }
        public long BufferingTicks { get; set; }
        public long? BufferingSince { get; set; }
#endif
    }

    private async Task MountPlanAsync(Attempt attempt, double playerStart, bool paused = false)
    {
        // Every accepted plan mounts here: the start, the failure replan and each seek reanchor.
        // The line carries protocol ids only (PlaybackPlanLog), never the URL or warning text.
        Log.Info("player: " + PlaybackPlanLog.Describe(attempt.Plan));
#if DEBUG_SERVER
        if (attempt.PretendHdr)
        {
            // Microsoft hevc-xbox: HDR10 can be tone-mapped by the media pipeline in SDR mode.
            // This explicit comparison never requests PQ or replans for an unavailable HDR mode.
            Log.Info("player: HDR output: " + DebugHdrPlayback.Output(true, attempt.Plan));
        }
        else
#endif
        if (DisplayHdrPolicy.RequiresHdr10(attempt.Plan!))
        {
#if DEBUG_SERVER
            var hdrEnabled = attempt.MatchContentFrameRate
                ? await _hdrOutput.TryMatchContentAsync(attempt.Plan!)
                : await _hdrOutput.TryEnableHdr10Async();
            if (!hdrEnabled)
#else
            if (!await _hdrOutput.TryEnableHdr10Async())
#endif
            {
                attempt.Cancellation.Token.ThrowIfCancellationRequested();
                await RecoverCoreAsync(attempt, 0, isDecoder: false, isNetwork: false, hdrOutputFailed: true);
                return;
            }
        }
#if DEBUG_SERVER
        else if (attempt.MatchContentFrameRate) await _hdrOutput.TryMatchContentAsync(attempt.Plan!);
#endif
        else await _hdrOutput.RestoreAsync();
        // Back/suspend may arrive during the HDMI handshake. Do not open or start that mount.
        attempt.Cancellation.Token.ThrowIfCancellationRequested();
        // A kept overlay (seek reanchor) must not show the previous mount's track over this one;
        // StartSubtitles below reloads this plan's track for the new mount.
        ClearSubtitles();
        var transport = attempt.Client.Playback.OpenMedia(attempt.Plan!);
        var media = attempt.Media = new Mount(transport, attempt.Cancellation.Token)
        {
            StartPosition = playerStart,
            StartPaused = paused,
            Route = PlaybackRoutePolicy.Decide(attempt.Plan!, transport.IsHls, attempt.FfmpegEnabled, attempt.ForceFfmpeg),
        };
        _delivery = transport.Delivery;
        _duration = transport.DurationSeconds;
        StartSubtitles(attempt, media);
#if DEBUG_SERVER
        ResetStats(attempt);
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
#if DEBUG_SERVER
            var prefetch = media.Route.UseFfmpeg ? attempt.FfmpegTuning.Prefetch ?? HttpRangeStream.DefaultPrefetchBlocks
                : HttpRangeStream.DefaultPrefetchBlocks;
            media.Stream = await transport.OpenStreamAsync(media.Token, prefetch);
#else
            media.Stream = await transport.OpenStreamAsync(media.Token);
#endif
            media.Token.ThrowIfCancellationRequested();
            if (media.Route.UseFfmpeg)
            {
                media.RandomStream = attempt.UsePooledStreamAdapter
                    ? new RangeRandomAccessStream(media.Stream) : media.Stream.AsRandomAccessStream();
                if (!attempt.StreamAdapterLogged)
                {
                    Log.Info($"player: stream adapter={(attempt.UsePooledStreamAdapter ? "pooled" : "builtin")}");
                    attempt.StreamAdapterLogged = true;
                }
                // Keep auth, profile ownership and range cancellation in Core; FFmpeg receives bytes, not a bare URI.
                Log.Info("player: FFmpeg before create stream");
#if DEBUG_SERVER
                var config = FfmpegSource.Configuration(media.Route, attempt.FfmpegTuning);
                media.SoftwareVideo = attempt.FfmpegTuning.SoftwareVideo(media.Route.SoftwareVideo);
                media.FfmpegDecoderOverride = attempt.FfmpegTuning.DecoderOverride;
                media.FfmpegToneMapOverride = attempt.FfmpegTuning.ToneMapOverride;
                // Read actual native properties, not the current override (which may have changed since this attempt began).
                media.FfmpegReadAhead = DebugFfmpegInputTuning.ReadAheadText(config.General.ReadAheadBufferEnabled,
                    config.General.ReadAheadBufferSize, config.General.ReadAheadBufferDuration);
                media.FfmpegStreamReads = DebugFfmpegInputTuning.StreamReadsText(config.General.FileStreamReadSize, media.Stream.PrefetchBlocks);
                media.FfmpegHdrSupport = config.Video.HdrSupport.ToString();
                if (!attempt.FfmpegTuningLogged)
                {
                    Log.Info($"player: FFmpeg input read-ahead={media.FfmpegReadAhead} · readsize={media.FfmpegStreamReads} · hdr={media.FfmpegHdrSupport} · decoder={media.FfmpegDecoderOverride} decoderMode={config.Video.VideoDecoderMode} · tonemap={media.FfmpegToneMapOverride}");
                    attempt.FfmpegTuningLogged = true;
                }
#else
                var config = FfmpegSource.Configuration(media.Route);
#endif
                FfmpegSource.ApplySubtitleSink(config, media.SubtitleStreamIndex, media.SubtitleSinkToken);
                media.FfmpegSource = await FFmpegMediaSource.CreateFromStreamAsync(media.RandomStream, config).AsTask();
                media.Token.ThrowIfCancellationRequested();
#if DEBUG_SERVER
                FfmpegSource.ValidateVideoDecoders(media.FfmpegSource, media.Route, attempt.FfmpegTuning);
#else
                FfmpegSource.ValidateVideoDecoders(media.FfmpegSource, media.Route);
#endif
                Log.Info("player: FFmpeg stream created; before item");
                media.Item = media.FfmpegSource.CreateMediaPlaybackItem();
                // Low-frequency Starting only, no sample observer or rendering state changes.
                media.RendererDevice = new PlaybackResources.MediaDevice(media.FfmpegSource);
#if DEBUG_SERVER
                try { media.MssVideoMediaType = new DebugVideoMediaType(media.FfmpegSource, config.Video.VideoDecoderMode); }
                catch (Exception ex) { Log.Info($"player: video media type route=ffmpeg source=mss unavailable ({ex.GetType().Name})"); }
#endif
                // The MSS only exists after CreateMediaPlaybackItem and is kept alive by the item.
                // FFmpegInteropX sets BufferTime 0, so the renderer pulls decoded frames only ~0.3 s ahead.
                int? bufferOverrideMs = null;
                var softwareVideo = media.Route.SoftwareVideo;
#if DEBUG_SERVER
                bufferOverrideMs = attempt.FfmpegTuning.BufferMs;
                softwareVideo = media.SoftwareVideo;
#endif
                if (SoftwareDecodeTuning.BufferTimeMs(softwareVideo, bufferOverrideMs,
                        media.Route.VideoWidth, media.Route.VideoHeight) is { } bufferMs)
                {
                    var mss = media.FfmpegSource.GetMediaStreamSource();
                    if (mss is null) Log.Info("player: FFmpeg MediaStreamSource unavailable; BufferTime setting skipped");
                    else
                    {
                        mss.BufferTime = TimeSpan.FromMilliseconds(bufferMs);
                        Log.Info($"player: FFmpeg MediaStreamSource BufferTime={bufferMs} ms");
                    }
                }
            }
            else
            {
                media.RandomStream = media.Stream.AsRandomAccessStream();
                media.Source = MediaSource.CreateFromStream(media.RandomStream, transport.MimeType ?? "video/mp4");
            }
        }

        media.Token.ThrowIfCancellationRequested();
        var player = media.Player = new MediaPlayer { AutoPlay = false };
        player.MediaOpened += OnMediaOpened;
        player.MediaFailed += OnMediaFailed;
        player.MediaEnded += OnMediaEnded;
        player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        AttachSubtitleSession(player.PlaybackSession);
        _video ??= new MediaPlayerElement { AreTransportControlsEnabled = false, Stretch = Stretch.Uniform, IsTabStop = false };
        VideoHost.Child = _video;
        _video.SetMediaPlayer(player);
        // Subscribe before Source: native opening/decoding can fail before MediaOpened is raised.
#if DEBUG_SERVER
        player.PlaybackSession.BufferingStarted += OnStatsBufferingStarted;
        player.PlaybackSession.BufferingEnded += OnStatsBufferingEnded;
        Log.Info($"player: frame delivery observer={attempt.FrameDeliveryMode}");
        if (attempt.FrameDeliveryMode != "off")
        {
            if (media.SoftwareVideo && media.FfmpegSource is { } softwareSource)
            {
                try
                {
                    media.FrameDelivery = attempt.FrameDeliveryMode == "managed"
                        ? new DebugFrameDelivery(softwareSource, player.PlaybackSession, stream: media.Stream)
                        : new NativeFrameDelivery(softwareSource, player.PlaybackSession, stream: media.Stream);
                }
                catch (Exception ex) { LogFailure("frame delivery observer", ex); }
            }
            else Log.Info($"player: frame delivery not attached softwareVideo={media.SoftwareVideo} ffmpeg={media.FfmpegSource is not null}");
        }
#endif
        // The item exposes native track selection in production as well as debug stats.
        media.Item ??= new MediaPlaybackItem(media.Source);
        player.Source = media.Item;
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
            if (media.FfmpegSource is { } ffmpeg)
            {
                ffmpeg.PlaybackSession = sender.PlaybackSession;
            }
            if (attempt.Plan?.Delivery == "original_http")
                SelectOriginalAudioTrack(attempt, media, attempt.Plan.SelectedTracks?.Audio?.Index);
            else if (media.FfmpegSource is { } packaged)
            {
                // A server remux carries only its chosen stream; catalog ordinals do not index it.
                var streams = packaged.AudioStreams.Select(stream => stream.StreamIndex).ToArray();
                if (media.Item!.AudioTracks.Count != streams.Length)
                    throw new InvalidDataException("The planned audio tracks could not be mapped.");
                var fileId = attempt.Plan?.Source?.MediaFileId ?? attempt.Start?.FileId;
                var expectedCount = attempt.Request.Detail.Versions.FirstOrDefault(version => version.FileId == fileId)?.AudioTracks?.Count;
                if (attempt.Plan?.Delivery == "server_remux_progressive") expectedCount = 1;
                // FFmpeg may omit an undecodable stream. Do not let that shift Silo's audio ordinals.
                var index = PlaybackRoutePolicy.AudioTrackIndex(media.Route.AudioOrdinal, streams, expectedCount is > 0 ? expectedCount : null);
                if (index >= 0) media.Item.AudioTracks.SelectedIndex = index;
            }
            // TvVideoPlaybackStarter.kt's player coordinate may be zero on reanchored HLS even
            // when the source is minutes into the title. Set it once, only after native opening.
            sender.PlaybackSession.Position = TimeSpan.FromSeconds(media.StartPosition);
            media.Opened = true;
            Log.Info("player: opened");
#if DEBUG_SERVER
            ReadTrackVideoMediaType(media);
            RefreshStats(attempt);
#endif
            // The natural video size is known from here; NaturalVideoSizeChanged covers later changes.
            UpdateSubtitleLayout();
            if (media.StartPaused) media.Started = true;
            else sender.Play();
            UpdatePlaybackState(attempt, media);
            media.Opening.TrySetResult(true);
        }
        catch (Exception ex)
        {
            LogFailure("opened", ex);
            QueueFailure(attempt, media, ex.HResult, isDecoder: false, IsNetworkFailure(ex),
                ex is PlaybackAudioSelectionException audio ? audio.Failure : null);
        }
    });

    // Reusable by a future audio picker: planIndex always addresses this original's catalog,
    // even when Media Foundation or FFmpeg expose tracks in a different order. Call on the UI thread.
    private void SelectOriginalAudioTrack(Attempt attempt, Mount media, long? planIndex)
    {
        try
        {
            if (attempt.Plan?.Delivery != "original_http" || media.Item is not { } item)
                throw new PlaybackAudioSelectionException();
            var fileId = attempt.Plan.Source?.MediaFileId ?? attempt.Plan.EffectiveMediaFileId;
            var catalog = attempt.Request.Detail.Versions.FirstOrDefault(version => version.FileId == fileId)?.AudioTracks;
            var ffmpeg = media.FfmpegSource;
            if (ffmpeg is not null && ffmpeg.AudioStreams.Count != item.AudioTracks.Count)
                throw new PlaybackAudioSelectionException();
            var mounted = item.AudioTracks.Select((track, index) =>
            {
                var format = track.GetEncodingProperties();
                var stream = ffmpeg?.AudioStreams[index];
                // AudioStreamInfo.Language is a localized DISPLAY name in FFmpegInteropX;
                // the Media Foundation descriptor keeps its BCP 47 tag. Prefer that tag.
                var language = string.IsNullOrWhiteSpace(track.Language) ? stream?.Language : track.Language;
                return new MountedPlaybackAudioTrack(stream?.StreamIndex, language,
                    stream?.CodecName ?? format.Subtype, stream?.Channels ?? (long)format.ChannelCount,
                    // Windows' Label may be a generated language name, not an embedded title.
                    stream?.Name ?? track.Name, track.SupportInfo.DecoderStatus is MediaDecoderStatus.FullySupported or MediaDecoderStatus.Degraded);
            }).ToArray();
            PlaybackOriginalAudioSelection.Select(planIndex, catalog, mounted, ffmpeg is not null, index =>
            {
                if (media.SelectedAudioTrack is { } prior) prior.OpenFailed -= OnAudioTrackOpenFailed;
                media.SelectedAudioTrack = item.AudioTracks[index];
                media.SelectedAudioTrack.OpenFailed += OnAudioTrackOpenFailed;
                item.AudioTracks.SelectedIndex = index;
            }, () => item.AudioTracks.SelectedIndex);
        }
        catch (PlaybackAudioSelectionException) { throw; }
        catch (Exception) { throw new PlaybackAudioSelectionException(); }
    }

    private void OnAudioTrackOpenFailed(AudioTrack sender, AudioTrackOpenFailedEventArgs args) => _ = DispatchAsync(() =>
    {
        if (_attempt is { Media: { } media } attempt && ReferenceEquals(media.SelectedAudioTrack, sender))
            QueueFailure(attempt, media, 0, isDecoder: false, isNetwork: false,
                new PlaybackAudioSelectionException(decoderUnsupported: true).Failure);
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
        // Playing vs paused/buffering changes how the subtitle overlay extrapolates its clock.
        PushSubtitleClock();
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
        // MSS exposes MF's renderer device even for ForceSystemDecoder. Hold it across Close so
        // Trim happens after MF/FFmpeg have released their decoder surfaces and texture pools.
        var rendererDevice = media.RendererDevice?.TakeForClose() ?? 0;
        media.RendererDevice = null;
        Cleanup("cancel mount", media.Cancellation.Cancel);
        media.Opening.TrySetCanceled(media.Token);
        if (media.SelectedAudioTrack is { } audio)
            Cleanup("detach audio failure", () => audio.OpenFailed -= OnAudioTrackOpenFailed);
#if DEBUG_SERVER
        Cleanup("detach video media type", () => media.MssVideoMediaType?.Dispose());
        Cleanup("detach frame delivery", () => media.FrameDelivery?.Dispose());
        media.MssVideoMediaType = null;
        media.FrameDelivery = null;
#endif
        if (media.Player is { } player)
        {
            Cleanup("detach callbacks", () =>
            {
                player.MediaOpened -= OnMediaOpened;
                player.MediaFailed -= OnMediaFailed;
                player.MediaEnded -= OnMediaEnded;
                player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
                DetachSubtitleSession(player.PlaybackSession);
#if DEBUG_SERVER
                player.PlaybackSession.BufferingStarted -= OnStatsBufferingStarted;
                player.PlaybackSession.BufferingEnded -= OnStatsBufferingEnded;
#endif
            });
            Cleanup("pause pipeline", player.Pause);
            Cleanup("detach source", () => player.Source = null);
            Cleanup("detach element", () => _video?.SetMediaPlayer(null));
            Cleanup("dispose player", player.Dispose);
        }
        Cleanup("release video presenter", ReleaseVideoPresenter);
        if (media.Adaptive is { } adaptive && media.DownloadHandler is { } handler)
            Cleanup("detach downloads", () => adaptive.DownloadRequested -= handler);
        // FFmpeg owns an MSS, but CreateMediaPlaybackItem also creates a MediaSource. Closing
        // only FFmpeg does not close that source's MF pipeline (including system HEVC surfaces).
        Cleanup("dispose item source", () => media.Item?.Source?.Dispose());
        Cleanup("dispose source", () => media.Source?.Dispose());
        // The native source owns its MSS/item and must outlive the player, but not the authenticated stream.
        Cleanup("dispose FFmpeg source", () => media.FfmpegSource?.Dispose());
        Cleanup("dispose adaptive", () => media.Adaptive?.Dispose());
        Cleanup("dispose random stream", () => media.RandomStream?.Dispose());
        Cleanup("dispose stream", () => media.Stream?.Dispose());
        Cleanup("dispose playlist input", () => media.PlaylistInput?.Dispose());
        Cleanup("dispose playlist", () => media.Playlist?.Dispose());
        media.Player = null;
        media.Item = null;
        media.Source = null;
        media.SelectedAudioTrack = null;
        media.FfmpegSource = null;
        media.Adaptive = null;
        media.DownloadHandler = null;
        media.RandomStream = null;
        media.Stream = null;
        media.PlaylistInput = null;
        media.Playlist = null;
        PlaybackResources.ReleaseMediaDevice(rendererDevice);
        media.Cancellation.Dispose();
#if DEBUG_SERVER
        Log.Info("player resources: mount released=player,item-source,ffmpeg,streams,observer,video-presenter renderer_device=" + (rendererDevice != 0 ? "released" : "unavailable"));
#endif
    }

    private void ReleaseVideoPresenter()
    {
        var video = _video;
        _video = null;
        VideoHost.Child = null;
        video?.SetMediaPlayer(null);
    }

    private static void Cleanup(string operation, Action action)
    {
        try { action(); }
        catch (Exception ex) { LogFailure(operation, ex); }
    }
}
