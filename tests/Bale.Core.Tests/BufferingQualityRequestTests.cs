using System.Net;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Core.Settings;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

/// <summary>
/// The fields a buffering-quality replan carries per mode and feature (silo-server validateBufferingQualityV3), the
/// plan's measured throughput, and the plan_summary route event.
/// </summary>
public sealed class BufferingQualityRequestTests
{
    private const string SpeedCapabilities = """
        {"installation_id":"installation","state":"available","allowed":true,"protocol_versions":[3],
         "features":["sequenced_progress_v1","buffering_quality_v1","connection_speed_v1"],"deliveries":["original_http","hls"],"revision":"1",
         "bandwidth_probe_url":"/api/v2/playback/bandwidth-probe","bandwidth_probe_max_age_seconds":604800}
        """;

    private static readonly PlaybackPlan Plan = new()
    {
        PlanId = "plan-0123456789", PlanAttemptKey = "v3:0123456789abcdef", Delivery = "original_http",
    };

    private static JsonElement Body(StubHandler stub, int index) => JsonDocument.Parse(stub.RequestBodies[index]!).RootElement;

    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString() : null;

    private static readonly BufferingQualityReplan Lowering = new("quality_change", "auto")
    {
        BandwidthEstimateKbps = 44_100, BandwidthEstimateSource = "observed_rebuffer", ObservedThroughputKbps = 30_000,
    };

    [Fact]
    public void AQualityChangeKeepsTheSessionTracksAndPosition()
    {
        var start = Start();
        var request = PlaybackReplan.ForQualityChange(start, Plan, "plan-attempt", ["v3:earlier"], 2, 1234.5, Lowering);
        Assert.Equal("quality_change", request.Operation);
        Assert.Equal("auto", request.QualityPreference);
        Assert.Equal(44_100, request.BandwidthEstimateKbps);
        Assert.Equal("observed_rebuffer", request.BandwidthEstimateSource);
        Assert.Equal(30_000, request.ObservedThroughputKbps);
        Assert.Null(request.Failure);
        Assert.Equal(1234.5, request.PositionSeconds);
        Assert.Equal(2, request.AttemptCount);
        // Nothing failed: the playing plan's key is not added to the attempted keys.
        Assert.Equal(["v3:earlier"], request.AttemptedPlanKeys);
        Assert.Equal(start.PlaybackAttemptId, request.PlaybackAttemptId);
    }

    [Fact]
    public void ClientReplansRepeatTheKeptEstimateWithoutASource()
    {
        var seek = PlaybackReplan.ForSeek(Start(), Plan, "plan-attempt", [], 1, 30);
        var kept = PlaybackReplan.WithClientReplan(seek, new("seek_reanchor", "auto") { BandwidthEstimateKbps = 44_100 });
        Assert.Equal(44_100, kept.BandwidthEstimateKbps);
        Assert.Null(kept.BandwidthEstimateSource);
        Assert.Equal("seek_reanchor", kept.Operation);
        Assert.Same(seek, PlaybackReplan.WithClientReplan(seek, null));
    }

    [Fact]
    public void FieldsFollowTheServersValidation()
    {
        var request = PlaybackReplan.ForQualityChange(Start(), Plan, "plan-attempt", [], 1, 30, Lowering);
        string[] with = [ConnectionSpeed.BufferingQualityFeature];
        Assert.Equal(request, PlaybackProtocol.BufferingQualityFields(request, with));

        // Without the feature the server refuses source and observed throughput; the estimate stays.
        var without = PlaybackProtocol.BufferingQualityFields(request, ["playback_plan_v3"]);
        Assert.Equal(44_100, without.BandwidthEstimateKbps);
        Assert.Null(without.BandwidthEstimateSource);
        Assert.Null(without.ObservedThroughputKbps);

        // Observed throughput needs observed_rebuffer and 100–1,000,000; an estimate out of bounds goes with its source.
        Assert.Null(PlaybackProtocol.BufferingQualityFields(request with { ObservedThroughputKbps = 99 }, with).ObservedThroughputKbps);
        Assert.Null(PlaybackProtocol.BufferingQualityFields(request with { BandwidthEstimateSource = "probe" }, with).ObservedThroughputKbps);
        var tiny = PlaybackProtocol.BufferingQualityFields(request with { BandwidthEstimateKbps = 50 }, with);
        Assert.Null(tiny.BandwidthEstimateKbps);
        Assert.Null(tiny.BandwidthEstimateSource);
        Assert.Null(tiny.ObservedThroughputKbps);
    }

