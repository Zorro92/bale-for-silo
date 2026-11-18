using System.Runtime.InteropServices.WindowsRuntime;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using FFmpegInteropX;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
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
        /// <summary>The player this mount drives (MediaPlayer today; IMFMediaEngine next).</summary>
        public PlaybackBackend? Player { get; set; }
        /// <summary>The Windows MediaPlayer behind <see cref="Player"/>, for what only it has (element, command manager).</summary>
        public MediaPlayer? NativePlayer => (Player as MediaPlayerBackend)?.Player;
        /// <summary>The media remote / Guide controls for a backend without MediaPlayer's command manager.</summary>
        public EngineSystemControls? SystemControls { get; set; }
        public MediaSource? Source { get; set; }
        public MediaRangeStream? Stream { get; set; }
        public IRandomAccessStream? RandomStream { get; set; }
#if DEBUG_SERVER
        /// <summary>A WinRT wrapper the debug engine player holds for the mount's lifetime.</summary>
        public object? EngineKeepAlive { get; set; }
#endif
        // server_remux_progressive: one forward-only response through FFmpeg's non-seekable input.
        public MediaProgressiveStream? Progressive { get; set; }
        public IInputStream? ForwardInput { get; set; }
        public bool Reconnecting { get; set; }
        public long? PausedAt { get; set; }
        public FFmpegMediaSource? FfmpegSource { get; set; }
        public PlaybackResources.MediaDevice? RendererDevice { get; set; }
        public FfmpegVideoOutputGuard? VideoOutputGuard { get; set; }
        public int? BaseLayerStreamIndex { get; set; }
        public PlaybackRouteDecision Route { get; set; } = null!;
        // Set by StartSubtitles for an in-container image track: the FFmpeg source feeds the overlay.
        public int? SubtitleStreamIndex { get; set; }
        public ulong SubtitleSinkToken { get; set; }
        public MediaPlaybackItem? Item { get; set; }
        public AudioTrack? SelectedAudioTrack { get; set; }
        /// <summary>Network throughput for the Stats tab's Estimated bandwidth row (one sample a second).</summary>
        public PlaybackThroughputMeter Throughput { get; } = new();
#if DEBUG_SERVER
        public string? FfmpegReadAhead { get; set; }
        public string? FfmpegStreamReads { get; set; }
        public string? FfmpegHdrSupport { get; set; }
        public string? FfmpegDecoderOverride { get; set; }
        public string? FfmpegToneMapOverride { get; set; }
        public string? FfmpegColorInfoOverride { get; set; }
        public bool SoftwareVideo { get; set; }
        public string? TrackVideoMediaType { get; set; }
        public DebugVideoMediaType? MssVideoMediaType { get; set; }
        public IDebugFrameDelivery? FrameDelivery { get; set; }
        public TimeSpan? CpuTime { get; set; }
        public long? CpuTimestamp { get; set; }
        public string? LastCpuFailure { get; set; }
        public int BufferingCount { get; set; }
        public long BufferingTicks { get; set; }
        public long? BufferingSince { get; set; }
