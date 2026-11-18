using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Bale.Core.Native;
using Bale.Core.Settings;

namespace Bale.Core.Playback;

/// <summary>One <c>available_qualities</c> entry.</summary>
public sealed record BufferingQualityRung(string Label, int BitrateKbps);

/// <summary>What the controller reads from an adopted playback plan.</summary>
/// <param name="Id">
/// The adoption's identity: every adoption is a new plan, so the host gives each one a new id. The server's
/// <c>plan_id</c> is a hash of the route and can repeat, so keying on it would hide a lowering that came back unchanged.
/// </param>
/// <param name="DeliveredKbps"><c>effective_recipe.bitrate_kbps</c>, else <c>source.bitrate_kbps</c>.</param>
/// <param name="Quality"><c>auto</c>, or the label of the rung the viewer picked.</param>
/// <param name="QualityReductionUnavailable"><c>degradation_warnings</c> holds <c>quality_reduction_unavailable</c>.</param>
public sealed record BufferingQualityPlan(string Id, int? DeliveredKbps, string Quality,
    IReadOnlyList<BufferingQualityRung> AvailableQualities, bool QualityReductionUnavailable)
{
    /// <summary>The rung <c>active_quality</c> names (with plan_active_quality_v1), or null.</summary>
    public string? ActiveQuality { get; init; }

    /// <summary>The estimate the plan was requested with, for <c>plan_summary</c>.</summary>
    public int? RequestedBandwidthEstimateKbps { get; init; }

    public bool IsAuto => Quality == BufferingQualityController.AutoQuality;
}

/// <summary>Why the player waits. Only a <see cref="Network"/> wait can be a stall.</summary>
public enum BufferingWaitCause
{
    Network,
    Seek,
    Pause,
    Reload,
    /// <summary>The wait began with more than RENDERER_WAIT_BUFFER_MS buffered: the decoder is behind, not the network.</summary>
    Renderer,
}

/// <summary>The fields of a replan for the host to send: the controller's <c>quality_change</c>, or one the player sends.</summary>
public sealed record BufferingQualityReplan(string Operation, string QualityPreference)
{
    public int? BandwidthEstimateKbps { get; init; }
    /// <summary><c>observed_rebuffer</c>; only to a server that advertises buffering_quality_v1.</summary>
    public string? BandwidthEstimateSource { get; init; }
    /// <summary><c>clear</c>; only to a server that advertises buffering_quality_v1.</summary>
    public string? LearnedLimit { get; init; }
    /// <summary>
    /// The evidence a lowering before anything of the attempt played rested on. Null on every other replan: the
    /// player adds the playing plan's own figure.
    /// </summary>
    public int? ObservedThroughputKbps { get; init; }

    /// <summary>Restore quality: Auto with no estimate.</summary>
    public bool IsRestore => QualityPreference == BufferingQualityController.AutoQuality && BandwidthEstimateKbps is null;
}

/// <summary>A display-only notice.</summary>
public enum BufferingQualityNotice
{
    /// <summary>An accepted lowering came back lower.</summary>
    Lowered,
    /// <summary>No lower rung (once per title), or a lowering that came back no lower.</summary>
    CantLower,
}

public enum BufferingPillKind
{
    /// <summary>"Playback keeps buffering": Lower quality / Keep quality.</summary>
    Prompt,
    /// <summary>"Quality lowered to stop buffering": Restore quality.</summary>
    Undo,
}

/// <summary>The actionable pill on screen.</summary>
/// <param name="DeadlineMs">When the timer runs out on the controller's clock; null while frozen.</param>
/// <param name="RemainingMs">Left as of the last arm or freeze.</param>
public sealed record BufferingPill(BufferingPillKind Kind, long? DeadlineMs, long RemainingMs);

/// <summary><c>buffering_action</c>: the last action on a plan.</summary>
public enum BufferingAction { None, Ignored, Lowered, Restored, Accepted, Kept, TimedOut, CantLower }