    [Theory]
    [InlineData(BufferingQualityMode.Always)]
    [InlineData(BufferingQualityMode.Ask)]
    public async Task ALoweringReachesTheWireWithItsEvidence(BufferingQualityMode mode)
    {
        var stub = new StubHandler().Enqueue(SpeedCapabilities).Enqueue(HttpStatusCode.Created, Decision).Enqueue(Decision);
        using var client = Create(stub);
        client.Settings.StoreBufferingQuality(new(mode, Supported: true));
        var start = Start();
        await client.Playback.StartAsync(start, new Installation());

        await client.Playback.ReplanAsync("session", PlaybackReplan.ForQualityChange(start, Plan, "plan-attempt", [], 1, 30, Lowering),
            new Installation());
        var body = Body(stub, 2);
        Assert.Equal("quality_change", Text(body, "operation"));
        Assert.Equal("auto", Text(body, "quality_preference"));
        Assert.Equal("44100", Text(body, "bandwidth_estimate_kbps"));
        Assert.Equal("observed_rebuffer", Text(body, "bandwidth_estimate_source"));
        Assert.Equal("30000", Text(body, "observed_throughput_kbps"));
        Assert.Null(Text(body, "learned_limit"));
        Assert.Contains(ConnectionSpeed.BufferingQualityFeature, body.GetProperty("client_features").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task RestoreQualitySendsClearAndNoEstimate()
    {
        var stub = new StubHandler().Enqueue(SpeedCapabilities).Enqueue(HttpStatusCode.Created, Decision).Enqueue(Decision);
        using var client = Create(stub);
        client.Settings.StoreBufferingQuality(new(BufferingQualityMode.Always, Supported: true));
        var start = Start();
        await client.Playback.StartAsync(start, new Installation());

        var restore = new BufferingQualityReplan("quality_change", "auto") { LearnedLimit = "clear" };
        await client.Playback.ReplanAsync("session", PlaybackReplan.ForQualityChange(start, Plan, "plan-attempt", [], 1, 30, restore),
            new Installation());
        var body = Body(stub, 2);
        Assert.Equal("clear", Text(body, "learned_limit"));
        Assert.Null(Text(body, "bandwidth_estimate_kbps"));
        Assert.Null(Text(body, "bandwidth_estimate_source"));
    }

    [Fact]
    public async Task WithoutTheFeatureALoweringSendsOnlyTheEstimate()
    {
        // A metered link leaves buffering_quality_v1 out of the start, so its replans may not name the new fields.
        var stub = new StubHandler().Enqueue(SpeedCapabilities).Enqueue(HttpStatusCode.Created, Decision).Enqueue(Decision);
        using var client = Create(stub);
        client.Playback.Speed.Metered = true;
        var start = Start();
        await client.Playback.StartAsync(start, new Installation());

        await client.Playback.ReplanAsync("session", PlaybackReplan.ForQualityChange(start, Plan, "plan-attempt", [], 1, 30, Lowering),
            new Installation());
        var body = Body(stub, 2);
        Assert.Equal("44100", Text(body, "bandwidth_estimate_kbps"));
        Assert.Null(Text(body, "bandwidth_estimate_source"));
        Assert.Null(Text(body, "observed_throughput_kbps"));
    }

    // PlanThroughput follows silo-apple UpstreamReadMeter.swift: ~1 s windows that saw bytes count whole.

    /// <summary>Feeds the meter every 100 ms from <paramref name="fromMs"/> to <paramref name="toMs"/>, bytes from <paramref name="bytesAt"/>.</summary>
    private static void Tick(PlanThroughput meter, object source, long fromMs, long toMs, Func<long, long> bytesAt)
    {
        for (var t = fromMs; t <= toMs; t += 100) meter.Observe(source, bytesAt(t), t);
    }

    [Fact]
    public void ThroughputOfAFlatOutReaderIsItsRate()
    {
        var meter = new PlanThroughput();
        var stream = new object();
        // 10 Mbps: 125,000 bytes every 100 ms.
        Tick(meter, stream, 0, 900, t => t * 1_250);
        Assert.Null(meter.Kbps);
        Tick(meter, stream, 1_000, 3_000, t => t * 1_250);
        Assert.Equal(10_000, meter.Kbps);
        Assert.Equal(3_750_000, meter.Bytes);
    }

    [Fact]
    public void ThroughputOfAParkedReaderReadsLowNotAtBurstRate()
    {
        var meter = new PlanThroughput();
        var stream = new object();
        // One 1 MiB block lands within a single 100 ms tick every 2 s and the reader parks: burst rate ~84 Mbps.
        const long block = 1_048_576;
        Tick(meter, stream, 0, 10_000, t => (t + 1_900) / 2_000 * block);
        // Only the windows that saw a block count, each for its whole second: 8 Mbit/s, a tenth of the burst.
        Assert.Equal(8_388, meter.Kbps);
        Assert.Equal(5 * block, meter.Bytes);
    }

    [Fact]
    public void ThroughputChargesAWindowItsWholeLength()
    {
        var meter = new PlanThroughput();
        var stream = new object();
        meter.Observe(stream, 0, 0);
        meter.Observe(stream, 1_000_000, 700);
        // The window closes at the first call 1 s on, here 1.4 s: its bytes are charged all of it.
        meter.Observe(stream, 1_000_000, 1_400);
        Assert.Equal(5_714, meter.Kbps);
    }

    [Fact]
    public void ThroughputLeavesOutAWindowLongerThanThreeSeconds()
    {
        var meter = new PlanThroughput();
        var stream = new object();
        Tick(meter, stream, 0, 1_000, t => t * 1_250);
        Assert.Equal(10_000, meter.Kbps);
        // The UI thread was held up for 4 s: what arrived then took an unknown time.
        meter.Observe(stream, 1_250_000 + 50_000_000, 5_000);
        Assert.Equal(10_000, meter.Kbps);
        Assert.Equal(1_250_000, meter.Bytes);
        // The next window measures again from there.
        Tick(meter, stream, 5_100, 6_000, t => 51_250_000 + (t - 5_000) * 2_500);
        Assert.Equal(15_000, meter.Kbps);
    }

    [Fact]
    public void ThroughputNeedsOneSecondOfReceiving()
    {
        var meter = new PlanThroughput();
        var stream = new object();
        // Two windows with no bytes count no time, and a partial window counts nothing.
        Tick(meter, stream, 0, 2_000, _ => 0);
        Tick(meter, stream, 2_100, 2_900, t => (t - 2_000) * 10_000);
        Assert.Null(meter.Kbps);
        Assert.Equal(0, meter.Bytes);
    }

    [Fact]
    public void ThroughputTakesTheFirstReadingAsABaselineAndANewMountFromZero()
    {
        var meter = new PlanThroughput();
        var first = new object();
        // Bytes before the plan's first call took an unknown time.
        meter.Observe(first, 5_000_000, 0);
        meter.Observe(first, 5_500_000, 500);
        // A seek remount mid-window: the new stream's counters began at zero with it.
        var second = new object();
        meter.Observe(second, 750_000, 1_000);
        Assert.Equal(1_250_000, meter.Bytes);
        Assert.Equal(10_000, meter.Kbps);
        meter.Reset();
        Assert.Null(meter.Kbps);
        Assert.Equal(0, meter.Bytes);
        meter.Observe(second, 9_000_000, 2_000);
        meter.Observe(second, 9_000_000, 3_000);
        Assert.Equal(0, meter.Bytes);
    }

    [Fact]
    public void ThroughputRestartsFromCountersThatGoBack()
    {
        var meter = new PlanThroughput();
        var stream = new object();
        meter.Observe(stream, 0, 0);
        meter.Observe(stream, 500_000, 400);
        // A reused stream object starts over: no negative bytes, and what the window had stays.
        meter.Observe(stream, 100_000, 600);
        meter.Observe(stream, 850_000, 1_000);
        Assert.Equal(1_250_000, meter.Bytes);
        Assert.Equal(10_000, meter.Kbps);
    }

    [Fact]
    public void ThroughputOutsideTheBoundsIsNullOrCapped()
    {
        var meter = new PlanThroughput();
        var stream = new object();
        // Below 100 kbps measures nothing.
        meter.Observe(stream, 0, 0);
        meter.Observe(stream, 10, 1_000);
        Assert.Null(meter.Kbps);
        meter.Reset();
        // Above 1,000,000 is capped.
        meter.Observe(stream, 0, 0);
        meter.Observe(stream, 1_000_000_000, 1_000);
        Assert.Equal(1_000_000, meter.Kbps);
    }

    private static readonly BufferingPlanSummary Summary = new("adoption-1", 125_000, 2, 9_000, 6_000, 63_000,
        BufferingQualityMode.Always, BufferingAction.Lowered, 44_100) { ThroughputKbps = 30_000 };

    private static PlanSummaryContext Context(params string[] features) =>
        new("attempt-0123456789", "session", "plan-0123456789", "plan-attempt-1", "v3:0123456789abcdef", features)
        {
            AppliedQuirkIds = ["quirk-a"],
            QuirkRegistryRevision = "r1",
        };

    [Fact]
    public void TheSummaryDiagnosticsAreTheAllowlistedStrings()
    {
        Assert.Equal(new Dictionary<string, string>
        {
            ["played_ms"] = "125000",
            ["stall_count"] = "2",
            ["stall_total_ms"] = "9000",
            ["stall_longest_ms"] = "6000",
            ["delivered_kbps"] = "63000",
            ["buffering_mode"] = "always",
            ["buffering_action"] = "lowered",
            ["bandwidth_estimate_kbps"] = "44100",
            ["throughput_kbps"] = "30000",
        }, BufferingQualityWire.Diagnostics(Summary));
        var bare = BufferingQualityWire.Diagnostics(Summary with { DeliveredKbps = null, BandwidthEstimateKbps = null, ThroughputKbps = 50 });
        Assert.False(bare.ContainsKey("delivered_kbps"));
        Assert.False(bare.ContainsKey("bandwidth_estimate_kbps"));
        Assert.False(bare.ContainsKey("throughput_kbps"));
    }

    [Fact]
    public void ThePlanSummaryEventIsSentOnlyWhenTheServerAdvertisesIt()
    {
        Assert.Null(PlaybackRouteEvents.ForPlanSummary(Summary, Context("buffering_quality_v1"), "installation", "event-1"));
        var routeEvent = PlaybackRouteEvents.ForPlanSummary(Summary, Context(PlaybackProtocol.PlanSummaryFeature), "installation", "event-1")!;
        Assert.Equal("plan_summary", routeEvent.Event);
        Assert.Equal(3, routeEvent.ProtocolVersion);
        Assert.Equal("attempt-0123456789", routeEvent.PlaybackAttemptId);
        Assert.Equal("plan-0123456789", routeEvent.PlanId);
        Assert.Equal("plan-attempt-1", routeEvent.PlanAttemptId);

        var json = JsonDocument.Parse(PlaybackRouteEvents.Serialize(routeEvent)).RootElement;
        Assert.Equal(["applied_quirk_ids", "diagnostics", "event", "event_id", "installation_id", "plan_attempt_id", "plan_attempt_key",
            "plan_id", "playback_attempt_id", "protocol_version", "quirk_registry_revision", "session_id"],
            json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("lowered", json.GetProperty("diagnostics").GetProperty("buffering_action").GetString());
    }

    [Fact]
    public async Task TheRouteEventPostsToTheRouteEventsEndpoint()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.Accepted, """{"event_id":"event-1","outcome":"accepted"}""");
        using var client = Create(stub);
        var routeEvent = PlaybackRouteEvents.ForPlanSummary(Summary, Context(PlaybackProtocol.PlanSummaryFeature), "installation", "event-1")!;
        Assert.True(await client.Playback.ReportRouteEventAsync(routeEvent));
        Assert.Equal(HttpMethod.Post, stub.Requests[0].Method);
        Assert.Equal("/api/v2/playback/route-events", stub.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("event-1", JsonDocument.Parse(stub.RequestBodies[0]!).RootElement.GetProperty("event_id").GetString());

        var refused = new StubHandler().Enqueue(HttpStatusCode.TooManyRequests, """{"title":"slow down"}""");
        using var other = Create(refused);
        Assert.False(await other.Playback.ReportRouteEventAsync(routeEvent));
    }
}

public sealed class BufferingQualityPlansTests
{
    [Fact]
    public void ThePlanMapsDeliveredBitrateRungsAndWarnings()
    {
        var plan = new Bale.Core.Api.Models.PlaybackPlan
        {
            PlanId = "plan-0123456789",
            Source = new() { BitrateKbps = 63_000 },
            EffectiveRecipe = new() { BitrateKbps = 9_800 },
            ActiveQuality = "1080p-high",
            AvailableQualities =
            [
                new() { Label = "original", BitrateKbps = 63_000 }, new() { Label = "1080p-high", BitrateKbps = 10_000 },
                new() { Label = "broken" }, new() { Label = "720p-high", BitrateKbps = 4_000 },
            ],
            DegradationWarnings = [new() { Code = "quality_reduction_unavailable" }],
        };
        var mapped = BufferingQualityPlans.From(plan, "adoption-1", "auto", ["plan_active_quality_v1"], 44_100);
        Assert.Equal("adoption-1", mapped.Id);
        Assert.Equal(9_800, mapped.DeliveredKbps);
        Assert.True(mapped.IsAuto);
        Assert.Equal(["original", "1080p-high", "720p-high"], mapped.AvailableQualities.Select(rung => rung.Label));
        Assert.True(mapped.QualityReductionUnavailable);
        Assert.Equal("1080p-high", mapped.ActiveQuality);
        Assert.Equal(44_100, mapped.RequestedBandwidthEstimateKbps);

        // Without the feature the server's active_quality is not a report; without a recipe the source's bitrate plays.
        var bare = BufferingQualityPlans.From(new() { Source = new() { BitrateKbps = 63_000 }, ActiveQuality = "x" }, "a2", null, [], null);
        Assert.Null(bare.ActiveQuality);
        Assert.Equal(63_000, bare.DeliveredKbps);
        Assert.Equal("auto", bare.Quality);
        Assert.Empty(bare.AvailableQualities);
        Assert.Null(BufferingQualityPlans.From(new(), "a3", "auto", null, null).DeliveredKbps);
    }

    [Fact]
    public async Task TheStartsEstimateAndNegotiatedFeaturesAreKeptForItsAttempt()
    {
        const string capabilities = """
            {"installation_id":"installation","state":"available","allowed":true,"protocol_versions":[3],
             "features":["sequenced_progress_v1","buffering_quality_v1","connection_speed_v1"],"deliveries":["original_http","hls"],"revision":"1",
             "bandwidth_probe_url":"/api/v2/playback/bandwidth-probe","bandwidth_probe_max_age_seconds":604800}
            """;
        var stub = new StubHandler().Enqueue(capabilities).Enqueue(System.Net.HttpStatusCode.Created, PlaybackTestSupport.Decision);
        using var client = PlaybackTestSupport.Create(stub);
        var start = PlaybackTestSupport.Start();
        await client.Playback.StartAsync(start, new PlaybackTestSupport.Installation());
        Assert.True(client.Playback.Negotiated(start.PlaybackAttemptId, ConnectionSpeed.BufferingQualityFeature));
        Assert.False(client.Playback.Negotiated("another-attempt", ConnectionSpeed.BufferingQualityFeature));
        Assert.Null(client.Playback.StartEstimateKbps(start.PlaybackAttemptId));
    }
}
