using Bale.Core.Playback;
using Bale.Core.Settings;

// The C# originals, before the Rust port (docs/rust-port.md), for differential tests only.
namespace Bale.Core.Tests.Reference;

/// <summary>
/// Lowers quality when playback keeps buffering: silo-server docs/design/2026-10-17-buffering-quality.md, its stall
/// tracker and trigger (Terms) and every table under "Behaviour", ported from silo-apple's
/// BufferingQualityController.swift (playback follows Apple; Android's BufferingQualityController.kt is the same spec).
/// The tests replay the server's conformance vectors (schemas/buffering-quality/v1/vectors.json) case for case.
/// The player feeds it plan adoptions, media events and the viewer's answers, and acts on <see cref="Output"/>:
/// replans to send, pills and notices to show, and <c>plan_summary</c> figures. It performs no I/O and is not
/// thread-safe: call it on the UI thread, which also polls the clock's timers.
/// </summary>
internal sealed class BufferingQualityController(IBufferingQualityClock clock)
{
    // Spec constants (vectors.json "constants").
    public const long StartupGraceMs = 10_000;
    public const long StallMinMs = 1_000;
    public const int StallsBeforeTrigger = 2;
    public const long StallWindowMs = 120_000;
    public const long LongStallMs = 6_000;
    public const int PromptSeconds = 15;
    public const long ReofferMinMs = 120_000;
    public const long NoLowerCooldownMs = 300_000;
    public const long RearmAfterUnadoptedMs = 60_000;
    public const long RendererWaitBufferMs = 1_000;
    /// <summary>STEP_DOWN_RATIO as the vectors state it; the estimate is the exact integer <c>delivered × 7 / 10</c>.</summary>
    public const double StepDownRatio = 0.7;
    public const int MinEstimateKbps = 100;
    /// <summary>Display-only notices show this long (the platform's notice style).</summary>
    public const long NoticeMs = 5_000;
    public const long PromptMs = PromptSeconds * 1000L;

    // The spec's copy (vectors.json "copy").
    public const string PromptTitle = "Playback keeps buffering";
    public const string LowerAction = "Lower quality";
    public const string KeepAction = "Keep quality";
    public const string UndoCaption = "Quality lowered to stop buffering";
    public const string UndoAction = "Restore quality";
    public const string LoweredNotice = "Quality lowered to stop buffering";
    public const string CantLowerNotice = "Can't lower quality for this video";

    public const string AutoQuality = "auto";
    public const string QualityChangeOperation = "quality_change";
    public const string ObservedRebufferSource = "observed_rebuffer";

    public BufferingQualityMode Mode { get; set; } = BufferingQualityModes.Default;

    /// <summary>The server advertises buffering_quality_v1; without it replans drop the source and learned_limit.</summary>
    public bool SendsBufferingQualityFields { get; set; }

    public Action<BufferingQualityOutput>? Output { get; set; }

    /// <summary>What the playing plan's media has measurably arrived at so far, in kbps, or null.</summary>
    public Func<int?> MeasuredThroughputKbps { get; set; } = () => null;

    /// <summary>The actionable pill on screen, or null.</summary>
    public BufferingPill? Pill { get; private set; }

    /// <summary>
    /// The step-down estimate of the latest Auto lowering, from its replan until Restore quality, a quality change
    /// the controller did not send, or a new attempt. Later Auto replans repeat it without a source.
    /// </summary>
    public int? KeptEstimateKbps { get; private set; }

    /// <summary>A pill is waiting, visible or hidden behind another surface.</summary>
    public bool PillPending => _presented is not null;

    private PlanState? _current;
    private long? _playingSince;
    private bool _isPaused;
    private bool _rendererWaiting;
    private bool _transportTrouble;
    private long? _transportTroubleEndedAt;
    private bool _competingPillVisible;
    private AttemptStage _attemptStage = AttemptStage.New;
    private bool _answered;
    private bool _noLowerRungNoticeShown;
    private long? _lastOfferAt;
    private PendingReplan? _pendingReplan;
    private bool _pickPending;
    private PresentedPill? _presented;
    private IDisposable? _longStallTimer, _suppressionTimer, _rearmTimer, _pillTimer;

