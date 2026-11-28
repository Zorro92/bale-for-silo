using System.Globalization;
using Bale.Core;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Microsoft.Kiota.Abstractions;
using Windows.ApplicationModel;
using Windows.Media.Playback;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>Full-screen playback with passive skip transport; focusable transport/HUD controls come later.</summary>
public sealed partial class PlayerPage : Page, IBackHandler
{
    // All lifecycle operations run on the UI thread and serialize across awaits. Native callbacks
    // carry their mount identity so an old pipeline cannot fail or start its replacement.
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DisplayRequest _display = new();
    private readonly DisplayHdrOutput _hdrOutput = new();
    private readonly PlayerSpinnerVisibility _spinner = new();
    private PlayerRequest? _request;
    private Attempt? _attempt;
    private Task? _shutdown;
    private IDisposable? _gcLatencyLease;
    private IDisposable? _resourceLease;
    private bool _active;
    private bool _leaving;
    private bool _suspended;
    private bool _displayActive;
    private bool _loading;
#if DEBUG_SERVER
    private long _lastRangeLog;
#endif
    private bool _recording;
    private bool _starting;
    private long _lastSample;
    private string _state = "loading";
    private string? _delivery;
    private double _sourcePosition;
    private double? _duration;

    private sealed class Attempt(PlayerRequest request, SiloClient client)
    {
        public PlaybackSmoothnessMeter Smoothness { get; } = new();
        /// <summary>The console's hardware decode entries this attempt claimed (route software fallback).</summary>
        public IReadOnlyList<Bale.Core.Playback.VideoDecodeCapability> HardwareDecode { get; set; } = [];
        public PlayerRequest Request { get; set; } = request;
        public SiloClient Client { get; } = client;
        public CancellationTokenSource Cancellation { get; } = new();
        public PlaybackSessionServices? Services { get; set; }
        public PlaybackStartRequestV3? Start { get; set; }
        public PlaybackPlan? Plan { get; set; }
        public IReadOnlyList<string> ServerFeatures { get; set; } = [];
        public IReadOnlyList<string> AttemptedPlanKeys { get; set; } = [];
        public string PlanAttemptId { get; set; } = string.Empty;
        public string? SessionId { get; set; }
        public bool Attached { get; set; }
        public bool Replanned { get; set; }
        /// <summary>The SDR downscale stage failed once: later mounts of this attempt use the system decoder.</summary>
        public bool SdrDownscaleDeclined { get; set; }
        public double Position { get; set; }
        /// <summary>
        /// A skip reached the end of the file and ended playback there (PlayerPage.SkipMarkers.cs EndAtSkip): the source
        /// time it ended at, which every position read reports from then on, wherever the paused player sits.
        /// </summary>
        public double? EndedAt { get; set; }
        public Mount? Media { get; set; }
        public bool StreamAdapterLogged { get; set; }
        public int ProgressiveReconnects { get; set; }
        public long ProgressiveReconnectAt { get; set; }
        /// <summary>Same-plan remounts after a range stream gave up (PlaybackTransportStall).</summary>
        public int TransportStallRemounts { get; set; }
        public long TransportStallRemountAt { get; set; }
        public PlaybackVersionNotice VersionNotice { get; } = new();
        /// <summary>Keep the device's SDR Brightness (tone-map SDR white) through every open/remount of this attempt.</summary>
        public int SdrWhiteNits { get; } = AppSettings.SdrWhiteNits;
        /// <summary>The subtitle appearance read this attempt started with; a text subtitle load waits for it (bounded).</summary>
        public Task SubtitleAppearanceRead { get; set; } = Task.CompletedTask;
        /// <summary>In-band closed captions: whether a mount found them and whether the viewer picked CC (PlayerPage.Captions.cs).</summary>
        public PlaybackClosedCaptions Captions { get; } = new();
        /// <summary>A rav1g mount failed and the same plan was remounted on dav1d; later mounts stay on dav1d.</summary>
        public bool Rav1gRemounted { get; set; }
#if DEBUG_SERVER
        public DebugPlayUrlRequest? DebugUrl => Request.DebugUrl;
        // Match the capability claim and backend even if the debug toggle changes during a request/replan.
        public string FfmpegMode { get; } = DebugFfmpegPlayback.Mode;
        public string DeliveryMode { get; } = DebugPlaybackDelivery.Mode;
        public DebugFfmpegInputTuning FfmpegTuning { get; } = DebugFfmpegInputTuning.Current;
        public bool FfmpegTuningLogged { get; set; }
        public bool PretendHdr { get; } = DebugHdrPlayback.Pretend;
        public bool MatchContentFrameRate { get; } = DebugDisplayMatch.Enabled;
        public string FrameDeliveryMode { get; } = DebugFrameDeliverySwitch.Mode;
        /// <summary>bale-debug.sh frame-log, captured when the attempt starts: frame:/frame-event: lines in bale.log.</summary>
        public bool FrameLog { get; } = DebugFrameLogSwitch.Enabled;
        public string StreamAdapter { get; } = DebugStreamAdapterSwitch.Mode;
        /// <summary>mediaplayer or engine (<see cref="EngineBackend"/>), captured when the attempt starts.</summary>
        public string PlayerBackend { get; } = DebugPlayerBackendSwitch.Mode;
        public bool UseEngine => PlayerBackend == "engine" && !EngineRefused;
        public bool UsePooledStreamAdapter => StreamAdapter == "pooled";
        /// <summary>The Rust range stream and its Rust IRandomAccessStream (docs/rust-port.md, slice 7).</summary>
        public bool UseRustTransport => StreamAdapter == "rust";
        /// <summary>`av1-claim 2160-dav1d`: rav1g's AV1 claim also applies with dav1d (memory/stress tests only).</summary>
        public bool Av1ClaimDav1d { get; } = DebugAv1ClaimSwitch.Dav1dToo;
        public int Av1ClaimMaxHeight { get; } = DebugAv1ClaimSwitch.MaxHeight;
        /// <summary>dav1d or rav1g for software AV1 (DebugAv1DecoderSwitch, default rav1g as in release), captured when the attempt starts.</summary>
        public string Av1Decoder { get; } = DebugAv1DecoderSwitch.Mode;
        public string Dav1dPath { get; } = DebugDav1dPathSwitch.Mode;
        public bool FfmpegEnabled => FfmpegMode != "off";
        public bool ForceFfmpeg => FfmpegMode == "direct";
#else
        public bool UsePooledStreamAdapter => true;
        public bool UseRustTransport => true;
        /// <summary>IMFMediaEngine is the player (owner, 2026-11-17), unless it could not open this attempt's stream.</summary>
        public bool UseEngine => !EngineRefused;
        public bool FfmpegEnabled => true;
        public bool ForceFfmpeg => false;
        /// <summary>Software AV1 decodes on rav1g (owner, 2026-11-26) wherever rav1g.dll is usable (Rav1gLibrary), else dav1d.</summary>
        public string Av1Decoder => "rav1g";
#endif
        /// <summary>
        /// HLS always plays through the Rust HLS reader and FFmpeg (owner, 2026-11-16 console A/B); Windows' HLS
        /// source was removed 2026-11-17, so a playlist the reader refuses fails to a replan.
        /// </summary>
        public bool HlsThroughFfmpeg => true;
        /// <summary>The engine could not open a stream of this attempt: its mounts use Windows MediaPlayer.</summary>
        public bool EngineRefused { get; set; }
        /// <summary>
        /// The mount whose start budget ran out while the buffering controller's lowering owns its recovery, until that
        /// lowering is adopted (silo-apple heldStartDeadlineFailure). A refused or failed lowering recovers it.
        /// </summary>
        public Mount? HeldStartDeadline { get; set; }
        /// <summary>The attempt's seek watchdog (PlayerPage.SeekWatchdog.cs): its one remount spans every seek and mount.</summary>
        public PlaybackSeekWatchdog? SeekWatchdog { get; set; }
        /// <summary>The source target of the seek whose failure the watchdog queued: the replan starts there.</summary>
        public double? StuckSeekTarget { get; set; }
        public CancellationTokenSource? ReportingCancellation { get; set; }
        public Task? ReportingTask { get; set; }
    }

