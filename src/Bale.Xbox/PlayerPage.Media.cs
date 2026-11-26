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

        public PlaybackMedia Transport { get; set; }
        public CancellationTokenSource Cancellation { get; }
        public CancellationToken Token { get; }
        public double StartPosition { get; set; }
        public bool StartPaused { get; set; }
        public TaskCompletionSource<bool> Opening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Opened { get; set; }
        public bool Started { get; set; }
        public double? PlayingFrom { get; set; }
        public bool FailureQueued { get; set; }
        /// <summary>This mount's start budget (PlayerPage.StartBudget.cs), from its creation to its first frame.</summary>
        public PlaybackStartBudget? StartBudget { get; set; }
        /// <summary>MountPlanAsync is still building this mount (under the lifecycle), as opposed to waiting for the backend.</summary>
        public bool Mounting { get; set; } = true;
        /// <summary>The start budget ran out before the first frame.</summary>
        public bool StartBudgetRanOut { get; set; }
#if DEBUG_SERVER
        /// <summary>bale-debug.sh start-budget hang: "open" never finishes opening, "ready" never reports MediaOpened.</summary>
        public string? DebugHang { get; init; }
        public DebugStageGpuTiming StageGpuTiming { get; } = DebugStageGpuTiming.Capture();
        /// <summary>The source peak of this mount's tone-map stage (stream metadata or config), logged once per change.</summary>
        public DebugToneMapPeak ToneMapPeak { get; } = DebugToneMapPeak.Capture();
#endif
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
        /// <summary>
        /// The FFmpeg source's current streams for the UI thread (HUD, stats, debug readers): read at open, refreshed on
        /// the thread pool. The UI thread never calls the wrapper's locking members during playback (FfmpegStreamSnapshot).
        /// </summary>
        public BackgroundSnapshot<FfmpegStreamSnapshot>? FfmpegStreams { get; set; }
        public PlaybackResources.MediaDevice? RendererDevice { get; set; }
        public FfmpegVideoOutputGuard? VideoOutputGuard { get; set; }
        /// <summary>EngineOutputPolicy's decision, made once when the FFmpeg source is configured (null on other routes).</summary>
        public EngineOutputDecision? EngineOutput { get; set; }
        /// <summary>The stage was asked for dithered 8-bit NV12 (bale_tonemap_output_depth=8) for this mount.</summary>
        public bool StageOutput8Bit { get; set; }
        public int? BaseLayerStreamIndex { get; set; }
        public PlaybackRouteDecision Route { get; set; } = null!;
        // Set by StartSubtitles for an in-container image track: the FFmpeg source feeds the overlay.
        public int? SubtitleStreamIndex { get; set; }
        public ulong SubtitleSinkToken { get; set; }
        /// <summary>The fixed stage at which this mount's native embedded subtitle could not be mounted, or null.</summary>
        public string? EmbeddedSubtitleFailure { get; set; }
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
        /// <summary>The debug AV1 decoder switch's decision for this mount, or null with the switch on dav1d.</summary>
        public Rav1gRouteDecision? Rav1g { get; set; }
        /// <summary>This mount was configured for rav1g (bale_av1_decoder=1): a software AV1 route with the switch on rav1g.</summary>
        public bool Rav1gRoute { get; set; }
        /// <summary>The open took rav1g's provider (its D3D11 engine), not the fork's open-time dav1d fallback.</summary>
        public bool Rav1gActive { get; set; }
        public Rav1gOutputCheck? Rav1gCheck { get; set; }
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
        // The mount this one replaces closes on the thread pool (DisposeMount); open after its decoders are gone.
        await WaitForMountCloseAsync("mount");
#if DEBUG_SERVER
        DebugDecoderLog.Reset();
        // Probe on this mount and keep its one range reader; the start budget also bounds the probe.
        Mount? debugMedia = attempt.DebugUrl is not null ? await ProbeDebugUrlAsync(attempt, playerStart, paused) : null;
#endif
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
        var transport =
#if DEBUG_SERVER
            debugMedia?.Transport ??
#endif
            attempt.Client.Playback.OpenMedia(attempt.Plan!);
        var media = attempt.Media =
#if DEBUG_SERVER
            debugMedia ??