/// <summary>The <c>plan_summary</c> route event's figures, for a plan that stopped being the playing plan.</summary>
public sealed record BufferingPlanSummary(string PlanId, long PlayedMs, int StallCount, long StallTotalMs, long StallLongestMs,
    int? DeliveredKbps, BufferingQualityMode Mode, BufferingAction Action, int? BandwidthEstimateKbps)
{
    /// <summary>The rate the plan's media requests received data; the player adds it.</summary>
    public int? ThroughputKbps { get; init; }
}

public abstract record BufferingQualityOutput;
public sealed record BufferingReplanOutput(BufferingQualityReplan Request) : BufferingQualityOutput;
/// <summary>A pill came on screen: first, or again after something that hid it went away.</summary>
public sealed record BufferingPillShownOutput(BufferingPillKind Kind) : BufferingQualityOutput;
public sealed record BufferingNoticeOutput(BufferingQualityNotice Notice) : BufferingQualityOutput;
public sealed record BufferingSummaryOutput(BufferingPlanSummary Summary) : BufferingQualityOutput;

/// <summary>
/// Lowers quality when playback keeps buffering: silo-server docs/design/2026-10-17-buffering-quality.md, its stall
/// tracker and trigger (Terms) and every table under "Behaviour", ported from silo-apple's
/// BufferingQualityController.swift (playback follows Apple; Android's BufferingQualityController.kt is the same spec).
/// The tests replay the server's conformance vectors (schemas/buffering-quality/v1/vectors.json) case for case.
/// The player feeds it plan adoptions, media events and the viewer's answers, and acts on <see cref="Output"/>:
/// replans to send, pills and notices to show, and <c>plan_summary</c> figures. It performs no I/O and is not
/// thread-safe: call it on the UI thread, which also polls the clock's timers.
/// <para>
/// The decisions and their state live in Rust (native/bale-core/src/buffering.rs, docs/rust-port.md). Each call sends
/// one event with the clock's time, <see cref="Mode"/>, <see cref="SendsBufferingQualityFields"/> and the current
/// <see cref="MeasuredThroughputKbps"/>, and gets back the ordered steps it caused: timers to schedule or cancel on
/// <c>clock</c>, and outputs, which are raised through <see cref="Output"/> in that order once the call has returned.
/// </para>
/// </summary>
public sealed class BufferingQualityController
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

    // Rust's Timer names, by slot.
    private static readonly string[] TimerNames = ["long_stall", "suppression", "rearm", "pill"];

    private readonly IBufferingQualityClock _clock;
    private readonly BufferingHandle _handle;
    private readonly ArrayBufferWriter<byte> _input = new(512);
    private readonly Utf8JsonWriter _writer;
    // The clock timer Rust last scheduled per kind (the original's timer fields), and what each one runs.
    private readonly IDisposable?[] _timers = new IDisposable?[4];
    private readonly Action[] _timerActions = new Action[4];
    private bool _competingPillVisible;

    public BufferingQualityController(IBufferingQualityClock clock)
    {
        _clock = clock;
        _handle = BaleCoreBuffering.bale_core_buffering_create();
        _writer = new Utf8JsonWriter(_input);
        for (var slot = 0; slot < _timers.Length; slot++)
        {
            var timer = slot;
            _timerActions[slot] = () =>
            {
                _timers[timer] = null;
                Run("timer", TimerNames[timer], static (writer, name) => writer.WriteString("timer", name));
            };
        }
    }

    public BufferingQualityMode Mode { get; set; } = BufferingQualityModes.Default;

    /// <summary>The server advertises buffering_quality_v1; without it replans drop the source and learned_limit.</summary>
    public bool SendsBufferingQualityFields { get; set; }

    public Action<BufferingQualityOutput>? Output { get; set; }

    /// <summary>
    /// What the playing plan's media has measurably arrived at so far, in kbps, or null. Read once before every call
    /// into the controller and passed with it: no call changes what it reads.
    /// </summary>
    public Func<int?> MeasuredThroughputKbps { get; set; } = () => null;

    /// <summary>The actionable pill on screen, or null.</summary>
    public BufferingPill? Pill { get; private set; }

    /// <summary>
    /// The step-down estimate of the latest Auto lowering, from its replan until Restore quality, a quality change
    /// the controller did not send, or a new attempt. Later Auto replans repeat it without a source.
    /// </summary>
    public int? KeptEstimateKbps { get; private set; }

    /// <summary>A pill is waiting, visible or hidden behind another surface.</summary>
    public bool PillPending { get; private set; }

    /// <summary>The adoption id of the playing plan, or null.</summary>
    public string? CurrentPlanId { get; private set; }

    // ---- Plans ----

    /// <summary>
    /// A plan became the playing plan. It ends the outgoing plan (emitting its summary), takes down any pill and
    /// starts fresh history. A plan that answers the controller's pending replan settles it. Pass
    /// <paramref name="controllerInitiated"/> false for a plan answering a replan the controller did not send.
    /// </summary>
    public void PlanAdopted(BufferingQualityPlan plan, bool controllerInitiated = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Run("plan_adopted", (plan, controllerInitiated), static (writer, args) =>
        {
            writer.WritePropertyName("plan");
            WritePlan(writer, args.plan);
            writer.WriteBoolean("controller_initiated", args.controllerInitiated);
        });
    }

    /// <summary>The playing plan's summary as if it ended now, without ending it.</summary>
    public BufferingPlanSummary? RunningSummary => Run("running_summary").Summary is { } summary ? ToSummary(summary) : null;

    /// <summary>The plan stopped or failed with no successor: emits its summary.</summary>
    public void PlanEnded() => Run("plan_ended");

    /// <summary>A new playback attempt of the same title: the kept estimate goes, its first plan starts afresh.</summary>
    public void NewAttempt() => Run("new_attempt");

    // ---- Media events ----

    public void Playing() => Run("playing");

    /// <summary>
    /// The player waits. A network wait is a stall, except while a replacement of a plan that played loads; any
    /// other cause ends a stall in progress without counting it. <paramref name="startedAgoMs"/> back-dates a wait
    /// noticed late, never past the start of the current playing state.
    /// </summary>
    public void Waiting(BufferingWaitCause cause = BufferingWaitCause.Network, long startedAgoMs = 0) =>
        Run("waiting", (cause, startedAgoMs), static (writer, args) =>
        {
            writer.WriteString("cause", args.cause switch
            {
                BufferingWaitCause.Network => "network",
                BufferingWaitCause.Seek => "seek",
                BufferingWaitCause.Pause => "pause",
                BufferingWaitCause.Reload => "reload",
                BufferingWaitCause.Renderer => "renderer",
                _ => throw new ArgumentOutOfRangeException(nameof(cause)),
            });
            writer.WriteNumber("started_ago_ms", args.startedAgoMs);
        });

    public void Paused() => Run("paused");

    public void Seeked() => Run("seeked");

    /// <summary>
    /// An outage: media requests failing or silent, or the device offline. A stall that runs into it drops its time
    /// so far, and stall time during it never counts; a stall still in progress when it ends is timed from then.
    /// </summary>
    public void TransportTroubleChanged(bool trouble) =>
        Run("transport_trouble", trouble, static (writer, trouble) => writer.WriteBoolean("trouble", trouble));

    /// <summary>The controller's replan failed. The plan keeps playing and is watched again at the rearm.</summary>
    public void ReplanFailed() => Run("replan_failed");

    /// <summary>The player could not send the controller's replan at all: it fails like <see cref="ReplanFailed"/>, and keeps no estimate.</summary>
    public void ReplanRefused() => Run("replan_refused");

    /// <summary>
    /// The plan failed before its first frame because the player's own start deadline passed. On Auto in
    /// <c>always</c> with evidence that its media arrived below its delivered bitrate, it lowers as a long stall
    /// would and returns true; false leaves the failure to the player's usual recovery.
    /// </summary>
    public bool StartDeadlinePassed() => Run("start_deadline_passed").Returned == true;

    // ---- Pill answers ----

    /// <summary>Lower quality on the prompt.</summary>
    public void SelectLower() => Run("select_lower");

    /// <summary>Keep quality on the prompt: answers the title at this quality.</summary>
    public void SelectKeep() => Run("select_keep");

    /// <summary>Restore quality on the undo pill: answers the title and clears the learned limit.</summary>
    public void SelectRestore() => Run("select_restore");

    /// <summary>
    /// Back. On the prompt it answers like Keep; on the undo pill it only hides it. True when a visible pill consumed
    /// the press, so a second Back behaves normally.
    /// </summary>
    public bool Back() => Run("back").Returned == true;

    /// <summary>Select at the player root: the visible pill's primary action.</summary>
    public void SelectPrimary() => Run("select_primary");

    // ---- Host inputs ----

    /// <summary>A quality change the controller did not send (the viewer's pick, as its plan is adopted).</summary>
    public void QualityChanged() => Run("quality_changed");

    /// <summary>The viewer's quality pick was sent: pills hide, nothing prompts or lowers while it is pending.</summary>
    public void PickSent() => Run("pick_sent");

    /// <summary>The pick never became a plan: nothing else changes, and a long stall in progress is evaluated at once.</summary>
    public void PickRefused() => Run("pick_refused");

    /// <summary>
    /// A replan the player sends for its own reasons (track change, seek reanchor, failure recovery): the adopted
    /// plan's quality and, on Auto, the kept estimate without a source. Null before a plan is adopted.
    /// </summary>
    public BufferingQualityReplan? ClientReplan(string operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Run("client_replan", operation, static (writer, operation) => writer.WriteString("operation", operation)).Replan is { } replan
            ? ToReplan(replan)
            : null;
    }

    /// <summary>
    /// Up Next, Options or a track menu covers the pill, or the player cannot show pills: one that would appear waits,
    /// one already up hides with its timer frozen; either shows again when this clears, if the plan has not changed.
    /// </summary>
    public void SetCompetingPillVisible(bool visible)
    {
        // The page reports this every tick; Rust ignores a repeat too, so only a change crosses.
        if (visible == _competingPillVisible) return;
        _competingPillVisible = visible;
        Run("competing_pill_visible", visible, static (writer, visible) => writer.WriteBoolean("visible", visible));
    }

    // ---- Stateless rules ----

    /// <summary>
    /// The spec's lower rung: the highest real rung below the lower of the named rung's bitrate and the delivered
    /// bitrate, or below the delivered bitrate when the plan names no rung. Equal bitrates keep the earlier entry.
    /// </summary>
    public static BufferingQualityRung? LowerRung(BufferingQualityPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return BaleCoreNative.Call("buffering.lower_rung", writer =>
        {
            writer.WritePropertyName("plan");
            WritePlan(writer, plan);
        }, BaleCoreJson.Default.NullableInt32) is { } index ? plan.AvailableQualities[index] : null;
    }

    /// <summary><c>floor(delivered × 7 / 10)</c> in integer arithmetic, or null below MIN_ESTIMATE_KBPS (22120 → 15484).</summary>
    public static int? StepDownEstimate(int? deliveredKbps) =>
        BaleCoreNative.Call("buffering.step_down_estimate", deliveredKbps, BaleCoreJson.Default.NullableInt32,
            BaleCoreJson.Default.NullableInt32);

    // ---- The bridge ----

    private BufferingOutcome Run(string type) => Run<object?>(type, null, null);

    private BufferingOutcome Run<TArgs>(string type, TArgs args, Action<Utf8JsonWriter, TArgs>? fields)
    {
        var measured = MeasuredThroughputKbps();
        _input.ResetWrittenCount();
        _writer.Reset(_input);
        _writer.WriteStartObject();
        _writer.WriteNumber("now", _clock.ElapsedMs);
        _writer.WriteString("mode", BufferingQualityModes.Wire(Mode));
        _writer.WriteBoolean("sends_fields", SendsBufferingQualityFields);
        if (measured is { } kbps) _writer.WriteNumber("measured_kbps", kbps);
        else _writer.WriteNull("measured_kbps");
        _writer.WriteStartObject("event");
        _writer.WriteString("type", type);
        fields?.Invoke(_writer, args);
        _writer.WriteEndObject();
        _writer.WriteEndObject();
        _writer.Flush();
        var outcome = BaleCoreBuffering.Call(_handle, _input);
        Apply(outcome);
        return outcome;
    }

    /// <summary>
    /// Takes the state, then performs the steps in order. An output handler may call back in (the page refuses a
    /// replan it cannot send at once); that call applies its own steps and state before the rest of these.
    /// </summary>
    private void Apply(BufferingOutcome outcome)
    {
        var state = outcome.State;
        Pill = state.Pill is { } pill
            ? Pill is { } shown && shown.Kind == Kind(pill.Kind) && shown.DeadlineMs == pill.DeadlineMs && shown.RemainingMs == pill.RemainingMs
                ? shown
                : new BufferingPill(Kind(pill.Kind), pill.DeadlineMs, pill.RemainingMs)
            : null;
        PillPending = state.PillPending;
        KeptEstimateKbps = state.KeptEstimateKbps;
        if (state.CurrentPlanId != CurrentPlanId) CurrentPlanId = state.CurrentPlanId;
        foreach (var step in outcome.Steps)
        {
            switch (step.Step)
            {
                case "schedule":
                    var slot = Slot(step.Timer);
                    // A timer scheduled over one still waiting leaves that one waiting, as the original's field did.
                    _timers[slot] = _clock.Schedule(Math.Max(0, step.At!.Value - _clock.ElapsedMs), _timerActions[slot]);
                    break;
                case "cancel":
                    var cancelled = Slot(step.Timer);
                    _timers[cancelled]?.Dispose();
                    _timers[cancelled] = null;
                    break;
                case "replan":
                    Output?.Invoke(new BufferingReplanOutput(ToReplan(step.Replan!)));
                    break;
                case "pill_shown":
                    Output?.Invoke(new BufferingPillShownOutput(Kind(step.Pill)));
                    break;
                case "notice":
                    Output?.Invoke(new BufferingNoticeOutput(step.Notice switch
                    {
                        "lowered" => BufferingQualityNotice.Lowered,
                        "cant_lower" => BufferingQualityNotice.CantLower,
                        _ => throw new InvalidDataException("unknown buffering notice"),
                    }));
                    break;
                case "summary":
                    Output?.Invoke(new BufferingSummaryOutput(ToSummary(step.Summary!)));
                    break;
                default:
                    throw new InvalidDataException("unknown buffering step");
            }
        }
    }

    private static int Slot(string? timer) => Array.IndexOf(TimerNames, timer) is var slot and >= 0
        ? slot
        : throw new InvalidDataException("unknown buffering timer");

    private static BufferingPillKind Kind(string? kind) => kind switch
    {
        "prompt" => BufferingPillKind.Prompt,
        "undo" => BufferingPillKind.Undo,
        _ => throw new InvalidDataException("unknown buffering pill"),
    };

    private static BufferingQualityReplan ToReplan(BufferingReplanWire wire) => new(wire.Operation, wire.QualityPreference)
    {
        BandwidthEstimateKbps = wire.BandwidthEstimateKbps,
        BandwidthEstimateSource = wire.BandwidthEstimateSource,
        LearnedLimit = wire.LearnedLimit,
        ObservedThroughputKbps = wire.ObservedThroughputKbps,
    };

    private static BufferingPlanSummary ToSummary(BufferingSummaryWire wire) => new(wire.PlanId, wire.PlayedMs, wire.StallCount,
        wire.StallTotalMs, wire.StallLongestMs, wire.DeliveredKbps,
        BufferingQualityModes.FromWire(wire.Mode) ?? throw new InvalidDataException("unknown buffering mode"),
        BufferingQualityWire.FromWire(wire.Action), wire.BandwidthEstimateKbps);

    private static void WritePlan(Utf8JsonWriter writer, BufferingQualityPlan plan)
    {
        writer.WriteStartObject();
        writer.WriteString("id", plan.Id);
        writer.Number("delivered_kbps", plan.DeliveredKbps);
        writer.WriteString("quality", plan.Quality);
        writer.WriteStartArray("available_qualities");
        foreach (var rung in plan.AvailableQualities)
        {
            writer.WriteStartObject();
            writer.WriteString("label", rung.Label);
            writer.WriteNumber("bitrate_kbps", rung.BitrateKbps);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteBoolean("quality_reduction_unavailable", plan.QualityReductionUnavailable);
        writer.Text("active_quality", plan.ActiveQuality);
        writer.Number("requested_bandwidth_estimate_kbps", plan.RequestedBandwidthEstimateKbps);
        writer.WriteEndObject();
    }
}

