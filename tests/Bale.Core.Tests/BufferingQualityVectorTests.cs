using System.Text.Json;
using System.Text.Json.Nodes;
using Bale.Core.Playback;
using Bale.Core.Settings;

namespace Bale.Core.Tests;

/// <summary>
/// The conformance suite for lowering quality when playback keeps buffering. The oracle is silo-server's
/// docs/design/2026-10-17-buffering-quality.md, encoded as schemas/buffering-quality/v1/vectors.json (copied unchanged
/// from silo-server e3af98b0, AGPL-3.0 like Bale). Web, Android and Apple replay the same file.
/// </summary>
public sealed class BufferingQualityVectorTests
{
    private static readonly JsonObject Vectors = JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures/buffering-quality-v1-vectors.json")))!.AsObject();

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var vector in Vectors["cases"]!.AsArray()) data.Add(vector!["name"]!.GetValue<string>());
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void EveryVectorCase(string name) => Replay(Case(name), sendsBufferingQualityFields: true);

    /// <summary>Against a server without buffering_quality_v1 the controller behaves the same, minus two fields.</summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void EveryVectorCaseWithoutBufferingQualityFeature(string name) => Replay(Case(name), sendsBufferingQualityFields: false);

    [Fact]
    public void ConstantsCopyAndVocabularyMatchTheVectors()
    {
        var constants = Vectors["constants"]!.AsObject();
        Assert.Equal(["LONG_STALL_MS", "MIN_ESTIMATE_KBPS", "NO_LOWER_COOLDOWN_MS", "PROMPT_SECONDS", "REARM_AFTER_UNADOPTED_MS",
            "RENDERER_WAIT_BUFFER_MS", "REOFFER_MIN_MS", "STALLS_BEFORE_TRIGGER", "STALL_MIN_MS", "STALL_WINDOW_MS",
            "STARTUP_GRACE_MS", "STEP_DOWN_RATIO"], constants.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(BufferingQualityController.StartupGraceMs, constants["STARTUP_GRACE_MS"]!.GetValue<long>());
        Assert.Equal(BufferingQualityController.StallMinMs, constants["STALL_MIN_MS"]!.GetValue<long>());
        Assert.Equal(BufferingQualityController.StallsBeforeTrigger, constants["STALLS_BEFORE_TRIGGER"]!.GetValue<int>());
        Assert.Equal(BufferingQualityController.StallWindowMs, constants["STALL_WINDOW_MS"]!.GetValue<long>());
        Assert.Equal(BufferingQualityController.LongStallMs, constants["LONG_STALL_MS"]!.GetValue<long>());
        Assert.Equal(BufferingQualityController.PromptSeconds, constants["PROMPT_SECONDS"]!.GetValue<int>());
        Assert.Equal(BufferingQualityController.ReofferMinMs, constants["REOFFER_MIN_MS"]!.GetValue<long>());
        Assert.Equal(BufferingQualityController.NoLowerCooldownMs, constants["NO_LOWER_COOLDOWN_MS"]!.GetValue<long>());
        Assert.Equal(BufferingQualityController.RearmAfterUnadoptedMs, constants["REARM_AFTER_UNADOPTED_MS"]!.GetValue<long>());
        Assert.Equal(BufferingQualityController.StepDownRatio, constants["STEP_DOWN_RATIO"]!.GetValue<double>());
        Assert.Equal(BufferingQualityController.MinEstimateKbps, constants["MIN_ESTIMATE_KBPS"]!.GetValue<int>());
        Assert.Equal(BufferingQualityController.RendererWaitBufferMs, constants["RENDERER_WAIT_BUFFER_MS"]!.GetValue<long>());

        var copy = Vectors["copy"]!.AsObject();
        Assert.Equal(BufferingQualityController.PromptTitle, copy["prompt_title"]!.GetValue<string>());
        Assert.Equal(BufferingQualityController.LowerAction, copy["prompt_lower"]!.GetValue<string>());
        Assert.Equal(BufferingQualityController.KeepAction, copy["prompt_keep"]!.GetValue<string>());
        Assert.Equal(BufferingQualityController.UndoCaption, copy["undo_caption"]!.GetValue<string>());
        Assert.Equal(BufferingQualityController.UndoAction, copy["undo_action"]!.GetValue<string>());
        Assert.Equal(BufferingQualityController.LoweredNotice, copy["lowered"]!.GetValue<string>());
        Assert.Equal(BufferingQualityController.CantLowerNotice, copy["cant_lower"]!.GetValue<string>());
        Assert.Equal(7, copy.Count);

        // The replay understands exactly these; anything new must fail loudly rather than be skipped.
        Assert.Equal(KnownEvents.Order(StringComparer.Ordinal),
            Vectors["event_types"]!.AsArray().Select(node => node!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal(["notice", "prompt_shown", "replan", "undo_shown"],
            Vectors["output_types"]!.AsArray().Select(node => node!.GetValue<string>()).Order(StringComparer.Ordinal));
        var actions = Enum.GetValues<BufferingAction>().Select(BufferingQualityWire.Action).ToHashSet();
        foreach (var vector in Vectors["cases"]!.AsArray())
            if (vector!["buffering_action"] is JsonObject expected)
                foreach (var pair in expected) Assert.Contains(pair.Value!.GetValue<string>(), actions);
    }

    private static readonly string[] KnownEvents =
    [
        "plan_adopted", "playing", "waiting", "pause", "seek", "transport_error_start", "transport_error_end", "replan_failed",
        "select_lower", "select_keep", "select_restore", "back", "pill_obscured", "pill_unobscured", "client_replan",
        "quality_changed", "pick_sent", "pick_refused", "new_attempt",
    ];

    private static JsonObject Case(string name) =>
        Vectors["cases"]!.AsArray().Single(vector => vector!["name"]!.GetValue<string>() == name)!.AsObject();

    private static void Replay(JsonObject vector, bool sendsBufferingQualityFields)
    {
        var mode = BufferingQualityModes.FromWire(vector["mode"]?.GetValue<string>() ?? Vectors["defaults"]!["mode"]!.GetValue<string>())
            ?? throw new InvalidDataException("mode");
        var harness = new BufferingQualityHarness(mode) { SendsBufferingQualityFields = sendsBufferingQualityFields };
        // The vectors' waits are a link too slow for every plan: each plan's media measures below its bitrate.
        harness.MeasuredKbps = 1;
        var adopted = new List<string>();
        foreach (var node in vector["events"]!.AsArray())
        {
            var item = node!.AsObject();
            harness.Clock.AdvanceTo(item["t"]!.GetValue<long>());
            if (Apply(harness, item) is { } plan) adopted.Add(plan);
        }
        harness.Clock.AdvanceTo(vector["until"]!.GetValue<long>());
        harness.Controller.PlanEnded();

        var expected = vector["expect"]!.AsArray().Select(node => Expected(node!.AsObject(), sendsBufferingQualityFields)).ToList();
        var actual = harness.Shown.Select(Actual).ToList();
        Assert.Equal(expected, actual);
        Assert.Equal(adopted, harness.Summaries.Select(summary => summary.PlanId));
        if (vector["buffering_action"] is JsonObject actions)
            foreach (var pair in actions)
                Assert.Equal(pair.Value!.GetValue<string>(),
                    BufferingQualityWire.Action(harness.Summaries.First(summary => summary.PlanId == pair.Key).Action));
    }

    /// <summary>Feeds one event; returns the adopted plan's id for plan_adopted.</summary>
    private static string? Apply(BufferingQualityHarness harness, JsonObject item)
    {
        var controller = harness.Controller;
        var type = item["type"]!.GetValue<string>();
        switch (type)
        {
            case "plan_adopted":
                var plan = Plan(item["plan"]!.AsObject());
                controller.PlanAdopted(plan, item["controllerInitiated"]?.GetValue<bool>() ?? true);
                return plan.Id;
            case "playing": controller.Playing(); break;
            case "waiting":
                controller.Waiting(item["cause"]?.GetValue<string>() switch
                {
                    null => BufferingWaitCause.Network,
                    "seek" => BufferingWaitCause.Seek,
                    "pause" => BufferingWaitCause.Pause,
                    "reload" => BufferingWaitCause.Reload,
                    "renderer" => BufferingWaitCause.Renderer,
                    var cause => throw new InvalidDataException("unknown wait cause " + cause),
                });
                break;
            case "pause": controller.Paused(); break;
            case "seek": controller.Seeked(); break;
            case "transport_error_start": controller.TransportTroubleChanged(true); break;
            case "transport_error_end": controller.TransportTroubleChanged(false); break;
            case "replan_failed": controller.ReplanFailed(); break;
            case "select_lower": controller.SelectLower(); break;
            case "select_keep": controller.SelectKeep(); break;
            case "select_restore": controller.SelectRestore(); break;
            case "back": controller.Back(); break;
            case "pill_obscured": controller.SetCompetingPillVisible(true); break;
            case "pill_unobscured": controller.SetCompetingPillVisible(false); break;
            case "client_replan":
                var operation = item["operation"]!.GetValue<string>();
                Assert.Contains(operation, (string[])["track_change", "seek_reanchor", "seek_failure_recovery", "failure_recovery", "output_change"]);
                // What the player sends itself with the controller's fields.
                if (controller.ClientReplan(operation) is { } request) harness.Record(new BufferingReplanOutput(request));
                break;
            case "quality_changed": controller.QualityChanged(); break;
            case "pick_sent": controller.PickSent(); break;
            case "pick_refused": controller.PickRefused(); break;
            case "new_attempt": controller.NewAttempt(); break;
            default: throw new InvalidDataException("unknown event type " + type);
        }
        return null;
    }

    private static BufferingQualityPlan Plan(JsonObject plan)
    {
        var warnings = plan["degradation_warnings"]?.AsArray().Select(node => node!.GetValue<string>()).ToArray() ?? [];
        // The vectors write a missing delivered bitrate as 0.
        var delivered = plan["delivered_kbps"]?.GetValue<int>();
        return new(plan["plan_id"]!.GetValue<string>(), delivered is 0 ? null : delivered, plan["quality"]!.GetValue<string>(),
            plan["available_qualities"]!.AsArray().Select(rung => new BufferingQualityRung(rung!["label"]!.GetValue<string>(),
                rung["bitrate_kbps"]!.GetValue<int>())).ToArray(),
            warnings.Contains("quality_reduction_unavailable"))
        {
            ActiveQuality = plan["active_quality"]?.GetValue<string>(),
        };
    }

    /// <summary>An output in the vectors' shape: time, type, and a canonical body or notice key.</summary>
    private static string Expected(JsonObject output, bool sendsBufferingQualityFields)
    {
        var t = output["t"]!.GetValue<long>();
        var type = output["type"]!.GetValue<string>();
        return type switch
        {
            "replan" => $"{t} replan {Canonical(output["body"]!.AsObject(), sendsBufferingQualityFields)}",
            "notice" => $"{t} notice {output["key"]!.GetValue<string>()}",
            "prompt_shown" or "undo_shown" => $"{t} {type}",
            _ => throw new InvalidDataException("unknown output type " + type),
        };
    }

    private static string Actual((long T, BufferingQualityOutput Output) recorded) => recorded.Output switch
    {
        BufferingReplanOutput { Request: var request } => $"{recorded.T} replan {Canonical(Body(request), true)}",
        BufferingPillShownOutput { Kind: var kind } => $"{recorded.T} {(kind == BufferingPillKind.Prompt ? "prompt_shown" : "undo_shown")}",
        BufferingNoticeOutput { Notice: var notice } => $"{recorded.T} notice {(notice == BufferingQualityNotice.Lowered ? "lowered" : "cant_lower")}",
        _ => throw new InvalidOperationException("summaries are checked separately"),
    };

    /// <summary>The request body as the vectors write it: operation and every present field (observed throughput is outside them).</summary>
    private static JsonObject Body(BufferingQualityReplan request)
    {
        var body = new JsonObject { ["operation"] = request.Operation, ["quality_preference"] = request.QualityPreference };
        if (request.BandwidthEstimateKbps is { } estimate) body["bandwidth_estimate_kbps"] = estimate;
        if (request.BandwidthEstimateSource is { } source) body["bandwidth_estimate_source"] = source;
        if (request.LearnedLimit is { } limit) body["learned_limit"] = limit;
        return body;
    }

    private static string Canonical(JsonObject body, bool keepBufferingQualityFields) => JsonSerializer.Serialize(
        body.Where(pair => keepBufferingQualityFields || pair.Key is not ("bandwidth_estimate_source" or "learned_limit"))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value!.ToJsonString()));
}

/// <summary>A clock the test advances by hand: due timers fire in deadline order with the clock at their deadline.</summary>
internal sealed class ManualBufferingQualityClock
{
    private long _now;

    public ManualBufferingQualityClock() => Timers = new PolledBufferingQualityClock(() => _now);

    public PolledBufferingQualityClock Timers { get; }

    public long NowMs => _now;

    public void AdvanceTo(long ms)
    {
        Assert.True(ms >= _now, "the clock only moves forward");
        while (Timers.NextDueMs is { } due && due <= ms)
        {
            _now = Math.Max(_now, due);
            Timers.Fire();
        }
        _now = ms;
        Timers.Fire();
    }
}

/// <summary>A controller on a manual clock, recording every output with its time.</summary>
internal sealed class BufferingQualityHarness
{
    private readonly List<(long T, BufferingQualityOutput Output)> _outputs = [];

    public BufferingQualityHarness(BufferingQualityMode mode)
    {
        Controller = new BufferingQualityController(Clock.Timers)
        {
            Mode = mode,
            SendsBufferingQualityFields = true,
            Output = Record,
            MeasuredThroughputKbps = () => MeasuredKbps,
        };
    }

    /// <summary>A reading below <see cref="Plan"/>'s 63 Mbps: a link too slow for it.</summary>
    public const int SlowLinkKbps = 20_000;

    public ManualBufferingQualityClock Clock { get; } = new();
    public BufferingQualityController Controller { get; }

    /// <summary>
    /// The playing plan's reading. A slow link by default, so a lowering claims it (silo-android 878c972c4 defaults its
    /// harness to a reading too); the cases without evidence set null or a fast one.
    /// </summary>
    public int? MeasuredKbps { get; set; } = SlowLinkKbps;

    public bool SendsBufferingQualityFields
    {
        get => Controller.SendsBufferingQualityFields;
        set => Controller.SendsBufferingQualityFields = value;
    }

    public void Record(BufferingQualityOutput output) => _outputs.Add((Clock.NowMs, output));

    /// <summary>Everything but plan summaries: what the vectors' expect lists.</summary>
    public IReadOnlyList<(long T, BufferingQualityOutput Output)> Shown => _outputs.Where(item => item.Output is not BufferingSummaryOutput).ToList();

    public IReadOnlyList<BufferingPlanSummary> Summaries =>
        _outputs.Select(item => item.Output).OfType<BufferingSummaryOutput>().Select(output => output.Summary).ToList();

    public IReadOnlyList<BufferingQualityReplan> Replans =>
        _outputs.Select(item => item.Output).OfType<BufferingReplanOutput>().Select(output => output.Request).ToList();

    public void At(long ms, Action<BufferingQualityController> action)
    {
        Clock.AdvanceTo(ms);
        action(Controller);
    }

    public static readonly BufferingQualityRung[] Ladder =
    [
        new("original", 63_000), new("1080p-high", 10_000), new("1080p-medium", 6_000), new("720p-high", 4_000),
    ];

    public static BufferingQualityPlan Plan(string id, int? delivered = 63_000, string quality = "auto") =>
        new(id, delivered, quality, Ladder, false);

    public void StartPlaying(BufferingQualityPlan plan, long at = 0) => At(at, controller =>
    {
        controller.PlanAdopted(plan);
        controller.Playing();
    });
}