    private long NowMs => clock.ElapsedMs;

    // ---- Plans ----

    /// <summary>
    /// A plan became the playing plan. It ends the outgoing plan (emitting its summary), takes down any pill and
    /// starts fresh history. A plan that answers the controller's pending replan settles it. Pass
    /// <paramref name="controllerInitiated"/> false for a plan answering a replan the controller did not send.
    /// </summary>
    public void PlanAdopted(BufferingQualityPlan plan, bool controllerInitiated = true)
    {
        var now = NowMs;
        // During the start, a stall in progress carries over as the new plan's first wait.
        var wasStalled = _current?.Stall is not null;
        var startGrace = _attemptStage == AttemptStage.Adopted ? _current?.GraceEndsAt : null;
        EndPlan(now);
        var pending = controllerInitiated ? _pendingReplan : null;
        _pendingReplan = null;
        var state = new PlanState(plan) { GraceEndsAt = startGrace };
        // A plan adopted while playing has its first frame now, and its grace counts from there.
        if (_playingSince is not null)
        {
            _playingSince = now;
            state.GraceEndsAt = now + StartupGraceMs;
        }
        else state.Reloading = _attemptStage == AttemptStage.Played;
        if (_attemptStage == AttemptStage.New) _attemptStage = AttemptStage.Adopted;
        _current = state;
        if (wasStalled && !state.Reloading) BeginStall(now);
        if (pending is not null) Settle(pending, now);
    }

    /// <summary>The playing plan's summary as if it ended now, without ending it.</summary>
    public BufferingPlanSummary? RunningSummary => _current is { } state ? Summary(state, NowMs) : null;

    /// <summary>The adoption id of the playing plan, or null.</summary>
    public string? CurrentPlanId => _current?.Plan.Id;

    /// <summary>The plan stopped or failed with no successor: emits its summary.</summary>
    public void PlanEnded()
    {
        EndPlan(NowMs);
        _pendingReplan = null;
        _playingSince = null;
    }

    /// <summary>A new playback attempt of the same title: the kept estimate goes, its first plan starts afresh.</summary>
    public void NewAttempt()
    {
        _attemptStage = AttemptStage.New;
        KeptEstimateKbps = null;
        if (_pendingReplan is not null) _pendingReplan.PriorKeptEstimateKbps = null;
    }

    // ---- Media events ----

    public void Playing()
    {
        var now = NowMs;
        _isPaused = false;
        _rendererWaiting = false;
        if (_current is { } plan)
        {
            // The plan ran again: a reload that ended its stall did not end it.
            plan.StallEndedByReload = false;
            plan.Reloading = false;
        }
        if (_playingSince is null)
        {
            _playingSince = now;
            if (_current is { GraceEndsAt: null } fresh) fresh.GraceEndsAt = now + StartupGraceMs;
        }
        // The start wait the first frame ends counts only on evidence.
        var counts = _attemptStage == AttemptStage.Played || _current is { } state && ArrivesSlowly(state.Plan);
        if (_current is not null) _attemptStage = AttemptStage.Played;
        EndStall(now, resumed: counts);
        RefreshPill();
        Evaluate(now);
    }

    /// <summary>
    /// The player waits. A network wait is a stall, except while a replacement of a plan that played loads; any
    /// other cause ends a stall in progress without counting it. <paramref name="startedAgoMs"/> back-dates a wait
    /// noticed late, never past the start of the current playing state.
    /// </summary>
    public void Waiting(BufferingWaitCause cause = BufferingWaitCause.Network, long startedAgoMs = 0)
    {
        var now = NowMs;
        _rendererWaiting = cause == BufferingWaitCause.Renderer;
        var began = _playingSince is { } since ? Math.Max(since, now - Math.Max(0, startedAgoMs)) : now;
        LeavePlaying(began);
        if (cause != BufferingWaitCause.Network)
        {
            EndStall(now, resumed: false, byReload: cause == BufferingWaitCause.Reload);
            if (cause == BufferingWaitCause.Renderer)
            {
                _isPaused = false;
                RefreshPill();
            }
            return;
        }
        _isPaused = false;
        if (_current is { Stall: null, Reloading: false }) BeginStall(began);
        RefreshPill();
    }