#endif
    }

    private async Task MountPlanAsync(Attempt attempt, double playerStart, bool paused = false, bool rangeRefused = false)
    {
        // Every accepted plan mounts here: the start, the failure replan and each seek reanchor.
        // The line carries protocol ids only (PlaybackPlanLog), never the URL or warning text.
        Log.Info("player: " + PlaybackPlanLog.Describe(attempt.Plan));
        // Client-managed originals keep SDR HDMI; all other mounts retain native HDR policy.
        var sdrOutput = !ConsoleToneMapPolicy.HasHdrOutput(attempt.Start!.ClientPlaybackContext.Output.HdrDetails);
        var consoleToneMap = ConsoleToneMapPolicy.RequiresToneMap(attempt.Plan!, sdrOutput, attempt.Request.Detail.Versions,
            attempt.HlsThroughFfmpeg);
#if DEBUG_SERVER
        if (attempt.PretendHdr)
        {
            // Microsoft hevc-xbox: HDR10 can be tone-mapped by the media pipeline in SDR mode.
            // This explicit comparison never requests PQ or replans for an unavailable HDR mode.
            Log.Info("player: HDR output: " + DebugHdrPlayback.Output(true, attempt.Plan));
        }
        else
#endif
        if (!consoleToneMap && DisplayHdrPolicy.RequiresHdr10(attempt.Plan!))
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
            Route = PlaybackRoutePolicy.Decide(attempt.Plan!, transport.IsHls, attempt.FfmpegEnabled, attempt.ForceFfmpeg,
                sdrOutput: sdrOutput, versions: attempt.Request.Detail.Versions, hlsThroughFfmpeg: attempt.HlsThroughFfmpeg,
                hardwareDecode: attempt.HardwareDecode, display: DisplayHdrOutput.CurrentRawSize(),
                sdrDownscale: !attempt.SdrDownscaleDeclined && ConsoleToneMapPolicy.SdrDownscaleWrapperSupported(FfmpegWrapperVersion.Value)
                    && ConsoleToneMapRuntime.SupportsSdrDownscale),
        };
        if (media.Route.ConsoleToneMap && !media.Route.UseFfmpeg)
            throw new InvalidDataException("An HDR original on SDR requires the FFmpeg tone-map route.");
        if (media.Route.DolbyVision7BaseLayer)
        {
            if (!media.Route.UseFfmpeg) throw new InvalidDataException("DV7 requires FFmpeg base-layer decoding.");
            DolbyVision7Policy.ValidatePlan(attempt.Plan!);
        }
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
        // Every HLS package plays through the Rust HLS reader and FFmpeg; Windows' HLS source is gone (owner,
        // 2026-11-17: it cannot feed IMFMediaEngine, the player's future main path).
        if (transport.IsHls && !media.Route.UseFfmpeg) throw new InvalidDataException("HLS requires the FFmpeg route.");
        {
            if (transport.IsProgressive)
            {
                // PlaybackProgressive: no length, no ranges. Windows' sources need both; FFmpeg's
                // non-seekable input does not. Every seek is a server reanchor (PlayerPage.Seek.cs).
                if (!media.Route.UseFfmpeg) throw new InvalidDataException("A progressive remux requires the FFmpeg route.");
                try
                {
                    media.Progressive = attempt.UseRustTransport
                        ? await transport.OpenNativeProgressiveAsync(media.Token)
                        : await transport.OpenProgressiveAsync(media.Token);
                }
                catch (ProgressiveRangeRefusedException) when (!rangeRefused
                    && attempt.ServerFeatures.Contains(PlaybackSeekReanchor.Feature))
                {
                    // docs/playback-api.md: a 409 means "reopen the stream at its current position".
                    // Nothing has played from this mount, so that is the plan's own source start.
                    Log.Info("player: progressive range refused; reanchoring");
                    var decision = await RequestReanchorAsync(attempt, PlaybackTimeline.From(attempt.Plan!.Timeline).SourceStartSeconds);
                    if (decision is null || !ReferenceEquals(attempt.Media, media)) throw;
                    DisposeMount(attempt);
                    var kept = ClientBufferingReplan("seek_reanchor")?.BandwidthEstimateKbps;
                    attempt.Plan = decision.PlaybackPlan!;
                    attempt.ServerFeatures = decision.ServerFeatures ?? [];
                    AdoptBufferingPlan(attempt, controllerInitiated: false, kept);
                    await MountPlanAsync(attempt, 0, paused, rangeRefused: true);
                    return;
                }
                media.ForwardInput = NativeInputStream(media.Progressive) ?? new ForwardInputStream(media.Progressive);
            }
            else if (transport.IsHls)
            {
                // HLS through FFmpeg (docs/rust-port.md): the Rust reader's forward-only stream from the
                // segment holding playerStart. FFmpeg's clock reads 0 at that segment's start, so the
                // mount's timeline starts there; seeks remount at the target segment (PlayerPage.Seek.cs).
                // A playlist the reader refuses (HlsUnsupportedException) fails to a replan.
                var hls = await transport.OpenNativeHlsAsync(playerStart, media.Token);
                media.Progressive = hls;
                transport.StartPlayerClockAt(hls.SegmentStartSeconds);
                media.ForwardInput = NativeInputStream(media.Progressive) ?? new ForwardInputStream(media.Progressive);
            }
            else
            {
#if DEBUG_SERVER
                var prefetch = media.Route.UseFfmpeg ? attempt.FfmpegTuning.Prefetch ?? HttpRangeStream.DefaultPrefetchBlocks
                    : HttpRangeStream.DefaultPrefetchBlocks;
                media.Stream = attempt.UseRustTransport
                    ? await transport.OpenNativeStreamAsync(media.Token, prefetch)
                    : await transport.OpenStreamAsync(media.Token, prefetch);
#else
                // The Rust streams are the default (docs/rust-port.md, slice 7): reads never wait on the GC.
                media.Stream = await transport.OpenNativeStreamAsync(media.Token);
#endif
            }
            media.Token.ThrowIfCancellationRequested();
            if (media.Route.UseFfmpeg)
            {
                if (media.Stream is { } range)
                {
                    media.RandomStream = NativeRandomAccessStream(range)
                        ?? (attempt.UsePooledStreamAdapter ? new RangeRandomAccessStream(range) : range.AsRandomAccessStream());
                    if (!attempt.StreamAdapterLogged)
                    {
                        Log.Info($"player: stream adapter={(range is RustRangeStream ? "rust" : attempt.UsePooledStreamAdapter ? "pooled" : "builtin")}");
                        attempt.StreamAdapterLogged = true;
                    }
                }
                else Log.Info($"player: stream adapter={(media.Progressive is RustProgressiveStream ? transport.IsHls ? "rust-hls" : "rust-forward" : "forward")}");
                // Keep auth, profile ownership and range cancellation in Core; FFmpeg receives bytes, not a bare URI.
                Log.Info("player: FFmpeg before create stream");
#if DEBUG_SERVER
                var config = FfmpegSource.Configuration(media.Route, attempt.FfmpegTuning);
                media.SoftwareVideo = attempt.FfmpegTuning.SoftwareVideo(media.Route.SoftwareVideo);
                media.FfmpegDecoderOverride = attempt.FfmpegTuning.DecoderOverride;
                media.FfmpegToneMapOverride = attempt.FfmpegTuning.ToneMapOverride;
                media.FfmpegColorInfoOverride = attempt.FfmpegTuning.ColorInfoOverride;
                // Read actual native properties, not the current override (which may have changed since this attempt began).
                media.FfmpegReadAhead = DebugFfmpegInputTuning.ReadAheadText(config.General.ReadAheadBufferEnabled,
                    config.General.ReadAheadBufferSize, config.General.ReadAheadBufferDuration);
                media.FfmpegStreamReads = media.Stream is { } prefetched
                    ? DebugFfmpegInputTuning.StreamReadsText(config.General.FileStreamReadSize, prefetched.PrefetchBlocks)
                    : $"{config.General.FileStreamReadSize} B · forward";
                media.FfmpegHdrSupport = config.Video.HdrSupport.ToString();
                if (!attempt.FfmpegTuningLogged)
                {
                    Log.Info($"player: FFmpeg input read-ahead={media.FfmpegReadAhead} · readsize={media.FfmpegStreamReads} · hdr={media.FfmpegHdrSupport} · decoder={media.FfmpegDecoderOverride} decoderMode={config.Video.VideoDecoderMode} · tonemap={media.FfmpegToneMapOverride} · colorinfo={media.FfmpegColorInfoOverride}");
                    attempt.FfmpegTuningLogged = true;
                }
#else
                var config = FfmpegSource.Configuration(media.Route);
#endif
                FfmpegSource.ApplyDisplaySize(config, media.Route, DisplayHdrOutput.CurrentRawSize());
                FfmpegSource.ApplySubtitleSink(config, media.SubtitleStreamIndex, media.SubtitleSinkToken);
                var engineSource = attempt.UseEngine;
#if DEBUG_SERVER
                engineSource |= DebugPresentEngineSwitch.Seconds > 0;
#endif
                // The engine sets a forward-only mount's real remaining duration itself: IMFMediaEngine ends at the
                // duration it read at load, so FFmpegInteropX's few-second auto-extended estimate would stop it
                // (MediaPlayer follows the extensions).
                if (engineSource && media.Progressive is not null) config.General.AutoExtendDuration = false;
                // The engine gives FFmpegInteropX no MediaPlayer session; wrapper bale.30's opt-in FastSeek lands on
                // the keyframe without one (reported via SetActualStartPosition).
                if (engineSource) config.FFmpegOptions["bale_fast_seek_without_session"] = "1";
                media.FfmpegSource = media.ForwardInput is { } forward
                    ? await FFmpegMediaSource.CreateFromInputStreamAsync(forward, config).AsTask()
                    : await FFmpegMediaSource.CreateFromStreamAsync(media.RandomStream!, config).AsTask();
                media.Token.ThrowIfCancellationRequested();
                if (media.Route.DolbyVision7BaseLayer)
                {
                    var fileId = attempt.Plan!.Source?.MediaFileId ?? attempt.Plan.EffectiveMediaFileId;
                    var expectedCount = attempt.Request.Detail.Versions.FirstOrDefault(version => version.FileId == fileId)?.VideoTracks?.Count;
                    var streams = media.FfmpegSource.VideoStreams.Select(video => video.StreamIndex).ToArray();
                    var index = DolbyVision7Policy.BaseLayerTrackIndex(streams, expectedCount);
                    media.BaseLayerStreamIndex = streams[index];
                }
#if DEBUG_SERVER
                FfmpegSource.ValidateVideoDecoders(media.FfmpegSource, media.Route, attempt.FfmpegTuning);
#else
                FfmpegSource.ValidateVideoDecoders(media.FfmpegSource, media.Route);
#endif
                Log.Info("player: FFmpeg stream created; before item");
                media.Item = media.FfmpegSource.CreateMediaPlaybackItem();
                // Both stage routes must deliver the fork's marked SDR output, never an unmapped fallback.
                var guardToneMap = media.Route.ConsoleStage;
#if DEBUG_SERVER
                if (!media.Route.DolbyVision7BaseLayer)
                    guardToneMap = attempt.FfmpegTuning.RequiresToneMapGuard(media.Route);
#endif
                if (guardToneMap || media.Route.DolbyVision7BaseLayer)
                    media.VideoOutputGuard = new FfmpegVideoOutputGuard(media.FfmpegSource, () =>
                        _ = DispatchAsync(() => QueueFailure(attempt, media, 0, isDecoder: false, isNetwork: false)),
                        toneMap: guardToneMap, baseLayerStreamIndex: media.BaseLayerStreamIndex);

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
                media.RandomStream = NativeRandomAccessStream(media.Stream!) ?? media.Stream!.AsRandomAccessStream();
                if (media.Stream is RustRangeStream) Log.Info("player: stream adapter=rust");
                media.Source = MediaSource.CreateFromStream(media.RandomStream, transport.MimeType ?? "video/mp4");
            }
        }

        media.Token.ThrowIfCancellationRequested();