    public PlayerPage()
    {
        InitializeComponent();
#if DEBUG_SERVER
        StatsPanel.Visibility = Visibility.Visible;
        ResetStats();
#endif
        InitializeSubtitleLayer();
        InitializeControls();
        InitializeTracks();
        InitializeSkipPill();
        _timer.Tick += OnTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _request = e.Parameter as PlayerRequest;
        _active = true;
        Application.Current.Suspending += OnSuspending;
        Application.Current.Resuming += OnResuming;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_active || _leaving || _suspended) return;
            // With no transport controls, the page itself owns Back until Retry becomes visible.
            FocusRetry.Until(this, () => !_active || ErrorPanel.Visibility == Visibility.Visible || Focus(FocusState.Programmatic));
            _timer.Start();
            await StartAttemptAsync();
        }
        catch (Exception ex) { LogFailure("load", ex); }
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // TvPlayerViewModel.kt retry(): resume exactly where it failed, with no skip-back nudge.
            if (_request is { } request)
                _request = request with
                {
                    OverridePosition = _sourcePosition > 0 ? _sourcePosition : null,
                    SuppressResumeRewind = true,
                };
            await StartAttemptAsync();
        }
        catch (Exception ex) { LogFailure("retry", ex); }
    }

    private async Task StartAttemptAsync()
    {
        if (_starting) return;
        _starting = true;
        await _lifecycle.WaitAsync();
        try
        {
            if (!_active || _leaving || _suspended) return;
            // Keep outgoing art intact during PageFrame's fade. Once the player covers it,
            // release cached UI before allocating a decoder/presenter in Xbox's shared pool.
            if (Frame is PageFrame pageFrame) await pageFrame.WhenTransitionSettledAsync();
            if (!_active || _leaving || _suspended) return;
            UiResidency.EnterPlayback();
            _resourceLease ??= PlaybackResources.Lifetime.EnterPlayer();
            _gcLatencyLease ??= PlaybackGcLatency.Shared.Acquire();
            await StopAttemptAsync();
            // A previous attempt or page may still hold its decoder memory; 4K starts on top of it can OOM.
            if (!await PlaybackMemoryRelease.Shared.WaitAsync(PlaybackMemoryRelease.StartWaitLimit))
                Log.Info("player: previous playback memory release still running");
            if (!_active || _leaving || _suspended) return;
            ErrorPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = string.Empty;
            _delivery = null;
            _duration = null;
            _sourcePosition = 0;
            _state = "loading";
#if DEBUG_SERVER
            _debugNavigationFailure = null;
            ResetStats();
#endif
            SetLoading(true);
            SetDisplayActive(true);
            if (!_speedMediaActive)
            {
                _speedMediaActive = true;
                ConnectionSpeedService.MediaStarting();
            }
            if (_request is not { } request || Session.Client is not { } client)
            {
                await ShowErrorAsync(PlaybackMessages.StartFailed("No active playback viewer."));
                return;
            }

            // A new attempt starts from the Detail page's tracks: the last one's picks no longer wait for a plan.
            _trackPicks.Clear();
            var attempt = _attempt = new Attempt(request, client);
            var ct = attempt.Cancellation.Token;
            // A new attempt of the title: the kept step-down estimate belonged to the last one (buffering-quality design).
            _bufferingController?.NewAttempt();
            // Its intro and credits decisions too (TvPlayerViewModel.kt resets the intro controller for new playback).
            ResetSkipMarkers();
            // The subtitle appearance as the server resolves it now (Settings › Subtitles or another device may have
            // changed it), read alongside the plan so a text track rarely waits for it.
            attempt.SubtitleAppearanceRead = ReadSubtitleAppearanceAsync(client);
            try
            {
                Log.Info("player: start");
#if DEBUG_SERVER
                if (attempt.DebugUrl is not null)
                {
                    await StartDebugUrlAsync(attempt);
                    return;
                }
#endif
                // The Rust pool has no API traffic to keep it warm: connect during the plan request.
                if (attempt.UseRustTransport) RustTransport.WarmConnection(client.Server);
                var services = attempt.Services = await PlaybackSessions.PrepareAsync(client);
                ct.ThrowIfCancellationRequested();
                // TvVideoPlaybackStarter.kt uses the leaf's user-data position. ResumeSeconds is
                // only PlayLabel's thresholded display value; never use a series container's clock.
                var startPosition = PlaybackStartPosition.ForStartRequest(request.OverridePosition,
                    request.Detail.PlaybackPositionSeconds,
                    request.SuppressResumeRewind ? 0 : PlaybackStartPosition.DefaultResumeRewindSeconds);
                // The production claims, shared with Auto's display matching on the detail page: AV1 up to 2160p60 at
                // 40 Mbps where rav1g.dll is usable (owner, 2026-11-26), else dav1d's 1080p60.
                var capabilities = await DetectClaimsAsync(attempt);
                ct.ThrowIfCancellationRequested();
                var start = PlaybackStartBuilder.Build(request.Detail, request.Selection,
                    services.ProfileId, Guid.NewGuid().ToString(), startPosition, capabilities, DeviceIdentity.Current());
#if DEBUG_SERVER
                start = DebugPlaybackDelivery.Apply(attempt.DeliveryMode, start);
                if (attempt.DeliveryMode != "auto") Log.Info("player: delivery forced=" + attempt.DeliveryMode);
#endif
                attempt.Start = start;
                // The route decodes a hardware codec in FFmpeg when these entries cannot take the file.
                attempt.HardwareDecode = capabilities.VideoDecode.Where(decode => decode.Hardware).ToArray();
                var decision = await client.Playback.StartAsync(start, services.Installation, ct);
#if DEBUG_SERVER
                DebugRecordPlaybackDecision(decision);
#endif
                var validation = PlaybackMessages.ValidatePlayable(decision);
                if (!validation.IsPlayable)
                {
                    await ShowErrorAsync(validation.Message ?? PlaybackMessages.NoSession);
                    return;
                }

                attempt.SessionId = decision.SessionId!;
                attempt.Plan = decision.PlaybackPlan!;
                attempt.ServerFeatures = decision.ServerFeatures ?? [];
                attempt.PlanAttemptId = Guid.NewGuid().ToString();
                var timeline = PlaybackTimeline.From(attempt.Plan.Timeline);
                attempt.Position = _sourcePosition = PlaybackProgressive.MountSourceStart(attempt.Plan,
                    timeline.SourceStart(startPosition, request.OverridePosition));
#if DEBUG_SERVER
                Log.Info($"player: start timeline request={startPosition:0.###} source={timeline.SourceStartSeconds:0.###} player={timeline.PlayerStartSeconds:0.###} offset={timeline.TimelineOffsetSeconds:0.###}");
#endif
                // Journal an admitted session even if Back arrived with the reply or opening fails.
                // Otherwise a failed mount could leave a server session with no final stop authority.
                await AttachSessionAsync(attempt);
                ct.ThrowIfCancellationRequested();
                AdoptBufferingPlan(attempt, controllerInitiated: false, client.Playback.StartEstimateKbps(start.PlaybackAttemptId));
                try
                {
                    await MountPlanAsync(attempt, PlaybackProgressive.MountPlayerStart(attempt.Plan, timeline.PlayerStart(request.OverridePosition)));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    LogFailure("open", ex);
                    await RecoverCoreAsync(attempt, ex.HResult, isDecoder: false, IsNetworkFailure(ex));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LogFailure("start", ex);
                if (_active && !_leaving && !_suspended)
                {
                    var message = ex switch
                    {
                        _ when IsNetworkFailure(ex) || ex is OperationCanceledException => PlaybackMessages.ServerUnreachable,
                        _ => PlaybackMessages.StartFailed("Please try again."),
                    };
                    await ShowErrorAsync(message);
                }
            }
        }
        finally
        {
            _starting = false;
            _lifecycle.Release();
        }
    }

    private async Task AttachSessionAsync(Attempt attempt)
    {
        var services = attempt.Services!;
        await services.Reporter.AttachSessionAsync(attempt.SessionId!, attempt.Position, isPaused: false);
        attempt.Attached = true;
        var cancellation = attempt.ReportingCancellation = new CancellationTokenSource();
        attempt.ReportingTask = ObserveReporterAsync(attempt, services.Reporter, attempt.SessionId!, cancellation.Token);
    }

    private async Task ObserveReporterAsync(Attempt attempt, PlaybackProgressReporter reporter, string sessionId, CancellationToken ct)
    {
        try
        {
            // The heartbeat returns only when the server ended the session (PlayerPage.ServerEnd.cs). A late answer about
            // a session this attempt already moved on from (a replan's new session) must not end the one that replaced it.
            var end = await reporter.RunAsync(sessionId, ct);
            if (!ct.IsCancellationRequested && attempt.SessionId == sessionId) LeaveServerEnded(attempt, end);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { LogFailure("reporter", ex); }
    }

    private async void OnTick(object? sender, object e)
    {
        // RecordPositionAsync needs each sample: it tracks the latest position in memory and
        // journals only 10 content seconds / pause changes; RunAsync owns the 10s HTTP heartbeat.
        if (_attempt is { Media: { Opened: true, Started: false, FailureQueued: false, Player: not null } first } current
            && IsCurrent(current, first)) UpdatePlaybackState(current, first);
        UpdateSpinner();
        UpdateHoldScan();
        UpdateUpNext();
        UpdateTransport();
        UpdateControls();
        // Before the buffering pill: a skip pill outranks it.
        UpdateSkipMarkers();
        UpdateVersionNotice();
        UpdateBufferingQuality();
        UpdateStartBudget();
        UpdateSeekWatchdog();
        // In-band closed captions: the first CC1 captions a mount's decoder sees add the CC row (about once a second until then).
        UpdateCaptions();
        // Drift correction about once per second while playing; SubtitleClockPush drops the other ticks.
        PushSubtitleClock();
        if (_recording || _seeking) return;
        _recording = true;
        try
        {
            var now = Environment.TickCount64;
            if (!_active || _leaving || _suspended || now - _lastSample < 1000
                || _attempt is not { Attached: true, Media: { Opened: true, FailureQueued: false } } attempt) return;
            _lastSample = now;
#if DEBUG_SERVER
            RefreshStats(attempt);
            // Range stats are a development diagnostic: counts only, never URLs.
            if (attempt.Media.Stream is { } range && now - _lastRangeLog >= 5000)
            {
                _lastRangeLog = now;
                Log.Info($"player: range reads={range.Reads} fetches={range.BlockFetches} hits={range.CacheHits} prefetchhits={range.PrefetchHits} bytes={range.BytesRead} minread={range.SmallestRead}");
            }
#else
            // The Options panel's Stats readout (in debug builds RefreshStats above reads it every second).
            if (OptionsOpen) RefreshHudStats(attempt);
#endif
            UpdateOptionsStats();
            var position = SourcePosition(attempt);
            var paused = attempt.Media.Player!.PlaybackSession.PlaybackState == MediaPlaybackState.Paused;
#if DEBUG_SERVER
            if (attempt.DebugUrl is not null) return;
#endif
            await attempt.Services!.Reporter.RecordPositionAsync(attempt.SessionId!, position, paused, attempt.Cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogFailure("position", ex); }
        finally { _recording = false; }
    }

    /// <summary>
    /// This attempt's claims (PlaybackClaims): AV1 up to 2160p60 at the 40 Mbps soft ceiling with a usable rav1g.dll, else
    /// dav1d's 1080p60. Debug builds apply the captured av1-decoder and av1-claim switches (defaults rav1g and 2160, as in
    /// release; dav1d or 1080 keep dav1d's 1080p60; 2160-dav1d forces rav1g's entry for memory/stress tests) and the other
    /// debug claim overrides. Logs the AV1 claim once per attempt.
    /// </summary>
    private static async Task<DeviceCapabilities> DetectClaimsAsync(Attempt attempt)
    {
#if DEBUG_SERVER
        var (capabilities, softwareVideo, rav1g) = await PlaybackClaims.DetectAsync(
            rav1g: attempt.Av1ClaimMaxHeight == 2160 && attempt.Av1Decoder == "rav1g");
        // Keep the probed hardware claims; only rav1g's AV1 entry is added, also for dav1d.
        var forced = attempt.Av1ClaimDav1d && softwareVideo && !rav1g;
        if (forced) capabilities = capabilities with { VideoDecode = ConsoleEvidence.BuildVideoDecode(await HardwareDecodeProbe.Limits, rav1g: true) };
        // rav1g-tune display=off has the stage process rav1g's frames: HDR AV1 stays server tone-mapped, as before 2026-11-24.
        if (!DebugRav1gTuning.DisplayOutputOn) capabilities = capabilities with { Av1DynamicRangeMappingSupported = false };
        capabilities = DebugFfmpegPlayback.Capabilities(attempt.FfmpegMode, capabilities);
        capabilities = DebugHdrPlayback.Capabilities(attempt.PretendHdr, capabilities);
        capabilities = attempt.FfmpegTuning.ToneMapCapabilities(capabilities);
        if (softwareVideo && attempt.FfmpegEnabled)
            Log.Info(rav1g ? "player: av1 claim=2160p60 40 Mbps (rav1g)"
                : forced ? "player: av1 claim=2160p60 40 Mbps (debug av1-claim 2160-dav1d)"
                : !Rav1gLibrary.Usable ? $"player: av1 claim=1080p60 (dav1d; {Rav1gLibrary.Detail})"
                : attempt.Av1Decoder != "rav1g" ? "player: av1 claim=1080p60 (dav1d; debug av1-decoder dav1d)"
                : "player: av1 claim=1080p60 (dav1d; debug av1-claim 1080)");
        return capabilities;
#else
        var (capabilities, softwareVideo, rav1g) = await PlaybackClaims.DetectAsync();
        if (softwareVideo)
            Log.Info(rav1g ? "player: av1 claim=2160p60 40 Mbps (rav1g)" : $"player: av1 claim=1080p60 (dav1d; {Rav1gLibrary.Detail})");
        return capabilities;
#endif
    }

    /// <summary>
    /// Remount the current plan once more on another local path (no server replan), from the same source position.
    /// The caller has marked the attempt so the new mount takes that path; a failed open takes the single replan.
    /// </summary>
    private async Task RemountSamePlanAsync(Attempt attempt, double position, string reason, PlaybackFailureV3? failure = null)
    {
        HideTransport();
        _state = "loading";
        SetLoading(true);
        DisposeMount(attempt);
        DisposeSubtitles(keepFetched: true);
        await _hdrOutput.RestoreAsync();
        attempt.Cancellation.Token.ThrowIfCancellationRequested();
        Log.Info(reason);
        var current = PlaybackTimeline.From(attempt.Plan!.Timeline);
        try
        {
            await MountPlanAsync(attempt, PlaybackProgressive.MountPlayerStart(attempt.Plan,
                current.PlayerForSource(position) ?? current.PlayerStartSeconds));
        }
        catch (Exception ex) when (!attempt.Cancellation.IsCancellationRequested)
        {
            // The remount's open failed (or its start budget stopped it): the single replan decides, as for the start.
            LogFailure("open", ex);
            // A transport stall's remount keeps its classification, so the replan keeps every claim.
            await RecoverCoreAsync(attempt, ex.HResult, isDecoder: false, IsNetworkFailure(ex), failure: failure);
        }
    }

    private bool IsCurrent(Attempt attempt, Mount media) => _active && !_leaving && !_suspended
        && ReferenceEquals(_attempt, attempt) && ReferenceEquals(attempt.Media, media)
        && !attempt.Cancellation.IsCancellationRequested;

    private void QueueFailure(Attempt attempt, Mount media, int hresult, bool isDecoder, bool isNetwork,
        PlaybackFailureV3? failure = null)
    {
        if (!IsCurrent(attempt, media) || media.FailureQueued) return;
        media.FailureQueued = true;
        media.Opening.TrySetResult(false);
        Pause(attempt);
        _ = RecoverAsync(attempt, media, hresult, isDecoder, isNetwork, failure);
    }

    private async Task RecoverAsync(Attempt attempt, Mount media, int hresult, bool isDecoder, bool isNetwork, PlaybackFailureV3? failure)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (IsCurrent(attempt, media)) await RecoverCoreAsync(attempt, hresult, isDecoder, isNetwork, failure: failure);
        }
        catch (OperationCanceledException) when (attempt.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogFailure("recovery", ex);
            if (_active && !_leaving && !_suspended) await ShowErrorAsync(PlaybackMessages.PlaybackFailed);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task RecoverCoreAsync(Attempt attempt, int hresult, bool isDecoder, bool isNetwork, bool hdrOutputFailed = false,
        PlaybackFailureV3? failure = null)
    {
        if (!_active || _leaving || _suspended || attempt.Cancellation.IsCancellationRequested) return;
#if DEBUG_SERVER
        if (attempt.DebugUrl is not null)
        {
            await DebugUrlFailedAsync(hdrOutputFailed ? "HDR display switch failed"
                : attempt.Media?.StartBudgetRanOut == true ? "playback start budget expired" : "player pipeline failed; no server recovery", hresult);
            return;
        }
#endif
        // A mount whose start budget ran out fails as such, whichever path noticed (its interrupted open or the queue).
        if (attempt.Media is { StartBudgetRanOut: true } && failure is null && !hdrOutputFailed) failure = PlaybackStartBudget.Failure;
        if (StartDeadlineLoweringOwns(attempt))
        {
            // The buffering controller's lowering replaces this mount, or hands it back if it is refused.
            Log.Info("player: failed open left to the start budget's lowering");
            return;
        }
        Log.Info($"player: failed hr=0x{unchecked((uint)hresult):X8}");
        var position = SourcePosition(attempt);
        // A stuck seek's replan starts where the viewer asked to go, not at the clock that never moved.
        if (PlaybackSeekWatchdog.Is(failure) && attempt.StuckSeekTarget is { } stuckTarget)
            attempt.Position = _sourcePosition = position = stuckTarget;
        Pause(attempt);
        // A start that ran out of time, or a seek still stuck after the seek watchdog's same-plan remount, withdraws no
        // claim (PlaybackReplan.ForFailure): no other same-plan remount, and the claim guards below do not apply.
        var keepsClaims = PlaybackStartBudget.Is(failure) || PlaybackSeekWatchdog.Is(failure) || PlaybackTransportStall.Is(failure);
        if (PlaybackTransportStall.Is(failure))
        {
            // silo-android: a network failure retries the same route once (renewed after recovered playback) before the
            // replan. Its open fails at once while the server is still away, and the replan then decides.
            var now = Environment.TickCount64;
            attempt.TransportStallRemounts = PlaybackTransportStall.RemountsAfterRenewal(attempt.TransportStallRemounts,
                attempt.TransportStallRemountAt, now);
            if (PlaybackTransportStall.RemountsSamePlan(attempt.TransportStallRemounts, attempt.TransportStallRemountAt, now))
            {
                attempt.TransportStallRemounts++;
                attempt.TransportStallRemountAt = now;
                await RemountSamePlanAsync(attempt, position, "player: range stream gave up; remounting the same plan", failure);
                return;
            }
        }
        // A subtitle failure says nothing about the video stage; remounting the same plan would fail it again. Nor does
        // a start that ran out of time: the server's next route is the answer, not the same plan on another decoder.
        if (attempt.Media is { Route.ConsoleDownscale: true } downscaled && !attempt.SdrDownscaleDeclined && !hdrOutputFailed
            && !PlaybackEmbeddedSubtitleFailure.Is(failure) && !keepsClaims)
        {
            // The SDR scale-only stage is an optimisation, not a new promise: the stage can decline a file
            // (e.g. unspecified colour tags) and the guard then rejects it. Remount the same plan once on the
            // system decoder (MF scales it as before) without spending the server replan. A software route keeps
            // FFmpeg software without the stage; above 1080p the route then refuses it (MF must never scale those
            // frames down), so that remount fails at once to the replan (docs/software-decode.md phase B).
            attempt.SdrDownscaleDeclined = true;
            await RemountSamePlanAsync(attempt, position, downscaled.Route.SoftwareVideo
                ? "player: console downscale declined; remounting the software route without the stage"
                : "player: console downscale declined; remounting on the system decoder");
            return;
        }
        // rav1g (owner, 2026-11-26: every build): a rav1g mount that failed (the fork's latched NotifyError, or the guard
        // rejecting its output) remounts once on dav1d/D3D11 only up to 1080 lines; taller sources take the ordinary replan.
        if (attempt.Media is { } failedMount && Rav1gPolicy.RemountOnDav1d(failedMount.Rav1gRoute, failedMount.Rav1gActive,
                attempt.Rav1gRemounted, hdrOutputFailed, PlaybackEmbeddedSubtitleFailure.Is(failure), keepsClaims, failedMount.Route.VideoHeight))
        {
            attempt.Rav1gRemounted = true;
            await RemountSamePlanAsync(attempt, position, "player: rav1g failed; remounting the same plan on dav1d");
            return;
        }
        if (attempt.Replanned)
        {
            await ShowErrorAsync(PlaybackMessages.PlaybackFailed);
            return;
        }

        attempt.Replanned = true;
        HideTransport();
        _state = "loading";
        SetLoading(true);
        var failedRoute = attempt.Media?.Route;
        var failedDolbyVision7 = DolbyVision7Policy.RequiresBaseLayer(attempt.Plan!);
        DisposeMount(attempt);
        // The failed pipeline's subtitles go with it; the replacement mount loads its own track.
        DisposeSubtitles(keepFetched: true);
        await _hdrOutput.RestoreAsync();
        attempt.Cancellation.Token.ThrowIfCancellationRequested();
        Log.Info("player: replan");
        try
        {
            // PlaybackSessionManager.kt prepareActiveVideoSessionReplan: one failure-recovery
            // fallback, carrying the failed plan's identity and the SOURCE clock, not player time.
            var services = attempt.Services!;
            failure ??= PlaybackReplan.Classify(hresult, isDecoder, isNetwork);
            var request = hdrOutputFailed
                ? PlaybackReplan.ForHdrOutputFailure(attempt.Start!, attempt.Plan!, attempt.PlanAttemptId, [], 1, position)
                : PlaybackReplan.ForFailure(attempt.Start!, attempt.Plan!, attempt.PlanAttemptId,
                    [], 1, position, failure, failedRoute);
            // After a lowering, the recovery keeps asking for it (the kept step-down estimate, no source).
            request = PlaybackReplan.WithClientReplan(request, ClientBufferingReplan("failure_recovery"));
            var decision = await attempt.Client.Playback.ReplanAsync(attempt.SessionId!, request,
                services.Installation, attempt.Cancellation.Token);
            // Outcome and reason are server enum codes; never log the plan, its URL or message.
            Log.Info($"player: replan outcome={decision.Outcome} reason={decision.Terminal?.Reason}");
#if DEBUG_SERVER
            DebugRecordPlaybackDecision(decision);
#endif
            if (!PlaybackMessages.ValidatePlayable(decision).IsPlayable)
            {
                await ShowErrorAsync(PlaybackMessages.PlaybackFailed);
                return;
            }

            if (decision.SessionId != attempt.SessionId)
            {
                await StopSessionAsync(attempt, position);
                attempt.SessionId = decision.SessionId!;
                attempt.Position = position;
                await AttachSessionAsync(attempt);
            }

            attempt.Plan = decision.PlaybackPlan!;
            attempt.ServerFeatures = decision.ServerFeatures ?? [];
            attempt.AttemptedPlanKeys = request.AttemptedPlanKeys;
            attempt.Start = attempt.Start! with
            {
                ClientFeatures = request.ClientFeatures,
                ClientCapabilities = request.ClientCapabilities,
                ClientPlaybackContext = request.ClientPlaybackContext,
            };
            attempt.PlanAttemptId = Guid.NewGuid().ToString();
            AdoptBufferingPlan(attempt, controllerInitiated: false, request.BandwidthEstimateKbps);
            attempt.Cancellation.Token.ThrowIfCancellationRequested();
            if (hdrOutputFailed && DisplayHdrPolicy.RequiresHdr10(attempt.Plan))
            {
                // Fail closed if the replacement ignores the withdrawn HDR output claim.
                await ShowErrorAsync(PlaybackMessages.PlaybackFailed);
                return;
            }
            // A start that ran out of time or a stuck seek withdrew no claim (PlaybackReplan.ForFailure): the next route may use them.
            if (failedDolbyVision7 && !keepsClaims && DolbyVision7Policy.RequiresBaseLayer(attempt.Plan))
            {
                // The recovery withdrew both DV7 promises. Never mount the same local conversion again.
                await ShowErrorAsync(PlaybackMessages.PlaybackFailed);
                return;
            }
            if (failedRoute?.ConsoleToneMap == true && !keepsClaims && ConsoleToneMapPolicy.RequiresToneMap(attempt.Plan,
                !ConsoleToneMapPolicy.HasHdrOutput(attempt.Start.ClientPlaybackContext.Output.HdrDetails), attempt.Request.Detail.Versions,
                attempt.HlsThroughFfmpeg))
            {
                // Do not retry a failed local stage if the server ignored claim withdrawal.
                await ShowErrorAsync(PlaybackMessages.PlaybackFailed);
                return;
            }
            var timeline = PlaybackTimeline.From(attempt.Plan.Timeline);
            await MountPlanAsync(attempt, PlaybackProgressive.MountPlayerStart(attempt.Plan,
                timeline.PlayerForSource(position) ?? timeline.PlayerStartSeconds));
        }
        catch (OperationCanceledException) when (attempt.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // The server ended the session: leave with its notice; no error screen, no other attempt.
            if (LeftForServerEnd(attempt, ex)) return;
            LogFailure("replan", ex);
            // The replacement's start budget ran out and the buffering controller lowers instead.
            if (StartDeadlineLoweringOwns(attempt)) return;
            if (_active && !_leaving && !_suspended) await ShowErrorAsync(PlaybackMessages.PlaybackFailed);
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        _state = "error";
        ErrorText.Text = message;
        SetLoading(false);
        await StopAttemptAsync();
        SetDisplayActive(false);
        // Let the spinner's minimum-visible interval finish before replacing it with TvErrorScreen.
        while (_active && !_leaving && !_suspended && _spinner.Visible)
        {
            await Task.Delay(50);
            UpdateSpinner();
        }
        if (!_active || _leaving || _suspended) return;
        ErrorPanel.Visibility = Visibility.Visible;
        FocusRetry.Until(ErrorPanel, () => !_active || ErrorPanel.Visibility != Visibility.Visible
            || RetryButton.Focus(FocusState.Programmatic));
    }

    private void SetLoading(bool loading)
    {
        _loading = loading;
        UpdateSpinner();
    }

    private void UpdateSpinner()
    {
        var visible = _spinner.Update(_loading, Environment.TickCount64);
        LoadingRing.IsActive = visible;
        LoadingRing.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetDisplayActive(bool active)
    {
        if (_displayActive == active) return;
        try
        {
            if (active) _display.RequestActive();
            else _display.RequestRelease();
            _displayActive = active;
        }
        catch (Exception ex) { LogFailure("display", ex); }
    }

    private static void Pause(Attempt attempt)
    {
        try { attempt.Media?.Player?.Pause(); }
        catch (Exception ex) { LogFailure("pause", ex); }
    }

    private async Task StopSessionAsync(Attempt attempt, double position)
    {
        attempt.ReportingCancellation?.Cancel();
        if (attempt.ReportingTask is { } task) await task;
        attempt.ReportingCancellation?.Dispose();
        attempt.ReportingCancellation = null;
        attempt.ReportingTask = null;
        if (!attempt.Attached) return;
        attempt.Attached = false;
#if DEBUG_SERVER
        if (attempt.DebugUrl is not null) return;
#endif
        try
        {
            // The durable stop survives a timeout; keep Back and the suspension deferral bounded.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await attempt.Services!.Reporter.StopAsync(attempt.SessionId!, position, isPaused: true, deadline.Token);
        }
        catch (Exception ex) { LogFailure("stop", ex); }
    }

    private async Task StopAttemptAsync()
    {
        HideTransport();
        // Stop, error, shutdown, navigation and suspend all end here: release the overlay's D3D device.
        DisposeSubtitles();
        if (_attempt is not { } attempt)
        {
            await _hdrOutput.RestoreAsync();
            return;
        }
        Pause(attempt);
        var position = SourcePosition(attempt);
        // The plan stops being the playing plan: its plan_summary goes out (never awaited here).
        EndBufferingPlan();
        attempt.Cancellation.Cancel();
        try
        {
            // Restore before a potentially slow final server report, including suspend/error paths.
            DisposeMount(attempt);
            await _hdrOutput.RestoreAsync();
            Log.Info("player: stop");
            await StopSessionAsync(attempt, position);
        }
        finally
        {
            DisposeMount(attempt);
            StopWatchingSeek();
            Cleanup("dispose seek watchdog", () => attempt.SeekWatchdog?.Dispose());
            attempt.SeekWatchdog = null;
            attempt.Cancellation.Dispose();
            if (ReferenceEquals(_attempt, attempt)) _attempt = null;
            // Explicit Close/Trim has released native owners; collect leftover unreachable wrappers.
            _ = PlaybackMemoryRelease.Shared.Schedule();
        }
    }

    public bool OnBack() => OnBack(closeControls: true);

    /// <summary>Back closes a scrub preview, then the controls, then leaves; Stop and Close leave at once.</summary>
    private bool OnBack(bool closeControls)
    {
#if DEBUG_SERVER
        if (_enginePlayer is not null)
        {
            _ = CloseEnginePlayerAsync(leave: true);
            return true;
        }
#endif
        if (closeControls && BackClosesControls()) return true;
        if (!_leaving) _ = StopAndGoBackAsync();
        return true;
    }

    private async Task StopAndGoBackAsync()
    {
        _leaving = true;
        RecordSeriesReturn();
        var frame = Frame;
        try { await ShutdownAsync(); }
        catch (Exception ex) { LogFailure("back", ex); }
        finally
        {
            if (ReferenceEquals(frame?.Content, this) && frame.CanGoBack) frame.GoBack();
        }
    }

    // Speed tests never run while this page loads or plays media (ConnectionSpeedService).
    private bool _speedMediaActive;

    private Task ShutdownAsync()
    {
        if (_shutdown is not null) return _shutdown;
        _active = false;
        if (_speedMediaActive)
        {
            _speedMediaActive = false;
            ConnectionSpeedService.MediaEnded();
        }
        _attempt?.Cancellation.Cancel();
        if (_attempt is { } attempt)
        {
            Pause(attempt);
            SourcePosition(attempt);
        }
        _timer.Stop();
        HideTransport();
        // Do not wait for the lifecycle lock: suspend and navigation release the overlay's device now.
        DisposeSubtitles();
        SetLoading(false);
        return _shutdown = ShutdownCoreAsync();
    }

    private async Task ShutdownCoreAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopAttemptAsync(); }
        catch (Exception ex) { LogFailure("shutdown", ex); }
        finally
        {
            await _hdrOutput.RestoreAsync();
            SetDisplayActive(false);
            _gcLatencyLease?.Dispose();
            _gcLatencyLease = null;
            Cleanup("release video presenter", ReleaseVideoPresenter);
            // The idle trim follows the last mount's native close, which runs on the thread pool (DisposeMount).
            await WaitForMountCloseAsync("shutdown");
            _resourceLease?.Dispose();
            _resourceLease = null;
            Cleanup("release idle resources", () => PlaybackResources.ReleaseIdle("teardown"));
            _lifecycle.Release();
        }
    }

    private async void OnSuspending(object sender, SuspendingEventArgs e)
    {
        var deferral = e.SuspendingOperation.GetDeferral();
        try
        {
            Log.Info("player: suspending");
            await SuspendPlaybackAsync();
        }
        catch (Exception ex) { LogFailure("suspend", ex); }
        finally { deferral.Complete(); }
    }

    // EnteredBackground and Suspending share the same idempotent shutdown. No resume auto-play.
    internal Task SuspendPlaybackAsync()
    {
        _suspended = true;
        return ShutdownAsync();
    }

    private async void OnResuming(object? sender, object e)
        => await RestoreAfterBackgroundAsync();

    internal async Task RestoreAfterBackgroundAsync()
    {
        try
        {
            if (!_suspended) return;
            if (_shutdown is { } shutdown) await shutdown;
            if (!_suspended || _leaving || Application.Current is App { IsInBackground: true }
                || !ReferenceEquals(Frame?.Content, this)) return;
            _suspended = false;
            _active = true;
            _shutdown = null;
            _timer.Start();
            // Suspension stops the session, rather than playing unexpectedly on resume. Retry
            // starts a fresh attempt from the final source position (OnRetryClick).
            await ShowErrorAsync(PlaybackMessages.PlaybackFailed);
        }
        catch (Exception ex) { LogFailure("resume", ex); }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Application.Current.Suspending -= OnSuspending;
        Application.Current.Resuming -= OnResuming;
        _ = ShutdownAsync();
        base.OnNavigatedFrom(e);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _ = ShutdownAsync();

    private async Task DispatchAsync(Action action)
    {
        // MediaPlayer raises callbacks off the XAML thread. Catch both the
        // dispatch and the callback body: uncaught native-event exceptions can terminate UWP/AOT.
        try
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try { action(); }
                catch (Exception ex) { LogFailure("callback", ex); }
            });
        }
        catch (Exception ex) { LogFailure("dispatch", ex); }
    }

    private static bool IsNetworkFailure(Exception ex) => ex is HttpRequestException or IOException
        || ex is ApiException { ResponseStatusCode: 0 };

    private static void LogFailure(string operation, Exception ex) =>
        Log.Info($"player: {operation} type={ex.GetType().Name} hr=0x{unchecked((uint)ex.HResult):X8}"
            + (ex is Bale.Core.Api.Models.Problem problem ? " " + PlaybackProblemLog.Describe(problem) : "")
#if DEBUG_SERVER
            // Debug: Bale's own InvalidDataException texts are fixed sentences naming the failed step; anything long or
            // holding a URL stays unlogged (2026-11-24: a play-url open failed with no way to tell which check threw).
            + (ex is InvalidDataException { Message: { Length: > 0 and < 120 } text } && !text.Contains("://", StringComparison.Ordinal)
                ? $" reason=\"{text}\"" : "")
#endif
        );

#if DEBUG_SERVER
    internal string DebugPlayback => string.Create(CultureInfo.InvariantCulture,
        $"state={_state} delivery={_delivery ?? "none"} source={(_attempt is { } attempt ? SourcePosition(attempt) : _sourcePosition):0.###}s"
        + $" duration={(_duration is { } duration ? duration.ToString("0.###", CultureInfo.InvariantCulture) : "unknown")}s"
        + $" spinner={(_spinner.Visible ? "visible" : "hidden")}");

    internal string DebugError => ErrorText.Text;

    internal MediaPlayer? DebugMediaPlayer => _attempt?.Media?.NativePlayer;

    internal bool DebugFailPlayback()
    {
        if (_attempt is not { Media: { Player: not null, FailureQueued: false } media } attempt
            || !IsCurrent(attempt, media)) return false;
        QueueFailure(attempt, media, unchecked((int)0xC00D5212), isDecoder: true, isNetwork: false);
        return true;
    }
#endif
}
