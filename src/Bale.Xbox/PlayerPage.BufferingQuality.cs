using System.Globalization;
using Bale.Core;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

/// <summary>
/// Lowering quality when playback keeps buffering (silo-server docs/design/2026-10-17-buffering-quality.md; playback
/// follows silo-apple, the pill and notices follow TvPlayerNoticeOverlay.kt). Core's
/// <see cref="BufferingQualityController"/> decides; this page feeds it the playing plan, the backend's state through
/// <see cref="BufferingSignals"/>, and the plan's measured throughput, sends its quality_change replans on the same
/// session, tracks and position (as a track change does), shows its pill and notices, and posts each plan's
/// <c>plan_summary</c> when the server advertises playback_plan_summary_v1.
/// </summary>
public sealed partial class PlayerPage
{
    private readonly PolledBufferingQualityClock _bufferingClock = new(() => Environment.TickCount64);
    private readonly BufferingSignals _bufferingSignals = new();
    private readonly PlanThroughput _planThroughput = new();
    private readonly MediaSilence _mediaSilence = new();
    private readonly Dictionary<string, BufferingPlanRecord> _bufferingPlans = new(StringComparer.Ordinal);
    private BufferingQualityController? _bufferingController;
    private long _bufferingAdoptions;
    private bool _bufferingWantsToPlay = true;
    private bool _bufferingEnded;
    // A quality or track change remounts: its loading is a reload, not a seek.
    private bool _remounting;
    private BufferingQualityNotice? _bufferingNotice;
    private long _bufferingNoticeLeft;
    private long? _bufferingNoticeShownAt;
    private BufferingPillKind? _bufferingPillShown;

    /// <summary>Who to tell about one adopted plan when it stops playing.</summary>
    private sealed class BufferingPlanRecord(PlanSummaryContext context, SiloClient client, string installationId, int networkChanges)
    {
        public PlanSummaryContext Context { get; } = context;
        public SiloClient Client { get; } = client;
        public string InstallationId { get; } = installationId;
        public int NetworkChanges { get; } = networkChanges;
        /// <summary>Played over HLS: segments arrive at the connection's burst rate, so no throughput is reported.</summary>
        public bool Segmented { get; set; }
    }

    private BufferingQualityController Buffering => _bufferingController ??= new(_bufferingClock)
    {
        Output = OnBufferingOutput,
        MeasuredThroughputKbps = MeasuredPlanThroughput,
    };

    private bool BufferingPillVisible => BufferingPill.Visibility == Visibility.Visible;

    /// <summary>The playing plan's media rate so far, or null (HLS, a network change, or too little to measure).</summary>
    private int? MeasuredPlanThroughput() =>
        _bufferingController?.CurrentPlanId is { } id && _bufferingPlans.TryGetValue(id, out var record)
            && !record.Segmented && record.NetworkChanges == ConnectionSpeedService.NetworkChanges ? _planThroughput.Kbps : null;

    /// <summary>
    /// A replan the player sends for its own reasons (track change, seek reanchor, failure recovery): the playing plan's
    /// quality and, after a lowering, its kept estimate (PlaybackReplan.WithClientReplan).
    /// </summary>
    private BufferingQualityReplan? ClientBufferingReplan(string operation) => _bufferingController?.ClientReplan(operation);

    /// <summary>
    /// A plan became the playing plan: call after <c>attempt.Plan</c>, <c>PlanAttemptId</c> and the session are set,
    /// before it mounts. <paramref name="controllerInitiated"/> is true only for the controller's own quality_change.
    /// </summary>
    private void AdoptBufferingPlan(Attempt attempt, bool controllerInitiated, int? requestedEstimateKbps)
    {
        try
        {
#if DEBUG_SERVER
            if (attempt.DebugUrl is not null) return;
#endif
            if (attempt.Plan is not { } plan || attempt.Start is not { } start || attempt.Services is not { } services) return;
            var controller = Buffering;
            // The outgoing source is gone already: report its reload (or pause) to the plan it belonged to.
            UpdateBufferingSignals(attempt);
            controller.Mode = attempt.Client.Settings.BufferingQuality.Value;
            // PlaybackClient drops source, observed throughput and learned_limit unless the attempt negotiated
            // buffering_quality_v1, so the controller always fills them in.
            controller.SendsBufferingQualityFields = true;
            var id = (++_bufferingAdoptions).ToString(CultureInfo.InvariantCulture);
            _bufferingPlans[id] = new(PlanSummaryContext.From(start.PlaybackAttemptId, attempt.SessionId, plan, attempt.PlanAttemptId,
                attempt.ServerFeatures), attempt.Client, services.Installation.InstallationId, ConnectionSpeedService.NetworkChanges);
            // The outgoing plan's summary goes out inside this call, measured by the meter it played under.
            controller.PlanAdopted(BufferingQualityPlans.From(plan, id, start.QualityPreference, attempt.ServerFeatures, requestedEstimateKbps),
                controllerInitiated);
            _planThroughput.Reset();
            _mediaSilence.Reset(Environment.TickCount64);
            _bufferingSignals.PlanAdopted();
            _bufferingEnded = false;
            UpdateBufferingSignals(attempt);
            UpdateBufferingUi();
        }
        catch (Exception ex) { LogFailure("buffering adopt", ex); }
    }