#endif
            new Mount(transport, attempt.Cancellation.Token)
        {
            StartPosition = playerStart,
            StartPaused = paused,
            StartBudget = NewStartBudget(),
#if DEBUG_SERVER
            DebugHang = DebugStartBudget.TakeHang(),
#endif
        };
        media.Route = PlaybackRoutePolicy.Decide(attempt.Plan!, transport.IsHls, attempt.FfmpegEnabled, attempt.ForceFfmpeg,
                sdrOutput: sdrOutput, versions: attempt.Request.Detail.Versions, hlsThroughFfmpeg: attempt.HlsThroughFfmpeg,
                hardwareDecode: attempt.HardwareDecode, display: DisplayHdrOutput.CurrentRawSize(),
                sdrDownscale: !attempt.SdrDownscaleDeclined && ConsoleToneMapPolicy.SdrDownscaleWrapperSupported(FfmpegWrapperVersion.Value)
                    && ConsoleToneMapRuntime.SupportsSdrDownscale,
                // The claims as they stand (a replan may have withdrawn software video): only those admit a software fallback.
                softwareDecode: attempt.Start?.ClientCapabilities.VideoDecode,
                // Phase B: the stage (tone map, downscale) takes FFmpeg software frames too (wrapper bale.80+).
                softwareStage: ConsoleToneMapPolicy.SoftwareStageWrapperSupported(FfmpegWrapperVersion.Value)
                    && ConsoleToneMapRuntime.IsAvailable);
#if DEBUG_SERVER
        DebugValidateUrlRoute(attempt, media);
