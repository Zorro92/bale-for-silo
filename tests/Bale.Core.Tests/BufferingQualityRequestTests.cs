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

    [Fact]
    public void ThroughputCountsReceivingTimeAcrossMountsAndNeedsOneSecond()
    {
        var meter = new PlanThroughput();
        var first = new object();
        meter.Observe(first, 1_000_000, TimeSpan.FromMilliseconds(500));
        Assert.Null(meter.Kbps);
        meter.Observe(first, 2_500_000, TimeSpan.FromMilliseconds(1_000));
        Assert.Equal(20_000, meter.Kbps);
        // A seek remount's new stream starts its counters at zero.
        meter.Observe(new object(), 2_500_000, TimeSpan.FromMilliseconds(1_000));
        Assert.Equal(20_000, meter.Kbps);
        Assert.Equal(5_000_000, meter.Bytes);
        meter.Reset();
        Assert.Null(meter.Kbps);
        // Below 100 kbps measures nothing; above 1,000,000 is capped.
        meter.Observe(first, 10, TimeSpan.FromSeconds(10));
        Assert.Null(meter.Kbps);
        meter.Reset();
        meter.Observe(first, 1_000_000_000, TimeSpan.FromSeconds(1));
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