    /// <summary>The playing plan stopped with no successor (end of media, error, leaving): its summary goes out.</summary>
    private void EndBufferingPlan()
    {
        try
        {
            _bufferingController?.PlanEnded();
            UpdateBufferingUi();
        }
        catch (Exception ex) { LogFailure("buffering end", ex); }
    }

    /// <summary>MediaEnded: the end of the media is no stall, and the plan's summary goes out.</summary>
    private void BufferingMediaEnded()
    {
        _bufferingEnded = true;
        if (_attempt is { } attempt) UpdateBufferingSignals(attempt);
        EndBufferingPlan();
    }

    /// <summary>OnTick: measure, report the backend's state, fire the controller's timers, draw the pill and notice.</summary>
    private void UpdateBufferingQuality()
    {
        try
        {
            if (_bufferingController is not { } controller) return;
            // Options, a track menu, Up Next or the error screen covers the pill (TvPlayerScreen.kt hudOpen || showNextUp).
            controller.SetCompetingPillVisible(OptionsOpen || TrackMenuOpen || UpNextVisible || ErrorPanel.Visibility == Visibility.Visible);
            if (_active && !_leaving && !_suspended && _attempt is { } attempt) UpdateBufferingSignals(attempt);
            _bufferingClock.Fire();
            UpdateBufferingUi();
        }
        catch (Exception ex) { LogFailure("buffering tick", ex); }
    }

    /// <summary>Hands the backend's state to the controller (also on every state change, from UpdatePlaybackState).</summary>
    private void UpdateBufferingSignals(Attempt attempt)
    {
        if (_bufferingController is not { CurrentPlanId: { } planId } controller) return;
        var now = Environment.TickCount64;
        var media = attempt.Media;
        SampleBufferingBytes(media, planId, now);
        var failed = media?.FailureQueued == true;
        var opened = !failed && media is { Opened: true, Player: not null };
        var state = opened ? media!.Player!.PlaybackSession.PlaybackState : MediaPlaybackState.None;
        // The viewer's intent: the player's pause while open, the remount's own while it loads, the last one between mounts.
        // A queued failure paused the player: the failure path owns that wait.
        var wantsToPlay = !failed && (opened ? state != MediaPlaybackState.Paused : media is not null ? !media.StartPaused : _bufferingWantsToPlay);
        if (opened) _bufferingWantsToPlay = wantsToPlay;
        var snapshot = new BufferingSnapshot
        {
            HasMount = opened,
            WantsToPlay = wantsToPlay,
            Playing = opened && media!.Started && state == MediaPlaybackState.Playing,
            SeekInFlight = _skipProcessing || _seeking && !_remounting,
            ReachedEnd = _bufferingEnded,
            UpstreamSilent = _mediaSilence.Silent(now, media?.Transport.IsHls == true),
        };
        BufferingSignals.Apply(controller, _bufferingSignals.Update(snapshot));
    }

    /// <summary>The mount's transport counters: the plan's throughput, and whether media data is still arriving.</summary>
    private void SampleBufferingBytes(Mount? media, string planId, long now)
    {
        if (media is null) return;
        try
        {
            object source;
            long bytes;
            if (media.Stream is { } stream) (source, bytes) = (stream, stream.DownloadedBytes);
            else if (media.Progressive is { } progressive) (source, bytes) = (progressive, progressive.DownloadedBytes);
            else return;
            // Wall-clock windows, not the transports' DownloadTime: 1 MiB blocks read at burst rate (PlanThroughput).
            _planThroughput.Observe(source, bytes, now);
            if (_mediaSilence.Observe(source, bytes, now)) _bufferingSignals.NoteMediaDataArrived();
            if (media.Transport.IsHls && _bufferingPlans.TryGetValue(planId, out var record)) record.Segmented = true;
        }
        catch (Exception ex) { LogFailure("buffering bytes", ex); }
    }