    public void Paused()
    {
        var now = NowMs;
        _rendererWaiting = false;
        LeavePlaying(now);
        EndStall(now, resumed: false);
        _isPaused = true;
        RefreshPill();
    }

    public void Seeked() => EndStall(NowMs, resumed: false);

    /// <summary>
    /// An outage: media requests failing or silent, or the device offline. A stall that runs into it drops its time
    /// so far, and stall time during it never counts; a stall still in progress when it ends is timed from then.
    /// </summary>
    public void TransportTroubleChanged(bool trouble)
    {
        if (trouble == _transportTrouble) return;
        _transportTrouble = trouble;
        var now = NowMs;
        if (!trouble) _transportTroubleEndedAt = now;
        if (_current is not { Stall: { } stall } state) return;
        if (trouble)
        {
            stall.MeasuredFrom = null;
            stall.TimedFrom = null;
            CancelStallTimers();
        }
        else
        {
            var from = Math.Max(now, state.GraceEndsAt ?? now);
            stall.TimedFrom = from;
            stall.MeasuredFrom = from;
            ScheduleLongStallTimer();
        }
    }

    /// <summary>The controller's replan failed. The plan keeps playing and is watched again at the rearm.</summary>
    public void ReplanFailed() => _pendingReplan = null;

    /// <summary>The player could not send the controller's replan at all: it fails like <see cref="ReplanFailed"/>, and keeps no estimate.</summary>
    public void ReplanRefused()
    {
        if (_pendingReplan is { } pending) KeptEstimateKbps = pending.PriorKeptEstimateKbps;
        _pendingReplan = null;
    }

    /// <summary>
    /// The plan failed before its first frame because the player's own start deadline passed. On Auto in
    /// <c>always</c> with evidence that its media arrived below its delivered bitrate, it lowers as a long stall
    /// would and returns true; false leaves the failure to the player's usual recovery.
    /// </summary>
    public bool StartDeadlinePassed()
    {
        if (Mode != BufferingQualityMode.Always || _answered || _transportTrouble
            || _transportTroubleEndedAt is { } ended && NowMs - ended < LongStallMs
            || _rendererWaiting || _pickPending || _rearmTimer is not null
            || _current is not { } state || !state.Plan.IsAuto || state.Plan.QualityReductionUnavailable
            || state.Plan.DeliveredKbps is not { } delivered || !ArrivesSlowly(state.Plan)
            || state.CooldownUntil is not null || LowerRung(state.Plan) is null
            || StepDownEstimate(delivered) is not { } estimate) return false;
        WithdrawPill();
        SendReplan(BufferingAction.Lowered, AutoQuality, estimate, fromKbps: delivered,
            observedThroughputKbps: ObservedThroughputKbps(state.Plan));
        return true;
    }

    // ---- Pill answers ----

    /// <summary>Lower quality on the prompt.</summary>
    public void SelectLower()
    {
        if (Pill?.Kind != BufferingPillKind.Prompt || _current?.Plan is not { } plan) return;
        WithdrawPill();
        if (LowerRung(plan) is not { } rung)
        {
            CantLower();
            return;
        }
        if (plan.IsAuto)
        {
            if (StepDownEstimate(plan.DeliveredKbps) is not { } estimate)
            {
                CantLower();
                return;
            }
            SendReplan(BufferingAction.Accepted, AutoQuality, estimate, fromKbps: plan.DeliveredKbps);
        }
        // This playback only: the saved preference does not change.
        else SendReplan(BufferingAction.Accepted, rung.Label, fromKbps: plan.DeliveredKbps ?? ActiveBitrate(plan));
    }

    /// <summary>Keep quality on the prompt: answers the title at this quality.</summary>
    public void SelectKeep()
    {
        if (Pill?.Kind != BufferingPillKind.Prompt) return;
        WithdrawPill();
        _answered = true;
        if (_current is { } state) state.Action = BufferingAction.Kept;
    }