#if DEBUG_SERVER
        // After routing: the engine gets exactly the source this mount built (FFmpeg or Windows' reader).
        if (DebugPresentEngineSwitch.Seconds is > 0 and var presentSeconds)
        {
            await PresentThroughEngineAsync(media, transport.MimeType ?? "video/mp4", presentSeconds, attempt.Plan!);
            return;
        }
#endif
        if (attempt.UseEngine)
        {
            if (await MountEngineAsync(attempt, media, transport.MimeType ?? "video/mp4")) return;
            // The engine could not open this stream: this attempt plays on Windows MediaPlayer, from a fresh mount
            // (the engine may have started reading the source it was given).
            Log.Info("player: engine could not open this stream; remounting on MediaPlayer");
            attempt.EngineRefused = true;
            DisposeMount(attempt);
            await MountPlanAsync(attempt, playerStart, paused, rangeRefused);
            return;
        }
        var backend = new MediaPlayerBackend();
        var player = backend.Player;
        media.Player = backend;
        backend.MediaOpened += OnMediaOpened;
        backend.MediaFailed += OnMediaFailed;
        backend.MediaEnded += OnMediaEnded;
        backend.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        AttachSystemControls(player);
        AttachSubtitleSession(backend.PlaybackSession);
        _video ??= new MediaPlayerElement { AreTransportControlsEnabled = false, Stretch = Stretch.Uniform, IsTabStop = false };
        VideoHost.Child = _video;
        _video.SetMediaPlayer(player);
        // Subscribe before Source: native opening/decoding can fail before MediaOpened is raised.