    private void OnBufferingOutput(BufferingQualityOutput output)
    {
        try
        {
            switch (output)
            {
                case BufferingReplanOutput replan:
                    _ = RunBufferingReplanAsync(replan.Request);
                    break;
                case BufferingPillShownOutput shown:
                    Log.Info("player: buffering pill " + (shown.Kind == BufferingPillKind.Prompt ? "prompt" : "undo"));
                    UpdateBufferingUi();
                    // TvBufferingPillCard: it takes focus once as it appears, unless a scrub owns it.
                    if (!_scrub.Active) FocusBufferingPill();
                    break;
                case BufferingNoticeOutput notice:
                    Log.Info("player: buffering notice " + (notice.Notice == BufferingQualityNotice.Lowered ? "lowered" : "cant_lower"));
                    _bufferingNotice = notice.Notice;
                    _bufferingNoticeLeft = BufferingQualityController.NoticeMs;
                    _bufferingNoticeShownAt = null;
                    break;
                case BufferingSummaryOutput summary:
                    SendPlanSummary(summary.Summary);
                    break;
            }
        }
        catch (Exception ex) { LogFailure("buffering output", ex); }
    }

    /// <summary>
    /// The controller's quality_change: Lower automatically / Lower quality with the step-down estimate, or Restore
    /// quality with learned_limit clear. Like a track change it remounts the same session at the current position; a
    /// refused or failed replan keeps the current playback. A lowering never queues behind another pipeline change
    /// (the spec: it fails and rearms); Restore quality waits its turn.
    /// </summary>
    private async Task RunBufferingReplanAsync(BufferingQualityReplan change)
    {
        var restore = change.IsRestore;
        // A lowering that answers a start budget that ran out waits its turn too: the open it replaces may still hold the
        // lifecycle while it stops (PlayerPage.StartBudget.cs), and refusing it would fail the start at the same quality.
        var heldAttempt = _attempt is { HeldStartDeadline: not null } held ? held : null;
        if (restore || heldAttempt is not null) await _lifecycle.WaitAsync();
        else if (!_lifecycle.Wait(0))
        {
            Log.Info("player: buffering replan busy");
            Buffering.ReplanRefused();
            return;
        }
        var adopted = false;
        var remounted = false;
        _remounting = true;
        Attempt? current = null;
        try
        {
            if (_attempt is not { Media: { FailureQueued: false } media, Plan: { } plan, Start: { } start } attempt
                || !IsCurrent(attempt, media) || attempt.SessionId is null)
            {
                Buffering.ReplanRefused();
                await RecoverHeldStartDeadlineAsync(heldAttempt);
                return;
            }
            current = attempt;
            var paused = media is { Opened: true, Player: { } player }
                ? player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused : media.StartPaused;
            var position = SourcePosition(attempt);
            var request = PlaybackReplan.ForQualityChange(start, plan, attempt.PlanAttemptId, attempt.AttemptedPlanKeys,
                attempt.Replanned ? 2 : 1, position, change);
            Log.Info("player: buffering replan " + (restore ? "restore" : "lower")
                + " estimate=" + (change.BandwidthEstimateKbps?.ToString(CultureInfo.InvariantCulture) ?? "-")
                + " observed=" + (change.ObservedThroughputKbps?.ToString(CultureInfo.InvariantCulture) ?? "-"));
            var decision = await SendBufferingReplanAsync(attempt, request);
            // Outcome is a server enum code; never log the plan or its URL.
            Log.Info($"player: buffering replan outcome={decision.Outcome}");
            if (!IsCurrent(attempt, media) || media.FailureQueued || !PlaybackMessages.ValidatePlayable(decision).IsPlayable
                || decision.SessionId != attempt.SessionId)
            {
                Log.Info("player: buffering replan refused; playback continues unchanged");
                Buffering.ReplanFailed();
                await RecoverHeldStartDeadlineAsync(heldAttempt);
                return;
            }
            // As a track change: position sampling and Up Next treat the remount like a seek.
            _seeking = remounted = true;
            Pause(attempt);
            DisposeSubtitles();
            DisposeMount(attempt);
            attempt.Plan = decision.PlaybackPlan!;
            attempt.ServerFeatures = decision.ServerFeatures ?? [];
            attempt.Start = start with { QualityPreference = change.QualityPreference };
            attempt.PlanAttemptId = Guid.NewGuid().ToString();
            attempt.Position = _sourcePosition = PlaybackProgressive.MountSourceStart(attempt.Plan, position);
            AdoptBufferingPlan(attempt, controllerInitiated: true, request.BandwidthEstimateKbps);
            adopted = true;
            // The lowering is adopted: the mount that ran out of its start budget is gone with its held failure.
            attempt.HeldStartDeadline = null;
            _state = "loading";
            SetLoading(true);
            var timeline = PlaybackTimeline.From(attempt.Plan.Timeline);
            await MountPlanAsync(attempt, PlaybackProgressive.MountPlayerStart(attempt.Plan,
                timeline.PlayerForSource(position) ?? timeline.PlayerStartSeconds), paused);
            await OpenedReplacementAsync(attempt);
            UpdateTrackValues();
        }
        catch (OperationCanceledException) when (current?.Cancellation.IsCancellationRequested != false)
        {
            if (!adopted) _bufferingController?.ReplanFailed();
        }
        catch (Exception ex)
        {
            // The server ended the session: leave with its notice and start nothing (PlayerPage.ServerEnd.cs).
            if (current is not null && LeftForServerEnd(current, ex)) return;
            LogFailure("buffering replan", ex);
            if (!adopted)
            {
                _bufferingController?.ReplanFailed();
                if (_active && !_leaving && !_suspended) await RecoverHeldStartDeadlineAsync(heldAttempt);
            }
            // The old mount is gone: the ordinary single failure replan decides, as for a start that cannot open.
            else if (current is not null && _active && !_leaving && !_suspended)
                await RecoverCoreAsync(current, ex.HResult, isDecoder: false, IsNetworkFailure(ex));
        }
        finally
        {
            if (remounted) _seeking = false;
            _remounting = false;
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Sends the replan, resending it once with the same body (and replan_request_id, so the server replays its decision)
    /// when it got no response.
    /// </summary>
    private static async Task<Bale.Core.Api.Models.PlaybackDecision> SendBufferingReplanAsync(Attempt attempt, PlaybackReplanRequestV3 request)
    {
        for (var tries = 0; ; tries++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                return await attempt.Client.Playback.ReplanAsync(attempt.SessionId!, request, attempt.Services!.Installation, deadline.Token);
            }
            catch (Exception ex) when (tries == 0 && !attempt.Cancellation.IsCancellationRequested
                && (IsNetworkFailure(ex) || ex is OperationCanceledException))
            {
                Log.Info("player: buffering replan got no response; resending");
            }
        }
    }

    private void SendPlanSummary(BufferingPlanSummary summary)
    {
        if (!_bufferingPlans.Remove(summary.PlanId, out var record)) return;
        // throughput_kbps only from one network and outside HLS (the meter belongs to the plan that just ended).
        var throughput = !record.Segmented && record.NetworkChanges == ConnectionSpeedService.NetworkChanges ? _planThroughput.Kbps : null;
        summary = summary with { ThroughputKbps = throughput };
        var routeEvent = PlaybackRouteEvents.ForPlanSummary(summary, record.Context, record.InstallationId, Guid.NewGuid().ToString());
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"player: plan summary played={summary.PlayedMs} stalls={summary.StallCount} stall_ms={summary.StallTotalMs} longest={summary.StallLongestMs} action={BufferingQualityWire.Action(summary.Action)} throughput={throughput?.ToString(CultureInfo.InvariantCulture) ?? "-"} sent={(routeEvent is null ? "no" : "yes")}"));
        if (routeEvent is not null) _ = PostPlanSummaryAsync(record.Client, routeEvent);
    }

    /// <summary>Best effort, never awaited by teardown: a summary must not hold up Back, an error or suspension.</summary>
    private static async Task PostPlanSummaryAsync(SiloClient client, PlaybackRouteEventV2 routeEvent)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!await client.Playback.ReportRouteEventAsync(routeEvent, deadline.Token)) Log.Info("player: plan summary not accepted");
        }
        catch (Exception ex) { LogFailure("plan summary", ex); }
    }

    // ---- Pill and notice ----

    private void UpdateBufferingUi()
    {
        var now = Environment.TickCount64;
        UpdateBufferingNotice(now);
        UpdateBufferingPill(now);
    }

    /// <summary>
    /// The display-only notice shows for NOTICE_MS in all (BufferingQualityNoticeSlot.kt): it waits while the version
    /// notice holds the slot or something covers the player, and then shows for the time it has left.
    /// </summary>
    private void UpdateBufferingNotice(long now)
    {
        var free = _bufferingNotice is not null && _active && !_leaving && VersionNotice.Visibility != Visibility.Visible
            && ErrorPanel.Visibility != Visibility.Visible && !OptionsOpen && !UpNextVisible;
        if (_bufferingNoticeShownAt is { } shownAt)
        {
            var left = _bufferingNoticeLeft - (now - shownAt);
            if (left <= 0) _bufferingNotice = null;
            if (left <= 0 || !free)
            {
                _bufferingNoticeLeft = Math.Max(0, left);
                _bufferingNoticeShownAt = null;
            }
        }
        else if (free && _bufferingNoticeLeft > 0) _bufferingNoticeShownAt = now;
        var visible = _bufferingNotice is not null && _bufferingNoticeShownAt is not null;
        if (visible)
            BufferingNoticeText.Text = _bufferingNotice == BufferingQualityNotice.Lowered
                ? BufferingQualityController.LoweredNotice : BufferingQualityController.CantLowerNotice;
        BufferingNotice.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBufferingPill(long now)
    {
        var pill = _bufferingController?.Pill;
        if (pill is null || !_active || _leaving)
        {
            HideBufferingPill();
            return;
        }
        if (_bufferingPillShown != pill.Kind)
        {
            var prompt = pill.Kind == BufferingPillKind.Prompt;
            // A caption and its action, or two actions, are separate elements (the spec's presentation rules).
            BufferingPillTitle.Text = prompt ? BufferingQualityController.PromptTitle : string.Empty;
            BufferingPillTitle.Visibility = prompt ? Visibility.Visible : Visibility.Collapsed;
            BufferingPillCaption.Text = prompt ? string.Empty : BufferingQualityController.UndoCaption;
            BufferingPillCaption.Visibility = prompt ? Visibility.Collapsed : Visibility.Visible;
            BufferingPrimaryButton.Content = prompt ? BufferingQualityController.LowerAction : BufferingQualityController.UndoAction;
            BufferingSecondaryButton.Content = BufferingQualityController.KeepAction;
            BufferingSecondaryButton.Visibility = prompt ? Visibility.Visible : Visibility.Collapsed;
            _bufferingPillShown = pill.Kind;
        }
        BufferingPill.Visibility = Visibility.Visible;
        // Under a notice at the same anchor, 12dp below it (TvPlayerNoticeOverlay.kt).
        var notice = VersionNotice.Visibility == Visibility.Visible ? VersionNotice
            : BufferingNotice.Visibility == Visibility.Visible ? BufferingNotice : null;
        var top = 32 + (notice is null ? 0 : notice.ActualHeight + 12);
        if (BufferingPill.Margin.Top != top) BufferingPill.Margin = new Thickness(32, top, 32, 0);
        // The countdown fill: the time left of the 15 s timer, frozen while paused.
        var left = pill.DeadlineMs is { } deadline ? Math.Max(0, deadline - now) : pill.RemainingMs;
        BufferingPillFill.Width = Math.Max(0, BufferingPill.ActualWidth * Math.Clamp(left / (double)BufferingQualityController.PromptMs, 0, 1));
    }

    private void HideBufferingPill()
    {
        _bufferingPillShown = null;
        if (!BufferingPillVisible) return;
        var hadFocus = BufferingPillHasFocus();
        BufferingPill.Visibility = Visibility.Collapsed;
        if (!hadFocus) return;
        // Focus goes back where the player keeps it: the controls if they are up, else the page.
        if (_controls.Visible) PlayPauseButton.Focus(FocusState.Keyboard);
        else Focus(FocusState.Programmatic);
    }

    private bool BufferingPillHasFocus()
    {
        var focused = FocusManager.GetFocusedElement();
        return ReferenceEquals(focused, BufferingPrimaryButton) || ReferenceEquals(focused, BufferingSecondaryButton);
    }

    /// <summary>Focus on the pill's primary action; false when no pill is showing.</summary>
    private bool FocusBufferingPill()
    {
        if (!BufferingPillVisible) return false;
        FocusRetry.Until(BufferingPrimaryButton, () => !BufferingPillVisible || BufferingPrimaryButton.Focus(FocusState.Keyboard));
        return true;
    }

    /// <summary>Back on a visible pill: the prompt answers Keep quality, the undo pill only hides.</summary>
    private bool BackClosesBufferingPill()
    {
        if (!BufferingPillVisible || _bufferingController is not { } controller || !controller.Back()) return false;
        UpdateBufferingUi();
        return true;
    }

    private void OnBufferingPrimaryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _bufferingController?.SelectPrimary();
            UpdateBufferingUi();
        }
        catch (Exception ex) { LogFailure("buffering primary", ex); }
    }

    private void OnBufferingSecondaryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _bufferingController?.SelectKeep();
            UpdateBufferingUi();
        }
        catch (Exception ex) { LogFailure("buffering secondary", ex); }
    }

    /// <summary>Left/Right move between the actions and never leave the card; Up stays; Down brings up the controls.</summary>
    private void OnBufferingPillKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            switch (e.Key)
            {
                case VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft:
                    e.Handled = true;
                    BufferingPrimaryButton.Focus(FocusState.Keyboard);
                    break;
                case VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight:
                    e.Handled = true;
                    if (BufferingSecondaryButton.Visibility == Visibility.Visible) BufferingSecondaryButton.Focus(FocusState.Keyboard);
                    break;
                case VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp:
                    e.Handled = true;
                    break;
                case VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown:
                    e.Handled = true;
                    ShowControls();
                    break;
            }
        }
        catch (Exception ex) { LogFailure("buffering pill input", ex); }
    }