#endif
        // From here the mount opens; the start budget may stop it (PlayerPage.StartBudget.cs) until this returns.
        try { await OpenMountAsync(attempt, media, transport, playerStart, paused, rangeRefused); }
        finally { media.Mounting = false; }
    }

    /// <summary>Opens a new mount's source and hands it to the player (MountPlanAsync's second half).</summary>
    private async Task OpenMountAsync(Attempt attempt, Mount media, PlaybackMedia transport, double playerStart, bool paused,
        bool rangeRefused)
    {
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
#if DEBUG_SERVER
        // bale-debug.sh start-budget hang=open: this mount's open never returns, as a source that never answers.
        if (media.DebugHang == "open") await DebugStartBudget.HangAsync(media.Token);
#endif
        {
            if (transport.IsProgressive)
            {
                // PlaybackProgressive: no length, no ranges. Windows' sources need both; FFmpeg's
                // non-seekable input does not. Every seek is a server reanchor (PlayerPage.Seek.cs).
                if (!media.Route.UseFfmpeg) throw new InvalidDataException("A progressive remux requires the FFmpeg route.");
                try
                {
                    media.Progressive = attempt.UseRustTransport
                        ? await OpenStepAsync(transport.OpenNativeProgressiveAsync(media.Token), media.Token)
                        : await OpenStepAsync(transport.OpenProgressiveAsync(media.Token), media.Token);
                }
                catch (ProgressiveRangeRefusedException) when (!rangeRefused
                    && attempt.ServerFeatures.Contains(PlaybackSeekReanchor.Feature))
                {
                    // docs/playback-api.md: a 409 means "reopen the stream at its current position".
                    // Nothing has played from this mount, so that is the plan's own source start.
                    Log.Info("player: progressive range refused; reanchoring");
                    var decision = await RequestReanchorAsync(attempt, PlaybackTimeline.From(attempt.Plan!.Timeline).SourceStartSeconds);
                    // The start budget ran out (or Back) while the server answered: no replacement mount.
                    media.Token.ThrowIfCancellationRequested();
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
                var hls = await OpenStepAsync(transport.OpenNativeHlsAsync(playerStart, media.Token), media.Token);
                media.Progressive = hls;
                transport.StartPlayerClockAt(hls.SegmentStartSeconds);
                media.ForwardInput = NativeInputStream(media.Progressive) ?? new ForwardInputStream(media.Progressive);
            }
            else if (media.Stream is null)
            {
#if DEBUG_SERVER
                var prefetch = media.Route.UseFfmpeg ? attempt.FfmpegTuning.Prefetch ?? HttpRangeStream.DefaultPrefetchBlocks
                    : HttpRangeStream.DefaultPrefetchBlocks;
                media.Stream = attempt.UseRustTransport
                    ? await OpenStepAsync(transport.OpenNativeStreamAsync(media.Token, prefetch), media.Token)
                    : await OpenStepAsync(transport.OpenStreamAsync(media.Token, prefetch), media.Token);
#else
                // The Rust streams are the default (docs/rust-port.md, slice 7): reads never wait on the GC.
                media.Stream = await OpenStepAsync(transport.OpenNativeStreamAsync(media.Token), media.Token);
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
                // Debug rav1g switch: software AV1 only, and never again on this attempt once a rav1g mount failed.
                var sdrDisplay = !ConsoleToneMapPolicy.HasHdrOutput(attempt.Start!.ClientPlaybackContext.Output.HdrDetails);
                if (attempt.Av1Decoder == "rav1g")
                {
                    var rav1g = media.Rav1g = Rav1gPolicy.Route(attempt.Av1Decoder, media.Route, attempt.Plan!,
                        attempt.Request.Detail.Versions, DisplayHdrOutput.CurrentRawSize(), sdrDisplay, attempt.Rav1gRemounted);
                    if (rav1g.Rav1g)
                    {
                        DebugRav1gLog.Reset();
                        var output = rav1g.OutputWidth is { } width && rav1g.OutputHeight is { } height ? $"{width}x{height}" : "source";
                        Log.Info($"player: AV1 decoder rav1g buffer={rav1g.BufferMs} ms output={output} tonemap={rav1g.ToneMap} library={(Rav1gLibrary.Present ? "packaged" : "missing")}");
                    }
                }
                DebugDescribeUrlRoute(attempt, media);
                var config = FfmpegSource.Configuration(media.Route, attempt.FfmpegTuning, media.Rav1g, attempt.Dav1dPath,
                    attempt.Dav1dGpuFilmGrain, attempt.SdrWhiteNits);
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
                var config = FfmpegSource.Configuration(media.Route, attempt.Dav1dGpuFilmGrain, attempt.SdrWhiteNits);
#endif
                FfmpegSource.ApplyDisplaySize(config, media.Route, DisplayHdrOutput.CurrentRawSize());
                // Phase 2 of the bit-depth rule: decide the stage's depth with the engine output, once per open.
                media.EngineOutput = DecideEngineOutput(attempt, media, ConsoleToneMapRuntime.SupportsDitheredNv12);
                media.StageOutput8Bit = FfmpegSource.ApplyStageOutputDepth(config, media.Route, media.EngineOutput);
                if (!media.StageOutput8Bit && media.EngineOutput.StageOutputDepth == 8)
                    media.EngineOutput = DecideEngineOutput(attempt, media, stageNv12Dithered: false);
                FfmpegSource.ApplySubtitleSink(config, media.SubtitleStreamIndex, media.SubtitleSinkToken);
                // TODO(captions): in-band closed captions plug in here once the pinned wrapper has fork bale.84's caption
                // tap (docs/closed-captions.md). For an FFmpeg mount of an H.264, HEVC or MPEG-2 stream: take a fresh token
                // from the subtitle sink range, register the playback's ClosedCaptions under it (Register starts it afresh
                // for the mount) and pass it with FfmpegSource.ApplyCaptionSink(config, token). ClosedCaptions.Seen then
                // offers the CC track, and picking it calls SubtitleOverlay.AttachCaptions with the subtitle appearance, on
                // the mount's sample timeline (_subtitleTimingOrigin as for bitmaps).
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
                try
                {
                    media.FfmpegSource = media.ForwardInput is { } forward
                        ? await FFmpegMediaSource.CreateFromInputStreamAsync(forward, config).AsTask()
                        : await FFmpegMediaSource.CreateFromStreamAsync(media.RandomStream!, config).AsTask();
                }
                catch (Exception ex) when (media.Progressive is { EndedWithoutMedia: true } && !media.Token.IsCancellationRequested)
                {
                    // Segments with no media to the playlist's end (an MPEG-TS stream of only PAT/PMT has no streams).
                    LogFailure("FFmpeg open past the media's end", ex);
                    EndAtEmptyTail(attempt, media);
                    return;
                }
                media.Token.ThrowIfCancellationRequested();
                if (media.Route.DolbyVision7BaseLayer)
                {
                    var fileId = attempt.Plan!.Source?.MediaFileId ?? attempt.Plan.EffectiveMediaFileId;
                    var expectedCount = attempt.Request.Detail.Versions.FirstOrDefault(version => version.FileId == fileId)?.VideoTracks?.Count;
                    var streams = media.FfmpegSource.VideoStreams.Select(video => video.StreamIndex).ToArray();
                    var index = DolbyVision7Policy.BaseLayerTrackIndex(streams, expectedCount);
                    media.BaseLayerStreamIndex = streams[index];
                }
                // Open time: no MediaStreamSource yet, so no sample thread can hold the wrapper's source lock.
                media.FfmpegStreams = FfmpegStreamSnapshot.Open(media.FfmpegSource);
#if DEBUG_SERVER
                var rav1gRoute = media.Rav1g is { Rav1g: true } && media.SoftwareVideo && media.Route.VideoCodec == "av1";
                FfmpegSource.ValidateVideoDecoders(media.FfmpegSource, media.Route, attempt.FfmpegTuning, rav1gRoute);
                media.Rav1gRoute = rav1gRoute;
                if (rav1gRoute)
                {
                    // rav1g's provider reports the D3D11 engine; the fork's open-time fallback is dav1d's software engine.
                    media.Rav1gActive = media.FfmpegStreams?.Value?.Video?.DecoderEngine == DecoderEngine.FFmpegD3D11HardwareDecoder;
                    Log.Info($"player: AV1 open used {(media.Rav1gActive ? "rav1g" : "dav1d (rav1g fell back at open)")}");
                    if (attempt.DebugUrl is not null)
                    {
                        _debugUrlDiagnostics += $"Debug file: AV1 open used {(media.Rav1gActive ? "rav1g" : "dav1d; rav1g declined (see Bale rav1g log)")}\n";
                        if (!media.Rav1gActive && media.Route.ConsoleToneMap)
                            throw new DebugPlayUrlException("rav1g declined; dav1d cannot provide this HDR stage");
                    }
                }
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
#if DEBUG_SERVER
                if (rav1gRoute)
                {
                    // Design §4: direct output only with the rav1g mark, the source depth and colour; stage output marked as
                    // today; unmarked output is the open's dav1d fallback, reported and accepted on this debug route.
                    media.Rav1gCheck = new Rav1gOutputCheck(media.Rav1g!.Source, sdrDisplay, !ConsoleToneMapRuntime.SupportsP010);
                    media.VideoOutputGuard = new FfmpegVideoOutputGuard(media.FfmpegSource, () =>
                        _ = DispatchAsync(() => QueueFailure(attempt, media, 0, isDecoder: false, isNetwork: false)), media.Rav1gCheck);
                }
                else
#endif
                if (guardToneMap || media.Route.DolbyVision7BaseLayer)
                    media.VideoOutputGuard = new FfmpegVideoOutputGuard(media.FfmpegSource, () =>
                        _ = DispatchAsync(() => QueueFailure(attempt, media, 0, isDecoder: false, isNetwork: false)),
                        toneMap: guardToneMap, baseLayerStreamIndex: media.BaseLayerStreamIndex,
                        allowNv12: !ConsoleToneMapRuntime.SupportsP010, nv12Requested: media.StageOutput8Bit,
                        softwareStage: media.Route.SoftwareVideo);

                // Low-frequency Starting only, no sample observer or rendering state changes.
                media.RendererDevice = new PlaybackResources.MediaDevice(media.FfmpegSource);
#if DEBUG_SERVER
                var streamSnapshot = media.FfmpegStreams;
                try { media.MssVideoMediaType = new DebugVideoMediaType(media.FfmpegSource, config.Video.VideoDecoderMode, () => streamSnapshot?.Value?.Video); }
                catch (Exception ex) { Log.Info($"player: video media type route=ffmpeg source=mss unavailable ({ex.GetType().Name})"); }
#endif
                // The MSS only exists after CreateMediaPlaybackItem and is kept alive by the item.
                // FFmpegInteropX sets BufferTime 0, so the renderer pulls decoded frames only ~0.3 s ahead.
                int? bufferOverrideMs = null;
                var softwareVideo = media.Route.SoftwareVideo;
#if DEBUG_SERVER
                // rav1g's pool is 8 images: a few frames of BufferTime, unless ffmpeg-tune buffer= overrides it.
                bufferOverrideMs = attempt.FfmpegTuning.BufferMs ?? (rav1gRoute ? media.Rav1g!.BufferMs : null);
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
            // A start budget that ran out ended this open: it fails, it does not move to another player.
            media.Token.ThrowIfCancellationRequested();
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
        ConfigureSmoothness(attempt, media, backend);
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
                        : new NativeFrameDelivery(softwareSource, player.PlaybackSession, stream: media.Stream, frameLog: attempt.FrameLog);
                }
                catch (Exception ex) { LogFailure("frame delivery observer", ex); }
            }
            else Log.Info($"player: frame delivery not attached softwareVideo={media.SoftwareVideo} ffmpeg={media.FfmpegSource is not null}");
        }
        LogFrameLog(attempt, media, "mediaplayer");
#endif
        // FastSeek needs MediaPlayer's session (other backends seek without one, wrapper bale.30). The setter takes the
        // wrapper's source lock: set it before Source, while no sample thread exists, never on the UI thread after open.
        if (media.FfmpegSource is { } sessionSource) sessionSource.PlaybackSession = player.PlaybackSession;
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
        ConfigureSmoothness(attempt, media, engine);
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
        engine.FrameLog = attempt.FrameLog;
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
        var target = EngineTarget(attempt, media);
#if DEBUG_SERVER
        // Before the open: the engine pulls its preroll samples while it opens. Its clock follows the open.
        AttachEngineFrameDelivery(attempt, media, engine);
#endif
        // Forward-only mounts open where they play (player 0); seekable ones at the mount's start.
        var opened = await engine.OpenAsync(source, type, canSeek: media.Progressive is null,
            start: media.Progressive is null ? media.StartPosition : 0, target: target);
        foreach (var line in engine.Report.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log.Info("engine: " + line);
#if DEBUG_SERVER
        if (opened && ReferenceEquals(attempt.Media, media) && media.FrameDelivery is NativeFrameDelivery frames)
            frames.AttachEngine(engine);
#endif
        return opened || !ReferenceEquals(attempt.Media, media);
    }

#if DEBUG_SERVER
    /// <summary>
    /// The attempt's sample observer (frame-delivery switch) on the engine: native only, since the managed observer reads
    /// MediaPlayer's session. Subscribed to the FFmpeg MSS before the engine pulls samples; NativeFrameDelivery.AttachEngine
    /// adds IMFMediaEngine's clock (current time and state) once the session is open.
    /// </summary>
    private static void AttachEngineFrameDelivery(Attempt attempt, Mount media, EngineBackend engine)
    {
        Log.Info($"player: frame delivery observer={attempt.FrameDeliveryMode} backend=engine");
        if (attempt.FrameDeliveryMode == "managed")
            Log.Info("player: frame delivery managed needs MediaPlayer's session; not attached to the engine");
        // Only with frame-log on: the observer reads the engine's clock on the sample thread, and default debug
        // playback (the console measurements) must behave as before.
        else if (!attempt.FrameLog)
            Log.Info("player: frame delivery not attached on the engine (frame-log off)");
        else if (attempt.FrameDeliveryMode != "off")
        {
            if (media.SoftwareVideo && media.FfmpegSource is { } softwareSource)
            {
                try { media.FrameDelivery = new NativeFrameDelivery(softwareSource, engine.PlaybackSession, media.Stream, attempt.FrameLog); }
                catch (Exception ex) { LogFailure("frame delivery observer", ex); }
            }
            else Log.Info($"player: frame delivery not attached softwareVideo={media.SoftwareVideo} ffmpeg={media.FfmpegSource is not null}");
        }
        LogFrameLog(attempt, media, "engine");
    }

    /// <summary>frame-log: once per mount, which lines follow (frame: needs the native observer; frame-event: the engine).</summary>
    private static void LogFrameLog(Attempt attempt, Mount media, string backend)
    {
        if (!attempt.FrameLog) return;
        var frames = media.FrameDelivery is NativeFrameDelivery
            ? "frame: lines"
            : "no frame: lines (needs frame-delivery native and FFmpeg software video)";
        var events = backend == "engine" ? ", frame-event: lines" : string.Empty;
        Log.Info($"player: frame log on backend={backend}: {frames}{events}; t = QPC ms, pts/dur/pos = 100 ns, lead = ms");
    }
#endif

    /// <summary>
    /// The engine swap-chain format for this mount (bale_gpu target code), capped at display depth for SDR
    /// (owner's bit-depth rule, AGENTS.md; EngineOutputPolicy). Read HDMI on the UI thread; log once per open.
    /// </summary>
    private static uint EngineTarget(Attempt attempt, Mount media)
    {
        var displayBitsPerPixel = DisplayHdrOutput.CurrentBitsPerPixel();
        // An FFmpeg mount decided at configuration, together with the stage depth it asked for.
        var output = media.EngineOutput ?? DecideEngineOutput(attempt, media, stageNv12Dithered: false);
        if (output.Frames == "unknown") Log.Info("player: engine output depth unknown; bgra kept");
        var code = output.Code;
        var name = output.Target;
#if DEBUG_SERVER
        if (DebugEngineTargetSwitch.Code is { } forced)
        {
            code = forced;
            name = DebugEngineTargetSwitch.Mode + " (debug engine-target)";
        }
#endif
        var display = displayBitsPerPixel is { } bpp ? $"{bpp}bpp" : "unknown";
        Log.Info($"player: engine output={name} frames={output.Frames} display={display} reason={output.Reason}");
        return code;
    }

    /// <summary>
    /// EngineOutputPolicy for this mount: the swap-chain format and the console stage's output depth (8 = dithered NV12
    /// on an 8-bit SDR display, phase 2 of the bit-depth rule). Reads the current HDMI mode on the UI thread.
    /// </summary>
    private static EngineOutputDecision DecideEngineOutput(Attempt attempt, Mount media, bool stageNv12Dithered)
    {
        var ceiling = "display";
#if DEBUG_SERVER
        ceiling = DebugOutputDepthSwitch.Mode;
#endif
        try
        {
            return attempt.Plan is { } plan
                ? EngineOutputPolicy.Decide(plan, media.Route, attempt.Request.Detail.Versions, ConsoleToneMapRuntime.SupportsP010,
                    DisplayHdrOutput.CurrentBitsPerPixel(), ceiling, stageNv12Dithered)
                : new EngineOutputDecision("bgra", "unknown", "no plan");
        }
        catch (Exception ex)
        {
            Log.Info($"player: engine output policy failed {ex.GetType().Name}");
            return new EngineOutputDecision("bgra", "unknown", "policy failed");
        }
    }

    /// <summary>The mount's route in words, for the log: never a URL.</summary>
    private static string RouteText(Mount media)
    {
        var route = media.Route;
        // Software frames through the stage (phase B); the debug rav1g route keeps its words.
        var software = route.SoftwareVideo && route.VideoCodec != "av1";
        return route.UseFfmpeg
            ? "ffmpeg " + (route.ConsoleToneMap ? (software ? "software + console tone map" : "d3d11va + console tone map")
                : route.ConsoleDownscale ? (software ? "software + console downscale" : "d3d11va + console downscale")
                : route.DolbyVision7BaseLayer ? "DV7 base layer"
                : route.SoftwareVideo ? "software decode" : route.DolbyVisionBaseLayer ? "system decoder, DV8 base layer" : "system decoder passthrough")
            : "Windows reader (direct)";
    }

    private void OnMediaOpened(PlaybackBackend sender, object? args) => _ = DispatchAsync(() =>
    {
        if (_attempt is not { Media: { } media } attempt || !ReferenceEquals(media.Player, sender)
            || !IsCurrent(attempt, media) || media.FailureQueued) return;
#if DEBUG_SERVER
        // bale-debug.sh start-budget hang=ready: the backend's open is never seen, so the mount never starts.
        if (media.DebugHang == "ready")
        {
            Log.Info("player: debug start-budget hang: MediaOpened ignored");
            return;
        }
#endif
        try
        {
            if (media.FfmpegSource is { } ffmpeg)
            {
                // MediaPlayer's session went to FFmpeg before Source (MountPlanAsync): its setter takes the source lock.
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
                // silo-apple AetherPlaybackController.validateEmbeddedSubtitleSelection: the bound stream must be
                // in the opened demuxer's subtitle inventory, or the sink never receives a bitmap.
                if (media.SubtitleStreamIndex is { } subtitleStream && !PlaybackEmbeddedSubtitleFailure.Mounted(subtitleStream,
                        ffmpeg.SubtitleStreams.Select(stream => stream.StreamIndex)))
                {
                    Log.Info("player: subtitles failed stage=embedded inventory");
                    media.EmbeddedSubtitleFailure ??= "inventory";
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
            // Before Play, as for audio: the subtitle_embedded_failed replan keeps this session and position.
            if (RecoverEmbeddedSubtitle(attempt, media)) return;
            // TvVideoPlaybackStarter.kt's player coordinate may be zero on reanchored HLS even
            // when the source is minutes into the title. Set it once, only after native opening.
            // A forward-only progressive input cannot seek: it plays from its origin (player 0).
            // Forward-only mounts (progressive remux, HLS through FFmpeg) already open where they play.
            if (media.Progressive is null) sender.PlaybackSession.Position = TimeSpan.FromSeconds(media.StartPosition);
            media.Opened = true;
            Log.Info("player: opened");
            // A pick made in the player is the viewer's choice once a plan that plays it opens: the track change's own
            // remount, or a later plan that asks for it again (PlayerPage.Tracks.cs).
            SaveTrackPicks(attempt);
            // The decoder engine can change once samples flow (a D3D11 or rav1g fallback): read again off the UI thread.
            media.FfmpegStreams?.RequestRefresh();
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
                // The HUD's audio rows follow the wrapper's current stream; never read it here (UI thread).
                media.FfmpegStreams?.RequestRefresh();
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
                // A mount past the media's end fails for want of media: the title ended there.
                else if (!media.Started && media.Progressive is { EndedWithoutMedia: true }) EndAtEmptyTail(attempt, media);
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
        // A direct play's range stream gave up (its retry window spent, or a final status): FFmpeg read the failed
        // read as the end of the file. A transport stall, never the end of the title: one same-plan remount, then
        // the replan with every claim kept, then the error with Retry (PlaybackTransportStall).
        else if (media.Stream is { LastReadFailed: true })
        {
            Log.Info("player: media ended after a failed range read; treating it as a transport stall");
            QueueFailure(attempt, media, 0, isDecoder: false, isNetwork: true, PlaybackTransportStall.Failure);
        }
        else
        {
            // The end of the media is no stall, and the plan stopped playing: its plan_summary goes out.
            BufferingMediaEnded();
            if (!HandleEndedWithUpNext()) _ = StopAndGoBackAsync();
        }
    });

    /// <summary>
    /// The mount started past the media's end (silo-server 4bee8f8): a transcode's playlist lists segments to the file's
    /// stored duration, and Silo answers those past the media's real end with no media, so this mount's HLS segments from
    /// its start to the playlist's end held none (a seek or start into that tail) and FFmpeg has nothing to open or play.
    /// The title ended there, as the web's hls.js steps over such segments to the end: end as MediaEnded does (Up Next or
    /// back), never an error screen, a replan or a wait for the start budget. Playing into the tail ends normally anyway.
    /// </summary>
    private void EndAtEmptyTail(Attempt attempt, Mount media)
    {
        if (!IsCurrent(attempt, media) || media.FailureQueued || media.Started) return;
        // Finished: no failure, start budget or seek watchdog acts on this mount any more.
        media.FailureQueued = true;
        media.Opening.TrySetResult(false);
        Log.Info("player: HLS mount opened past the media's end (no media to the playlist's end); ending playback");
        _ = DispatchAsync(() =>
        {
            if (!_active || _leaving || _suspended || !ReferenceEquals(_attempt, attempt) || !ReferenceEquals(attempt.Media, media)) return;
            SetLoading(false);
            BufferingMediaEnded();
            if (!HandleEndedWithUpNext()) _ = StopAndGoBackAsync();
        });
    }

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
#if DEBUG_SERVER
        var smoothnessFps = RefreshHudStats(attempt, sampleBandwidth: false).FrameRate;
        var smoothnessPlayer = media.Player;
#endif
        attempt.Media = null;
        // MSS exposes MF's renderer device even for ForceSystemDecoder. Hold it across Close so
        // Trim happens after MF/FFmpeg have released their decoder surfaces and texture pools.
        var rendererDevice = media.RendererDevice?.TakeForClose() ?? 0;
        media.RendererDevice = null;
        Cleanup("cancel mount", media.Cancellation.Cancel);
        Cleanup("dispose start budget", () => media.StartBudget?.Dispose());
        media.StartBudget = null;
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
#if DEBUG_SERVER
        // The next mount reuses this playback's meter. Freeze this summary before the asynchronous source close,
        // including when the bounded close wait lets a later mount proceed before an old decoder shuts down.
        var smoothnessSummary = smoothnessPlayer?.Smoothness.Read(PlaybackBackend.ClockSeconds, smoothnessFps);
#endif
        Cleanup("close system controls", () => media.SystemControls?.Dispose());
        media.SystemControls = null;
        Cleanup("release video presenter", ReleaseVideoPresenter);
        media.FfmpegStreams?.Close();
        media.FfmpegStreams = null;
        // The native sources close on the thread pool, in the same order, one mount after another: FFmpegInteropX's
        // Close (also reached from the item source's MediaStreamSource Closed event) takes the source lock, which a
        // sample request may hold while it waits on a decoder or a read. The fork cancels a GPU decoder's image wait
        // first, but the UI thread must not be the one that waits (rav1g, 2026-11-18). The next mount and the page's
        // idle trim wait for this close without blocking the UI thread (WaitForMountCloseAsync).
        var itemSource = media.Item?.Source;
        var source = media.Source;
        var ffmpeg = media.FfmpegSource;
        var random = media.RandomStream;
        var forward = media.ForwardInput;
        var stream = media.Stream;
        var progressive = media.Progressive;
        var cancellation = media.Cancellation;
        media.Player = null;
        media.Item = null;
        media.Source = null;
        media.SelectedAudioTrack = null;
        media.FfmpegSource = null;
        media.RandomStream = null;
        media.Stream = null;
        media.ForwardInput = null;
        media.Progressive = null;
        _mountClose = _mountClose.ContinueWith(_ =>
        {
            // FFmpeg owns an MSS, but CreateMediaPlaybackItem also creates a MediaSource. Closing
            // only FFmpeg does not close that source's MF pipeline (including system HEVC surfaces).
            Cleanup("dispose item source", () => itemSource?.Dispose());
            Cleanup("dispose source", () => source?.Dispose());
            // The native source owns its MSS/item and must outlive the player, but not the authenticated stream.
            Cleanup("dispose FFmpeg source", () => ffmpeg?.Dispose());
            Cleanup("dispose random stream", () => random?.Dispose());
            Cleanup("dispose forward input", () => forward?.Dispose());
            Cleanup("dispose stream", () => stream?.Dispose());
            Cleanup("dispose progressive stream", () => progressive?.Dispose());
#if DEBUG_SERVER
            if (smoothnessSummary is { } summary)
                LogSmoothnessSummary(summary,
                    smoothnessPlayer is EngineBackend, DebugDecoderLog.Snapshot(), smoothnessFps);
#endif
            PlaybackResources.ReleaseMediaDevice(rendererDevice);
            Cleanup("dispose mount cancellation", cancellation.Dispose);
#if DEBUG_SERVER
            Log.Info("player resources: mount released=player,item-source,ffmpeg,streams,observer,video-presenter renderer_device=" + (rendererDevice != 0 ? "released" : "unavailable"));
#endif
        }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
    }

    /// <summary>The previous mounts' native close (DisposeMount), run in order on the thread pool.</summary>
    private Task _mountClose = Task.CompletedTask;

    /// <summary>
    /// Waits, without blocking the UI thread, for earlier mounts' native close: before a new FFmpeg source opens (their
    /// decoder memory is gone, as when the close ran inline) and before the page's idle trim. A close that never ends
    /// (a wrapper deadlock) is logged and passed after <see cref="MountCloseWaitMs"/>, so the page stays usable.
    /// </summary>
    private async Task WaitForMountCloseAsync(string reason)
    {
        var close = _mountClose;
        if (close.IsCompleted) return;
        if (await Task.WhenAny(close, Task.Delay(MountCloseWaitMs)) != close)
            Log.Info($"player: previous mount still closing after {MountCloseWaitMs} ms; continuing ({reason})");
    }

    private const int MountCloseWaitMs = 5000;

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