/// <summary>The wire spellings of <see cref="BufferingAction"/> and the <c>plan_summary</c> diagnostics.</summary>
public static class BufferingQualityWire
{
    public static string Action(BufferingAction action) => action switch
    {
        BufferingAction.Ignored => "ignored",
        BufferingAction.Lowered => "lowered",
        BufferingAction.Restored => "restored",
        BufferingAction.Accepted => "accepted",
        BufferingAction.Kept => "kept",
        BufferingAction.TimedOut => "timed_out",
        BufferingAction.CantLower => "cant_lower",
        _ => "none",
    };

    /// <summary>The <see cref="BufferingAction"/> a wire spelling names.</summary>
    internal static BufferingAction FromWire(string action) => action switch
    {
        "none" => BufferingAction.None,
        "ignored" => BufferingAction.Ignored,
        "lowered" => BufferingAction.Lowered,
        "restored" => BufferingAction.Restored,
        "accepted" => BufferingAction.Accepted,
        "kept" => BufferingAction.Kept,
        "timed_out" => BufferingAction.TimedOut,
        "cant_lower" => BufferingAction.CantLower,
        _ => throw new InvalidDataException("unknown buffering action"),
    };

    /// <summary>
    /// The route event's diagnostics (design doc, Telemetry; silo-apple routeEventDiagnostics). Every value is a
    /// string; an unknown estimate, bitrate or throughput is left out rather than sent as zero.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Diagnostics(BufferingPlanSummary summary)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["played_ms"] = Text(Math.Max(0, summary.PlayedMs)),
            ["stall_count"] = Text(Math.Max(0, summary.StallCount)),
            ["stall_total_ms"] = Text(Math.Max(0, summary.StallTotalMs)),
            ["stall_longest_ms"] = Text(Math.Max(0, summary.StallLongestMs)),
            ["buffering_mode"] = BufferingQualityModes.Wire(summary.Mode),
            ["buffering_action"] = Action(summary.Action),
        };
        if (summary.DeliveredKbps is > 0 and var delivered) values["delivered_kbps"] = Text(delivered);
        if (summary.BandwidthEstimateKbps is > 0 and var estimate) values["bandwidth_estimate_kbps"] = Text(estimate);
        if (summary.ThroughputKbps is >= PlanThroughput.MinKbps and <= PlanThroughput.MaxKbps and var throughput)
            values["throughput_kbps"] = Text(throughput);
        return values;

        static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
