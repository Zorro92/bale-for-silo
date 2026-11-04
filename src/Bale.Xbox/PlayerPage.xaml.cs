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

/// <summary>The bare full-screen player; transport/HUD controls are a later milestone.</summary>
public sealed partial class PlayerPage : Page, IBackHandler
{
    // All lifecycle operations run on the UI thread and serialize across awaits. Native callbacks
    // carry their mount identity so an old pipeline cannot fail or start its replacement.
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DisplayRequest _display = new();
    private readonly PlayerSpinnerVisibility _spinner = new();
    private PlayerRequest? _request;
    private Attempt? _attempt;
    private Task? _shutdown;
    private bool _active;
    private bool _leaving;
    private bool _suspended;
    private bool _displayActive;
    private bool _loading;
    private bool _recording;
    private bool _starting;
    private long _lastSample;
    private string _state = "loading";
    private string? _delivery;
    private double _sourcePosition;
    private double? _duration;

    private sealed class Attempt(PlayerRequest request, SiloClient client)
    {
        public PlayerRequest Request { get; } = request;
        public SiloClient Client { get; } = client;
        public CancellationTokenSource Cancellation { get; } = new();
        public PlaybackSessionServices? Services { get; set; }
        public PlaybackStartRequestV3? Start { get; set; }
        public PlaybackPlan? Plan { get; set; }
        public string PlanAttemptId { get; set; } = string.Empty;
        public string? SessionId { get; set; }
        public bool Attached { get; set; }
        public bool Replanned { get; set; }
        public double Position { get; set; }
        public Mount? Media { get; set; }
        public CancellationTokenSource? ReportingCancellation { get; set; }
        public Task? ReportingTask { get; set; }
    }