#if DEBUG_SERVER
    /// <summary>The controller's state for <c>bale-debug.sh buffering</c>: mode, pill, kept estimate, the running plan's figures.</summary>
    internal string DebugBuffering
    {
        get
        {
            var controller = _bufferingController;
            var summary = controller?.RunningSummary;
            return string.Create(CultureInfo.InvariantCulture,
                $"Mode: {(controller is null ? "none" : Bale.Core.Settings.BufferingQualityModes.Wire(controller.Mode))}\n"
                + $"Plan: {controller?.CurrentPlanId ?? "none"} delivered={summary?.DeliveredKbps?.ToString(CultureInfo.InvariantCulture) ?? "-"} estimate={summary?.BandwidthEstimateKbps?.ToString(CultureInfo.InvariantCulture) ?? "-"}\n"
                + $"Played: {summary?.PlayedMs ?? 0} ms; stalls={summary?.StallCount ?? 0} total={summary?.StallTotalMs ?? 0} ms longest={summary?.StallLongestMs ?? 0} ms; action={(summary is null ? "none" : BufferingQualityWire.Action(summary.Action))}\n"
                + $"Throughput: {MeasuredPlanThroughput()?.ToString(CultureInfo.InvariantCulture) ?? "-"} kbps ({_planThroughput.Bytes} bytes)\n"
                + $"Kept estimate: {controller?.KeptEstimateKbps?.ToString(CultureInfo.InvariantCulture) ?? "-"}\n"
                + $"Pill: {(controller?.Pill is { } pill ? pill.Kind.ToString() : controller?.PillPending == true ? "hidden" : "none")}\n"
                + $"Plan summaries: {(_attempt?.ServerFeatures.Contains(PlaybackProtocol.PlanSummaryFeature) == true ? "advertised" : "not advertised")}\n");
        }
    }

    /// <summary>
    /// <c>bale-debug.sh buffering stall [ms]</c>: a network wait injected into the controller now, ended by a Playing
    /// after <paramref name="ms"/>, as if the backend had rebuffered. Detection is bypassed; the trigger, the replan, the
    /// pill and the summary are the real ones. 6000 ms or more is a long stall; two of 1000 ms+ within 120 s also trigger.
    /// </summary>
    internal (string Status, string Body) DebugBufferingStall(long ms)
    {
        if (_bufferingController is not { CurrentPlanId: not null } controller || _attempt is not { Media: { Started: true } })
            return ("409 Conflict", "no playing plan\n");
        Log.Info(string.Create(CultureInfo.InvariantCulture, $"player: debug buffering stall ms={ms}"));
        controller.Waiting();
        _ = _bufferingClock.Schedule(ms, () =>
        {
            controller.Playing();
            // Report the backend's real state again on the next update.
            _bufferingSignals.Invalidate();
        });
        UpdateBufferingUi();
        return ("200 OK", string.Create(CultureInfo.InvariantCulture, $"stall injected for {ms} ms\n") + DebugBuffering);
    }
#endif
}