#if DEBUG_SERVER
        backend.PlaybackSession.BufferingStarted += OnStatsBufferingStarted;
        backend.PlaybackSession.BufferingEnded += OnStatsBufferingEnded;
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
        backend.Item = media.Item;
        player.Source = media.Item;
    }

    /// <summary>
    /// Plays this mount's routed source through IMFMediaEngine (<see cref="EngineBackend"/>) with the page's own
    /// controls, Up Next, tracks and subtitles (owner, 2026-11-17: the engine is the main player). False when the
    /// engine could not open the stream; the caller then remounts on MediaPlayer.
    /// </summary>
    private async Task<bool> MountEngineAsync(Attempt attempt, Mount media, string mime)
    {
        ReleaseVideoPresenter();
        var engine = new EngineBackend(VideoHost);
        media.Player = engine;
        engine.MediaOpened += OnMediaOpened;
        engine.MediaFailed += OnMediaFailed;
        engine.MediaEnded += OnMediaEnded;
        engine.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        AttachSubtitleSession(engine.PlaybackSession);
        var detail = attempt.Request.Detail;
        var controls = media.SystemControls = new EngineSystemControls(PlaybackControlsText.Title(detail),
            detail.Type == "episode" ? detail.Title : null);
        controls.Command += command => SystemControl(engine, command switch
        {
            SystemControlCommand.Play => PlaybackControlRequest.Play,
            SystemControlCommand.Pause => PlaybackControlRequest.Pause,
            _ => null,
        }, command switch
        {
            SystemControlCommand.Forward => true,
            SystemControlCommand.Back => false,
            _ => null,
        });
#if DEBUG_SERVER
        engine.PlaybackSession.BufferingStarted += OnStatsBufferingStarted;
        engine.PlaybackSession.BufferingEnded += OnStatsBufferingEnded;
#endif
        object source;
        string? type = null;
        if (media.FfmpegSource is { } ffmpeg)
        {
            var mss = ffmpeg.GetMediaStreamSource();
            if (media.Progressive is not null && _duration is > 0 and var title)
            {
                // The engine ends at the duration it reads at load: give it the rest of the title from this
                // mount's origin instead of FFmpeg's estimate of what it has probed (AutoExtendDuration is off).
                var origin = media.Transport.Timeline.SourceForPlayer(0) ?? 0;
                mss.Duration = TimeSpan.FromSeconds(Math.Max(1, title - origin));
            }
            source = mss;
        }
        else if (media.RandomStream is { } random)
        {
            source = random;
            type = mime;
        }
        else throw new InvalidDataException("The engine has no source for this route.");
        Log.Info($"player: backend=engine route={RouteText(media)}");
        // Forward-only mounts open where they play (player 0); seekable ones at the mount's start.
        var opened = await engine.OpenAsync(source, type, canSeek: media.Progressive is null,
            start: media.Progressive is null ? media.StartPosition : 0);
        foreach (var line in engine.Report.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log.Info("engine: " + line);
        return opened || !ReferenceEquals(attempt.Media, media);
    }

    /// <summary>The mount's route in words, for the log: never a URL.</summary>
    private static string RouteText(Mount media)
    {
        var route = media.Route;
        return route.UseFfmpeg
            ? "ffmpeg " + (route.ConsoleToneMap ? "d3d11va + console tone map" : route.ConsoleDownscale ? "d3d11va + console downscale"
                : route.DolbyVision7BaseLayer ? "DV7 base layer"
                : route.SoftwareVideo ? "software decode" : route.DolbyVisionBaseLayer ? "system decoder, DV8 base layer" : "system decoder passthrough")
            : "Windows reader (direct)";
    }

    private void OnMediaOpened(PlaybackBackend sender, object? args) => _ = DispatchAsync(() =>
    {
        if (_attempt is not { Media: { } media } attempt || !ReferenceEquals(media.Player, sender)
            || !IsCurrent(attempt, media) || media.FailureQueued) return;
        try
        {
            if (media.FfmpegSource is { } ffmpeg)
            {
                // FastSeek needs MediaPlayer's session; other backends seek without one (wrapper bale.30).
                ffmpeg.PlaybackSession = MediaPlayerBackend.NativeSession(sender.PlaybackSession);
                if (media.BaseLayerStreamIndex is { } baseStream)
                {
                    var streams = ffmpeg.VideoStreams.Select(video => video.StreamIndex).ToArray();
                    if (sender.VideoTrackCount != streams.Length)
                        throw new InvalidDataException("The DV7 video tracks could not be mapped.");
                    var index = Array.IndexOf(streams, baseStream);
                    if (index < 0) throw new InvalidDataException("The DV7 base layer is unavailable.");
                    // Never accept FFmpeg's reordered default as the catalog video ordinal.
                    // AutoPlay is off and the output guard also checks the live stream before presentation.
                    if (!sender.SelectVideo(index))
                        throw new InvalidDataException("The DV7 base layer could not be selected.");
                }
            }
            if (attempt.Plan?.Delivery == "original_http")
                SelectOriginalAudioTrack(attempt, media, attempt.Plan.SelectedTracks?.Audio?.Index);
            else if (media.FfmpegSource is { } packaged)
            {
                // A server remux carries only its chosen stream; catalog ordinals do not index it.
                var streams = packaged.AudioStreams.Select(stream => stream.StreamIndex).ToArray();
                if (sender.AudioTracks.Count != streams.Length)
                    throw new InvalidDataException("The planned audio tracks could not be mapped.");
                var fileId = attempt.Plan?.Source?.MediaFileId ?? attempt.Start?.FileId;
                var catalogCount = attempt.Request.Detail.Versions.FirstOrDefault(version => version.FileId == fileId)?.AudioTracks?.Count;
                // Every Silo package (progressive remux, HLS remux or transcode) maps at most one audio
                // stream (remux.go / transcode.go: -map 0:a:N?), so the original's track count never applies.
                int? expectedCount = catalogCount > 0 ? 1 : null;
                // FFmpeg may omit an undecodable stream. Do not let that shift Silo's audio ordinals.
                var index = PlaybackRoutePolicy.AudioTrackIndex(media.Route.AudioOrdinal, streams, expectedCount is > 0 ? expectedCount : null);
                if (index >= 0) sender.SelectAudio(index);
            }
            // TvVideoPlaybackStarter.kt's player coordinate may be zero on reanchored HLS even
            // when the source is minutes into the title. Set it once, only after native opening.
            // A forward-only progressive input cannot seek: it plays from its origin (player 0).
            // Forward-only mounts (progressive remux, HLS through FFmpeg) already open where they play.
            if (media.Progressive is null) sender.PlaybackSession.Position = TimeSpan.FromSeconds(media.StartPosition);
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

    // Also the in-player audio picker's local switch: planIndex always addresses this original's catalog,
    // even when Media Foundation or FFmpeg expose tracks in a different order. Call on the UI thread.
    private void SelectOriginalAudioTrack(Attempt attempt, Mount media, long? planIndex)
    {
        try
        {
            if (attempt.Plan?.Delivery != "original_http" || media.Player is not { } player)
                throw new PlaybackAudioSelectionException();
            var fileId = attempt.Plan.Source?.MediaFileId ?? attempt.Plan.EffectiveMediaFileId;
            var catalog = attempt.Request.Detail.Versions.FirstOrDefault(version => version.FileId == fileId)?.AudioTracks;
            var ffmpeg = media.FfmpegSource;
            var tracks = player.AudioTracks;
            if (ffmpeg is not null && ffmpeg.AudioStreams.Count != tracks.Count)
                throw new PlaybackAudioSelectionException();
            var mounted = tracks.Select((track, index) =>
            {
                var stream = ffmpeg?.AudioStreams[index];
                // AudioStreamInfo.Language is a localized DISPLAY name in FFmpegInteropX;
                // the Media Foundation descriptor keeps its BCP 47 tag. Prefer that tag.
                var language = string.IsNullOrWhiteSpace(track.Language) ? stream?.Language : track.Language;
                return new MountedPlaybackAudioTrack(stream?.StreamIndex, language,
                    stream?.CodecName ?? track.Codec, stream?.Channels ?? track.Channels,
                    // Windows' Label may be a generated language name, not an embedded title.
                    stream?.Name ?? track.Name, track.Decodable);
            }).ToArray();
            PlaybackOriginalAudioSelection.Select(planIndex, catalog, mounted, ffmpeg is not null, index =>
            {
                // MediaPlayer reports a track it cannot open through the track itself (the engine through its state).
                if (media.NativePlayer is not null && media.Item is { } item)
                {
                    if (media.SelectedAudioTrack is { } prior) prior.OpenFailed -= OnAudioTrackOpenFailed;
                    media.SelectedAudioTrack = item.AudioTracks[index];
                    media.SelectedAudioTrack.OpenFailed += OnAudioTrackOpenFailed;
                }
                player.SelectAudio(index);
            }, () => player.SelectedAudioIndex);
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

    private void OnMediaFailed(PlaybackBackend sender, PlaybackBackendFailure failure)
    {
        try
        {
            var (hresult, decoder, network) = (failure.HResult, failure.Decoder, failure.Network);
            _ = DispatchAsync(() =>
            {
                if (_attempt is not { Media: { } media } attempt || !ReferenceEquals(media.Player, sender)) return;
                // An interrupted forward-only response is a lost connection, not a failed route.
                if (media.Progressive is { Faulted: true })
                {
                    _bufferingSignals.NoteMediaRequestFailed();
                    QueueProgressiveReconnect(attempt, media, "transport");
                }
                else QueueFailure(attempt, media, hresult, decoder, network);
            });
        }
        catch (Exception ex) { LogFailure("failed callback", ex); }
    }

    private void OnPlaybackStateChanged(PlaybackBackendSession sender, object? args) => _ = DispatchAsync(() =>
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
        if (media.Started && media.Progressive is not null) TrackProgressiveIdle(attempt, media, state);
        _state = !media.Opened || !media.Started ? "loading" : state switch
        {
            MediaPlaybackState.Playing => "playing",
            MediaPlaybackState.Paused => "paused",
            MediaPlaybackState.Buffering => "buffering",
            _ => "loading",
        };
        media.SystemControls?.Update(state);
        SetLoading(_state is "loading" or "buffering");
        SetDisplayActive(_state != "paused");
        // Playing vs paused/buffering changes how the subtitle overlay extrapolates its clock.
        PushSubtitleClock();
        // Stall detection sees every state change, not only the 100 ms tick.
        UpdateBufferingSignals(attempt);
    }

    private void OnMediaEnded(PlaybackBackend sender, object? args) => _ = DispatchAsync(() =>
    {
        if (_attempt is not { Media: { } media } attempt || !ReferenceEquals(media.Player, sender)
            || !IsCurrent(attempt, media)) return;
        // Protocol §4: a progressive response that did not end cleanly is a transport failure,
        // never the end of the title. Reopen it where it stopped (§6.2) instead of leaving.
        if (media.Progressive is { Completed: false }) QueueProgressiveReconnect(attempt, media, "incomplete");
        else
        {
            // The end of the media is no stall, and the plan stopped playing: its plan_summary goes out.
            BufferingMediaEnded();
            if (!HandleEndedWithUpNext()) _ = StopAndGoBackAsync();
        }
    });

    private void TrackProgressiveIdle(Attempt attempt, Mount media, MediaPlaybackState state)
    {
        var now = Environment.TickCount64;
        if (state == MediaPlaybackState.Paused)
        {
            media.PausedAt ??= now;
            return;
        }
        if (state != MediaPlaybackState.Playing) return;
        var pausedAt = media.PausedAt;
        media.PausedAt = null;
        // VideoPlayer.tsx PROGRESSIVE_REMUX_IDLE_REOPEN_MS: the server may have reaped the paused
        // response; playing again after a minute reopens it at the paused position.
        if (PlaybackProgressive.ShouldReopenAfterIdle(attempt.Plan, pausedAt, now))
            QueueProgressiveReconnect(attempt, media, "idle");
    }

    private void QueueProgressiveReconnect(Attempt attempt, Mount media, string reason)
    {
        if (!IsCurrent(attempt, media) || media.FailureQueued || media.Reconnecting) return;
        // A connection that recovered and played for a while starts a fresh reconnect budget.
        var now = Environment.TickCount64;
        if (now - attempt.ProgressiveReconnectAt > ProgressiveReconnectWindowMs) attempt.ProgressiveReconnects = 0;
        // Protocol §6.2: a transport that never showed a frame stays on the ordinary failure path.
        if (!media.Started || !attempt.ServerFeatures.Contains(PlaybackSeekReanchor.Feature)
            || attempt.ProgressiveReconnects >= MaxProgressiveReconnects)
        {
            QueueFailure(attempt, media, 0, isDecoder: false, isNetwork: true);
            return;
        }
        attempt.ProgressiveReconnects++;
        attempt.ProgressiveReconnectAt = now;
        media.Reconnecting = true;
        _ = ReconnectProgressiveAsync(attempt, media, reason);
    }

    private const int MaxProgressiveReconnects = 3;
    private const long ProgressiveReconnectWindowMs = 120_000;

    private async Task ReconnectProgressiveAsync(Attempt attempt, Mount media, string reason)
    {
        await _lifecycle.WaitAsync();
        var fallBack = false;
        try
        {
            if (!IsCurrent(attempt, media) || media.FailureQueued) return;
            // Each reason is a stream that ran dry while the viewer was playing, or an idle
            // reopen requested by Play: the replacement plays (protocol §6.2 step 4).
            var source = SourcePosition(attempt);
            Log.Info($"player: progressive reconnect reason={reason} count={attempt.ProgressiveReconnects}");
            // The same seek_reanchor the seek path uses: same route, session and tracks, no failure budget.
            // HLS through FFmpeg remounts the same plan locally; a progressive remux needs the server.
            var replacement = media.Transport.IsHls
                ? await RemountHlsAsync(attempt, new PlaybackSeek(source), paused: false)
                : await ReanchorSeekAsync(attempt, media, source, paused: false);
            fallBack = replacement is null;
            if (replacement is not null) await ReportPositionAsync(attempt, SourcePosition(attempt), false);
        }
        catch (OperationCanceledException) when (attempt.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogFailure("progressive reconnect", ex);
            fallBack = true;
        }
        finally
        {
            media.Reconnecting = false;
            _lifecycle.Release();
            PushSubtitleClock(force: true);
        }
        // Refused, unreachable or a replacement that did not open: the ordinary failure path
        // decides for whichever mount is current (one replan without progressive, then the error).
        if (fallBack && attempt.Media is { } current && IsCurrent(attempt, current))
            QueueFailure(attempt, current, 0, isDecoder: false, isNetwork: true);
    }

    private void DisposeMount(Attempt attempt)
    {
        VersionNotice.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
        if (attempt.Media is not { } media) return;
        attempt.Media = null;
        // MSS exposes MF's renderer device even for ForceSystemDecoder. Hold it across Close so
        // Trim happens after MF/FFmpeg have released their decoder surfaces and texture pools.
        var rendererDevice = media.RendererDevice?.TakeForClose() ?? 0;
        media.RendererDevice = null;
        Cleanup("cancel mount", media.Cancellation.Cancel);
        Cleanup("detach video output guard", () => media.VideoOutputGuard?.Dispose());
        media.VideoOutputGuard = null;
        media.Opening.TrySetCanceled(media.Token);
#if DEBUG_SERVER
        // The engine player reads this mount's source; stop it before the source goes.
        if (_enginePlayer is not null) _ = CloseEnginePlayerAsync(leave: false);
        media.EngineKeepAlive = null;
#endif
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
            // The element lets go first; the backend then pauses, drops its source and disposes.
            Cleanup("detach element", () => _video?.SetMediaPlayer(null));
            Cleanup("dispose player", player.Dispose);
        }
        Cleanup("close system controls", () => media.SystemControls?.Dispose());
        media.SystemControls = null;
        Cleanup("release video presenter", ReleaseVideoPresenter);
        // FFmpeg owns an MSS, but CreateMediaPlaybackItem also creates a MediaSource. Closing
        // only FFmpeg does not close that source's MF pipeline (including system HEVC surfaces).
        Cleanup("dispose item source", () => media.Item?.Source?.Dispose());
        Cleanup("dispose source", () => media.Source?.Dispose());
        // The native source owns its MSS/item and must outlive the player, but not the authenticated stream.
        Cleanup("dispose FFmpeg source", () => media.FfmpegSource?.Dispose());
        Cleanup("dispose random stream", () => media.RandomStream?.Dispose());
        Cleanup("dispose forward input", () => media.ForwardInput?.Dispose());
        Cleanup("dispose stream", () => media.Stream?.Dispose());
        Cleanup("dispose progressive stream", () => media.Progressive?.Dispose());
        media.Player = null;
        media.Item = null;
        media.Source = null;
        media.SelectedAudioTrack = null;
        media.FfmpegSource = null;
        media.RandomStream = null;
        media.Stream = null;
        media.ForwardInput = null;
        media.Progressive = null;
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

    /// <summary>
    /// The Rust stream's own IRandomAccessStream (reads on the Windows thread pool, never in managed
    /// code), or null for the C# stream. The projection takes its own reference; release ours.
    /// </summary>
    private static IInputStream? NativeInputStream(MediaProgressiveStream stream)
    {
        var pointer = stream.CreateNativeInputStream();
        if (pointer == 0) return null;
        try { return WinRT.MarshalInterface<IInputStream>.FromAbi(pointer); }
        finally { System.Runtime.InteropServices.Marshal.Release(pointer); }
    }

    private static IRandomAccessStream? NativeRandomAccessStream(MediaRangeStream stream)
    {
        var pointer = stream.CreateNativeRandomAccessStream();
        if (pointer == 0) return null;
        try { return WinRT.MarshalInterface<IRandomAccessStream>.FromAbi(pointer); }
        finally { System.Runtime.InteropServices.Marshal.Release(pointer); }
    }
}