    public PlayerPage()
    {
        InitializeComponent();
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
            await StopAttemptAsync();
            ErrorPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = string.Empty;
            _delivery = null;
            _duration = null;
            _sourcePosition = 0;
            _state = "loading";
            SetLoading(true);
            SetDisplayActive(true);
            if (_request is not { } request || Session.Client is not { } client)
            {
                await ShowErrorAsync(PlaybackMessages.StartFailed("No active playback viewer."));
                return;
            }

            var attempt = _attempt = new Attempt(request, client);
            var ct = attempt.Cancellation.Token;
            try
            {
                Log.Info("player: start");
                var services = attempt.Services = await PlaybackSessions.PrepareAsync(client);
                ct.ThrowIfCancellationRequested();
                // TvVideoPlaybackStarter.kt uses the leaf's user-data position. ResumeSeconds is
                // only PlayLabel's thresholded display value; never use a series container's clock.
                var startPosition = PlaybackStartPosition.ForStartRequest(request.OverridePosition,
                    request.Detail.PlaybackPositionSeconds,
                    request.SuppressResumeRewind ? 0 : PlaybackStartPosition.DefaultResumeRewindSeconds);
                var start = attempt.Start = PlaybackStartBuilder.Build(request.Detail, request.Selection,
                    services.ProfileId, Guid.NewGuid().ToString(), startPosition, ConsoleEvidence.Default, DeviceIdentity.Current());
                var decision = await client.Playback.StartAsync(start, services.Installation, ct);
                var validation = PlaybackMessages.ValidatePlayable(decision);
                if (!validation.IsPlayable)
                {
                    await ShowErrorAsync(validation.Message ?? PlaybackMessages.NoSession);
                    return;
                }

                attempt.SessionId = decision.SessionId!;
                attempt.Plan = decision.PlaybackPlan!;
                attempt.PlanAttemptId = Guid.NewGuid().ToString();
                var timeline = PlaybackTimeline.From(attempt.Plan.Timeline);
                attempt.Position = _sourcePosition = timeline.SourceStart(startPosition, request.OverridePosition);
                // Journal an admitted session even if Back arrived with the reply or opening fails.
                // Otherwise a failed mount could leave a server session with no final stop authority.
                await AttachSessionAsync(attempt);
                ct.ThrowIfCancellationRequested();
                try
                {
                    await MountPlanAsync(attempt, timeline.PlayerStart(request.OverridePosition));
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
                        Problem problem => PlaybackMessages.StartFailed(problem.Detail ?? problem.Title ?? "Please try again."),
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
        attempt.ReportingTask = ObserveReporterAsync(services.Reporter, attempt.SessionId!, cancellation.Token);
    }

    private static async Task ObserveReporterAsync(PlaybackProgressReporter reporter, string sessionId, CancellationToken ct)
    {
        try { await reporter.RunAsync(sessionId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { LogFailure("reporter", ex); }
    }

    private async void OnTick(object? sender, object e)
    {
        // RecordPositionAsync needs each sample: it tracks the latest position in memory and
        // journals only 10 content seconds / pause changes; RunAsync owns the 10s HTTP heartbeat.
        UpdateSpinner();
        if (_recording) return;
        _recording = true;
        try
        {
            var now = Environment.TickCount64;
            if (!_active || _leaving || _suspended || now - _lastSample < 1000
                || _attempt is not { Attached: true, Media: { Opened: true, FailureQueued: false } } attempt) return;
            _lastSample = now;
            var position = SourcePosition(attempt);
            var paused = attempt.Media.Player!.PlaybackSession.PlaybackState == MediaPlaybackState.Paused;
            await attempt.Services!.Reporter.RecordPositionAsync(attempt.SessionId!, position, paused, attempt.Cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogFailure("position", ex); }
        finally { _recording = false; }
    }

    private double SourcePosition(Attempt attempt)
    {
        try
        {
            if (attempt.Media is { Opened: true, Player: { } player } media
                && media.Transport.Timeline.SourceForPlayer(player.PlaybackSession.Position.TotalSeconds) is { } source)
                attempt.Position = source;
        }
        catch (Exception ex) { LogFailure("position snapshot", ex); }
        return _sourcePosition = attempt.Position;
    }

    private bool IsCurrent(Attempt attempt, Mount media) => _active && !_leaving && !_suspended
        && ReferenceEquals(_attempt, attempt) && ReferenceEquals(attempt.Media, media)
        && !attempt.Cancellation.IsCancellationRequested;

    private void QueueFailure(Attempt attempt, Mount media, int hresult, bool isDecoder, bool isNetwork)
    {
        if (!IsCurrent(attempt, media) || media.FailureQueued) return;
        media.FailureQueued = true;
        _ = RecoverAsync(attempt, media, hresult, isDecoder, isNetwork);
    }

    private async Task RecoverAsync(Attempt attempt, Mount media, int hresult, bool isDecoder, bool isNetwork)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (IsCurrent(attempt, media)) await RecoverCoreAsync(attempt, hresult, isDecoder, isNetwork);
        }
        catch (OperationCanceledException) when (attempt.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogFailure("recovery", ex);
            if (_active && !_leaving && !_suspended) await ShowErrorAsync(PlaybackMessages.PlaybackFailed);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task RecoverCoreAsync(Attempt attempt, int hresult, bool isDecoder, bool isNetwork)
    {
        if (!_active || _leaving || _suspended || attempt.Cancellation.IsCancellationRequested) return;
        Log.Info($"player: failed hr=0x{unchecked((uint)hresult):X8}");
        var position = SourcePosition(attempt);
        Pause(attempt);
        if (attempt.Replanned)
        {
            await ShowErrorAsync(PlaybackMessages.PlaybackFailed);
            return;
        }

        attempt.Replanned = true;
        _state = "loading";
        SetLoading(true);
        DisposeMount(attempt);
        Log.Info("player: replan");
        try
        {
            // PlaybackSessionManager.kt prepareActiveVideoSessionReplan: one failure-recovery
            // fallback, carrying the failed plan's identity and the SOURCE clock, not player time.
            var services = attempt.Services!;
            var failure = PlaybackReplan.Classify(hresult, isDecoder, isNetwork);
            var request = PlaybackReplan.ForFailure(attempt.Start!, attempt.Plan!, attempt.PlanAttemptId,
                [], 1, position, failure);
            var decision = await attempt.Client.Playback.ReplanAsync(attempt.SessionId!, request,
                services.Installation, attempt.Cancellation.Token);
            // Outcome and reason are server enum codes; never log the plan, its URL or message.
            Log.Info($"player: replan outcome={decision.Outcome} reason={decision.Terminal?.Reason}");
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
            attempt.PlanAttemptId = Guid.NewGuid().ToString();
            attempt.Cancellation.Token.ThrowIfCancellationRequested();
            var timeline = PlaybackTimeline.From(attempt.Plan.Timeline);
            await MountPlanAsync(attempt, timeline.PlayerForSource(position) ?? timeline.PlayerStartSeconds);
        }
        catch (OperationCanceledException) when (attempt.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogFailure("replan", ex);
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
        if (_attempt is not { } attempt) return;
        Pause(attempt);
        var position = SourcePosition(attempt);
        attempt.Cancellation.Cancel();
        try
        {
            Log.Info("player: stop");
            await StopSessionAsync(attempt, position);
        }
        finally
        {
            DisposeMount(attempt);
            attempt.Cancellation.Dispose();
            if (ReferenceEquals(_attempt, attempt)) _attempt = null;
        }
    }

    public bool OnBack()
    {
        if (!_leaving) _ = StopAndGoBackAsync();
        return true;
    }

    private async Task StopAndGoBackAsync()
    {
        _leaving = true;
        var frame = Frame;
        try { await ShutdownAsync(); }
        catch (Exception ex) { LogFailure("back", ex); }
        finally
        {
            if (ReferenceEquals(frame?.Content, this) && frame.CanGoBack) frame.GoBack();
        }
    }

    private Task ShutdownAsync()
    {
        if (_shutdown is not null) return _shutdown;
        _active = false;
        _attempt?.Cancellation.Cancel();
        if (_attempt is { } attempt)
        {
            Pause(attempt);
            SourcePosition(attempt);
        }
        _timer.Stop();
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
            SetDisplayActive(false);
            _lifecycle.Release();
        }
    }

    private async void OnSuspending(object sender, SuspendingEventArgs e)
    {
        var deferral = e.SuspendingOperation.GetDeferral();
        try
        {
            _suspended = true;
            await ShutdownAsync();
        }
        catch (Exception ex) { LogFailure("suspend", ex); }
        finally { deferral.Complete(); }
    }

    private async void OnResuming(object sender, object e)
    {
        try
        {
            if (!_suspended) return;
            if (_shutdown is { } shutdown) await shutdown;
            if (_leaving || !ReferenceEquals(Frame?.Content, this)) return;
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
        // MediaPlayer and AdaptiveMediaSource raise callbacks off the XAML thread. Catch both the
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
        Log.Info($"player: {operation} type={ex.GetType().Name} hr=0x{unchecked((uint)ex.HResult):X8}");

#if DEBUG_SERVER
    internal string DebugPlayback => string.Create(CultureInfo.InvariantCulture,
        $"state={_state} delivery={_delivery ?? "none"} source={(_attempt is { } attempt ? SourcePosition(attempt) : _sourcePosition):0.###}s"
        + $" duration={(_duration is { } duration ? duration.ToString("0.###", CultureInfo.InvariantCulture) : "unknown")}s"
        + $" spinner={(_spinner.Visible ? "visible" : "hidden")}");

    internal string DebugError => ErrorText.Text;

    internal bool DebugFailPlayback()
    {
        if (_attempt is not { Media: { Player: not null, FailureQueued: false } media } attempt
            || !IsCurrent(attempt, media)) return false;
        QueueFailure(attempt, media, unchecked((int)0xC00D5212), isDecoder: true, isNetwork: false);
        return true;
    }
#endif
}