    /// <summary>Restore quality on the undo pill: answers the title and clears the learned limit.</summary>
    public void SelectRestore()
    {
        if (Pill?.Kind != BufferingPillKind.Undo || _current is null) return;
        WithdrawPill();
        _answered = true;
        KeptEstimateKbps = null;
        SendReplan(BufferingAction.Restored, AutoQuality, learnedLimit: PlaybackProtocol.LearnedLimitClear);
    }

    /// <summary>
    /// Back. On the prompt it answers like Keep; on the undo pill it only hides it. True when a visible pill consumed
    /// the press, so a second Back behaves normally.
    /// </summary>
    public bool Back()
    {
        switch (Pill?.Kind)
        {
            case BufferingPillKind.Prompt:
                SelectKeep();
                return true;
            case BufferingPillKind.Undo:
                WithdrawPill();
                EvaluateLongStall();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Select at the player root: the visible pill's primary action.</summary>
    public void SelectPrimary()
    {
        switch (Pill?.Kind)
        {
            case BufferingPillKind.Prompt: SelectLower(); break;
            case BufferingPillKind.Undo: SelectRestore(); break;
        }
    }

    // ---- Host inputs ----

    /// <summary>A quality change the controller did not send (the viewer's pick, as its plan is adopted).</summary>
    public void QualityChanged()
    {
        _pickPending = false;
        KeptEstimateKbps = null;
        _answered = false;
        _lastOfferAt = null;
        _pendingReplan = null;
        Cancel(ref _rearmTimer);
        WithdrawPill();
        ClearHistory(NowMs);
    }

    /// <summary>The viewer's quality pick was sent: pills hide, nothing prompts or lowers while it is pending.</summary>
    public void PickSent()
    {
        _pickPending = true;
        WithdrawPill();
    }

    /// <summary>The pick never became a plan: nothing else changes, and a long stall in progress is evaluated at once.</summary>
    public void PickRefused()
    {
        if (!_pickPending) return;
        _pickPending = false;
        EvaluateLongStall();
    }

    /// <summary>
    /// A replan the player sends for its own reasons (track change, seek reanchor, failure recovery): the adopted
    /// plan's quality and, on Auto, the kept estimate without a source. Null before a plan is adopted.
    /// </summary>
    public BufferingQualityReplan? ClientReplan(string operation) => _current?.Plan is { } plan
        ? new(operation, plan.Quality) { BandwidthEstimateKbps = plan.IsAuto ? KeptEstimateKbps : null }
        : null;

    /// <summary>
    /// Up Next, Options or a track menu covers the pill, or the player cannot show pills: one that would appear waits,
    /// one already up hides with its timer frozen; either shows again when this clears, if the plan has not changed.
    /// </summary>
    public void SetCompetingPillVisible(bool visible)
    {
        if (visible == _competingPillVisible) return;
        _competingPillVisible = visible;
        RefreshPill();
    }

    // ---- Trigger ----

    private void Evaluate(long now)
    {
        if (_isPaused || _transportTrouble || _current is not { } state) return;
        state.EndedStalls.RemoveAll(stall => now - stall.At >= StallWindowMs);
        if (!Triggered(state, now)) return;
        // Before anything of the attempt played, the wait is the network's only on evidence.
        if (_attemptStage != AttemptStage.Played && !ArrivesSlowly(state.Plan)) return;
        var evidenceKbps = _attemptStage == AttemptStage.Played ? null : ObservedThroughputKbps(state.Plan);

        // A suppressed trigger leaves history intact for a later evaluation.
        if (_answered || _pickPending || _rearmTimer is not null || _presented is not null) return;
        if (state.CooldownUntil is { } until && now < until)
        {
            WatchStall(until);
            return;
        }
        if (Mode == BufferingQualityMode.Never)
        {
            state.Action = BufferingAction.Ignored;
            return;
        }
        var plan = state.Plan;
        if (LowerRung(plan) is null)
        {
            CantLower();
            return;
        }
        if (plan.IsAuto)
        {
            if (StepDownEstimate(plan.DeliveredKbps) is not { } estimate)
            {
                CantLower();
                return;
            }
            if (Mode == BufferingQualityMode.Always)
            {
                SendReplan(BufferingAction.Lowered, AutoQuality, estimate, fromKbps: plan.DeliveredKbps,
                    observedThroughputKbps: evidenceKbps);
                return;
            }
        }
        if (_lastOfferAt is { } offered && now - offered < ReofferMinMs)
        {
            WatchStall(offered + ReofferMinMs);
            return;
        }
        Present(BufferingPillKind.Prompt);
    }

    private static bool Triggered(PlanState state, long now)
    {
        if (state.Stall?.TimedFrom is { } from && now - from >= LongStallMs) return true;
        var recent = state.EndedStalls.Where(stall => now - stall.At < StallWindowMs).ToList();
        return recent.Any(stall => stall.Long) || recent.Count >= StallsBeforeTrigger;
    }

    /// <summary>Keeps watching a long stall a time-based suppression held back: evaluates again when it expires.</summary>
    private void WatchStall(long deadline)
    {
        Cancel(ref _suppressionTimer);
        if (_current?.Stall is null) return;
        _suppressionTimer = ScheduleAt(deadline, () =>
        {
            _suppressionTimer = null;
            EvaluateLongStall();
        });
    }

    /// <summary>Evaluates only if a stall in progress has reached LONG_STALL_MS.</summary>
    private void EvaluateLongStall()
    {
        var now = NowMs;
        if (_current?.Stall?.TimedFrom is { } from && now - from >= LongStallMs) Evaluate(now);
    }

    /// <summary>Evidence the plan's media arrives too slowly: its measured throughput below its delivered bitrate.</summary>
    private bool ArrivesSlowly(BufferingQualityPlan plan) =>
        plan.DeliveredKbps is { } delivered && MeasuredThroughputKbps() is { } measured && measured < delivered;

    /// <summary>The figure a lowering sends as observed_throughput_kbps: the measured rate when below the delivered bitrate.</summary>
    private int? ObservedThroughputKbps(BufferingQualityPlan plan) =>
        plan.DeliveredKbps is { } delivered && MeasuredThroughputKbps() is { } measured && measured < delivered ? measured : null;

    /// <summary>No lower rung: the display-only notice, once per title.</summary>
    private void CantLower()
    {
        if (_current is { } state) state.Action = BufferingAction.CantLower;
        if (_noLowerRungNoticeShown) return;
        _noLowerRungNoticeShown = true;
        Emit(new BufferingNoticeOutput(BufferingQualityNotice.CantLower));
    }

    /// <summary>
    /// The spec's lower rung: the highest real rung below the lower of the named rung's bitrate and the delivered
    /// bitrate, or below the delivered bitrate when the plan names no rung. Equal bitrates keep the earlier entry.
    /// </summary>
    public static BufferingQualityRung? LowerRung(BufferingQualityPlan plan)
    {
        if (ActiveBitrate(plan) is not { } ceiling) return null;
        BufferingQualityRung? best = null;
        foreach (var rung in plan.AvailableQualities)
            if (rung.Label != AutoQuality && rung.BitrateKbps > 0 && rung.BitrateKbps < ceiling
                && (best is null || rung.BitrateKbps > best.BitrateKbps)) best = rung;
        return best;
    }

    /// <summary><c>floor(delivered × 7 / 10)</c> in integer arithmetic, or null below MIN_ESTIMATE_KBPS (22120 → 15484).</summary>
    public static int? StepDownEstimate(int? deliveredKbps)
    {
        if (deliveredKbps is not (> 0 and var delivered)) return null;
        var estimate = (int)((long)delivered * 7 / 10);
        return estimate >= MinEstimateKbps ? estimate : null;
    }

    private static int? ActiveBitrate(BufferingQualityPlan plan)
    {
        var rung = plan.ActiveQuality is { } named
            ? plan.AvailableQualities.FirstOrDefault(entry => entry.Label == named && entry.BitrateKbps > 0)
            : null;
        if (rung is null) return plan.DeliveredKbps;
        // A cap that keeps the rung's resolution can deliver below its bitrate.
        return Math.Min(rung.BitrateKbps, plan.DeliveredKbps ?? rung.BitrateKbps);
    }

    // ---- Replans ----

    private void SendReplan(BufferingAction action, string qualityPreference, int? estimateKbps = null, string? learnedLimit = null,
        int? fromKbps = null, int? observedThroughputKbps = null)
    {
        var now = NowMs;
        if (_current is { } state) state.Action = action;
        var request = new BufferingQualityReplan(QualityChangeOperation, qualityPreference) { BandwidthEstimateKbps = estimateKbps };
        if (SendsBufferingQualityFields)
            request = request with
            {
                BandwidthEstimateSource = estimateKbps is null ? null : ObservedRebufferSource,
                LearnedLimit = learnedLimit,
                ObservedThroughputKbps = observedThroughputKbps,
            };
        // Only an Auto lowering sends an estimate; the next one replaces it.
        var priorKept = KeptEstimateKbps;
        if (estimateKbps is not null) KeptEstimateKbps = estimateKbps;
        // A Restore's plan settles nothing.
        _pendingReplan = action == BufferingAction.Restored ? null : new PendingReplan(action == BufferingAction.Lowered, fromKbps)
        {
            PriorKeptEstimateKbps = priorKept,
        };
        // Triggers on this plan wait for the adoption, or for the rearm if the replan fails or never arrives.
        Cancel(ref _suppressionTimer);
        Cancel(ref _rearmTimer);
        _rearmTimer = ScheduleAt(now + RearmAfterUnadoptedMs, Rearm);
        Emit(new BufferingReplanOutput(request));
    }

    private void Rearm()
    {
        _rearmTimer = null;
        _pendingReplan = null;
        ClearHistory(NowMs);
    }

    private void Settle(PendingReplan pending, long now)
    {
        if (_current is not { } state) return;
        var plan = state.Plan;
        bool lower;
        if (plan.QualityReductionUnavailable) lower = false;
        // A result with no delivered bitrate counts as lower (the vectors' delivered_kbps: 0).
        else if (pending.FromKbps is { } from) lower = (plan.DeliveredKbps ?? 0) < from;
        else return;
        // The viewer's pending pick replaces this plan, so its result shows nothing; its action and cooldown stand.
        if (lower)
        {
            if (_pickPending) return;
            if (pending.Automatic) Present(BufferingPillKind.Undo);
            else Emit(new BufferingNoticeOutput(BufferingQualityNotice.Lowered));
        }
        else
        {
            state.CooldownUntil = now + NoLowerCooldownMs;
            state.Action = BufferingAction.CantLower;
            if (_pickPending) return;
            // The viewer has now seen this copy for the title.
            _noLowerRungNoticeShown = true;
            Emit(new BufferingNoticeOutput(BufferingQualityNotice.CantLower));
        }
    }

    // ---- Stall tracker ----

    private void BeginStall(long now)
    {
        if (_current is not { } state) return;
        state.StallEndedByReload = false;
        // The plan's first wait, when it comes before a frame, starts the grace.
        var graceEnd = state.GraceEndsAt ?? now + StartupGraceMs;
        state.GraceEndsAt = graceEnd;
        var stall = new Stall();
        if (!_transportTrouble)
        {
            // A stall that begins in the grace counts from its end.
            var from = Math.Max(now, graceEnd);
            stall.TimedFrom = from;
            stall.MeasuredFrom = from;
        }
        state.Stall = stall;
        ScheduleLongStallTimer();
    }

    /// <summary>
    /// Ends a stall in progress. Only one playback resumed from, outside transport trouble, can be counted; its time
    /// feeds the summary either way. One a source reload ended counts in the summary if the plan ends before it runs again.
    /// </summary>
    private void EndStall(long now, bool resumed, bool byReload = false)
    {
        if (_current is not { Stall: { } stall } state) return;
        state.Stall = null;
        state.RecordStall(stall.Measured(now));
        if (stall.TimedFrom is { } from && !_transportTrouble && now - from >= StallMinMs)
        {
            if (resumed)
            {
                state.EndedStalls.Add(new EndedStall(now, now - from >= LongStallMs));
                state.StallCount++;
            }
            else if (byReload) state.StallEndedByReload = true;
        }
        CancelStallTimers();
    }

    /// <summary>Prompt timeout, rearm, or a quality change: history clears with the grace over; a stall in progress is retimed from now.</summary>
    private void ClearHistory(long now)
    {
        if (_current is not { } state) return;
        state.EndedStalls.Clear();
        state.GraceEndsAt = now;
        if (state.Stall is { TimedFrom: not null } stall) stall.TimedFrom = now;
        Cancel(ref _suppressionTimer);
        ScheduleLongStallTimer();
    }

    private void ScheduleLongStallTimer()
    {
        Cancel(ref _longStallTimer);
        if (_current?.Stall?.TimedFrom is not { } from) return;
        _longStallTimer = ScheduleAt(from + LongStallMs, () =>
        {
            _longStallTimer = null;
            LongStallTimerFired();
        });
    }

    private void LongStallTimerFired()
    {
        if (_current?.Stall?.TimedFrom is not { } from) return;
        // A timer can fire a little early against the clock; wait out the rest.
        if (NowMs - from < LongStallMs)
        {
            ScheduleLongStallTimer();
            return;
        }
        Evaluate(NowMs);
    }

    private void CancelStallTimers()
    {
        Cancel(ref _longStallTimer);
        Cancel(ref _suppressionTimer);
    }

    private void LeavePlaying(long now)
    {
        if (_playingSince is not { } since) return;
        if (_current is { } state) state.PlayedMs += now - since;
        _playingSince = null;
    }

    /// <summary>Closes the playing plan's books, takes down its pill and timers, and emits its summary.</summary>
    private void EndPlan(long now)
    {
        if (_current is not { } state) return;
        var summary = Summary(state, now);
        _current = null;
        CancelStallTimers();
        Cancel(ref _rearmTimer);
        WithdrawPill();
        Emit(new BufferingSummaryOutput(summary));
    }

    private BufferingPlanSummary Summary(PlanState state, long now)
    {
        var played = state.PlayedMs + (_playingSince is { } since ? now - since : 0);
        var count = state.StallCount;
        var total = state.StallTotalMs;
        var longest = state.StallLongestMs;
        if (state.Stall is { } stall)
        {
            // A stall still running when its plan ends ends with it, and counts if it reached STALL_MIN_MS.
            var measured = stall.Measured(now);
            if (measured > 0)
            {
                total += measured;
                longest = Math.Max(longest, measured);
            }
            if (!_transportTrouble && stall.TimedFrom is { } from && now - from >= StallMinMs) count++;
        }
        else if (state.StallEndedByReload) count++;
        return new(state.Plan.Id, played, count, total, longest, state.Plan.DeliveredKbps, Mode, state.Action,
            state.Plan.RequestedBandwidthEstimateKbps);
    }

    // ---- Pill ----

    private void Present(BufferingPillKind kind)
    {
        _presented = new PresentedPill(kind, PromptMs);
        RefreshPill();
    }

    private void WithdrawPill()
    {
        _presented = null;
        RefreshPill();
    }

    /// <summary>
    /// Shows, hides, runs or freezes the pill to match the player. Its timer runs only while it is on screen and
    /// playback is not paused. It counts as shown each time it reaches the screen, and the prompt as offered the first time.
    /// </summary>
    private void RefreshPill()
    {
        if (_presented is not { } shown)
        {
            Cancel(ref _pillTimer);
            Pill = null;
            return;
        }
        var now = NowMs;
        var visible = !_competingPillVisible;
        var running = visible && !_isPaused;
        if (!running && shown.DeadlineMs is { } deadline)
        {
            shown.RemainingMs = Math.Max(0, deadline - now);
            shown.DeadlineMs = null;
            Cancel(ref _pillTimer);
        }
        else if (running && shown.DeadlineMs is null)
        {
            var due = now + shown.RemainingMs;
            shown.DeadlineMs = due;
            _pillTimer = ScheduleAt(due, PillExpired);
        }
        var appearing = visible && !shown.IsOnScreen;
        if (appearing) shown.FirstShownAt ??= now;
        shown.IsOnScreen = visible;
        Pill = visible ? new BufferingPill(shown.Kind, shown.DeadlineMs, shown.RemainingMs) : null;
        if (appearing) Emit(new BufferingPillShownOutput(shown.Kind));
    }

    /// <summary>The prompt's timeout leaves the title unanswered, clears history and starts the re-offer wait; the undo's only hides it.</summary>
    private void PillExpired()
    {
        _pillTimer = null;
        if (_presented is not { } expired) return;
        WithdrawPill();
        if (expired.Kind == BufferingPillKind.Prompt)
        {
            if (_current is { } state) state.Action = BufferingAction.TimedOut;
            // The re-offer wait counts from the prompt's first display.
            _lastOfferAt = expired.FirstShownAt;
            ClearHistory(NowMs);
        }
        else EvaluateLongStall();
    }

    // ---- Helpers ----

    private IDisposable ScheduleAt(long deadline, Action action) => clock.Schedule(Math.Max(0, deadline - NowMs), action);

    private static void Cancel(ref IDisposable? timer)
    {
        timer?.Dispose();
        timer = null;
    }

    private void Emit(BufferingQualityOutput output) => Output?.Invoke(output);

    private sealed class PlanState(BufferingQualityPlan plan)
    {
        public BufferingQualityPlan Plan { get; } = plan;
        public BufferingAction Action { get; set; } = BufferingAction.None;
        /// <summary>STARTUP_GRACE_MS after the plan's first wait or, when none came before it, its first frame.</summary>
        public long? GraceEndsAt { get; set; }
        public Stall? Stall { get; set; }
        public List<EndedStall> EndedStalls { get; } = [];
        public long? CooldownUntil { get; set; }
        /// <summary>Replaced a plan after the attempt had played and has not played itself: a wait is a reload, not a stall.</summary>
        public bool Reloading { get; set; }
        public long PlayedMs { get; set; }
        public int StallCount { get; set; }
        public bool StallEndedByReload { get; set; }
        public long StallTotalMs { get; private set; }
        public long StallLongestMs { get; private set; }

        public void RecordStall(long measuredMs)
        {
            if (measuredMs <= 0) return;
            StallTotalMs += measuredMs;
            StallLongestMs = Math.Max(StallLongestMs, measuredMs);
        }
    }

    private sealed class Stall
    {
        /// <summary>Where the trigger times this stall from; null during transport trouble. Ahead of now in the grace.</summary>
        public long? TimedFrom { get; set; }
        /// <summary>The summary's timing, which a history clear does not re-time.</summary>
        public long? MeasuredFrom { get; set; }
        public long Measured(long now) => MeasuredFrom is { } from ? Math.Max(0, now - from) : 0;
    }

    private sealed record EndedStall(long At, bool Long);

    private enum AttemptStage { New, Adopted, Played }

    private sealed class PendingReplan(bool automatic, int? fromKbps)
    {
        /// <summary><c>always</c> on Auto, whose lower result shows the undo pill; otherwise Lower quality, whose result is a notice.</summary>
        public bool Automatic { get; } = automatic;
        /// <summary>The delivered bitrate the replan left; a lower result is below it.</summary>
        public int? FromKbps { get; } = fromKbps;
        public int? PriorKeptEstimateKbps { get; set; }
    }

    private sealed class PresentedPill(BufferingPillKind kind, long remainingMs)
    {
        public BufferingPillKind Kind { get; } = kind;
        public long RemainingMs { get; set; } = remainingMs;
        public long? DeadlineMs { get; set; }
        public long? FirstShownAt { get; set; }
        public bool IsOnScreen { get; set; }
    }
}
