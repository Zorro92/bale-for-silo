using System.Net;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Core.Settings;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

/// <summary><c>learned_limit</c> on starts and replans (apple-protocol-parity.md, "Request fields").</summary>
public class LearnedLimitRequestTests
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

    private static string? LearnedLimit(JsonElement body) =>
        body.TryGetProperty("learned_limit", out var value) ? value.GetString() : null;

    [Fact]
    public void OnlyNeverIgnoresAndOnlyWithTheFeature()
    {
        string[] with = ["playback_plan_v3", ConnectionSpeed.BufferingQualityFeature];
        Assert.Equal("ignore", PlaybackProtocol.LearnedLimit(with, BufferingQualityMode.Never));
        Assert.Null(PlaybackProtocol.LearnedLimit(with, BufferingQualityMode.Ask));
        Assert.Null(PlaybackProtocol.LearnedLimit(with, BufferingQualityMode.Always));
        // An explicit choice (Restore quality's clear) wins over the mode.
        Assert.Equal("clear", PlaybackProtocol.LearnedLimit(with, BufferingQualityMode.Never, PlaybackProtocol.LearnedLimitClear));
        // The server refuses the field without buffering_quality_v1.
        Assert.Null(PlaybackProtocol.LearnedLimit(["playback_plan_v3"], BufferingQualityMode.Never));
        Assert.Null(PlaybackProtocol.LearnedLimit(["playback_plan_v3"], BufferingQualityMode.Always, PlaybackProtocol.LearnedLimitClear));
    }

    [Theory]
    [InlineData(BufferingQualityMode.Never, "ignore")]
    [InlineData(BufferingQualityMode.Ask, null)]
    [InlineData(BufferingQualityMode.Always, null)]
    public async Task StartsAndReplansCarryTheModesLearnedLimit(BufferingQualityMode mode, string? expected)
    {
        var stub = new StubHandler().Enqueue(SpeedCapabilities).Enqueue(HttpStatusCode.Created, Decision).Enqueue(Decision).Enqueue(Decision);
        using var client = Create(stub);
        client.Settings.StoreBufferingQuality(new(mode, Supported: true));
        var start = Start();

        await client.Playback.StartAsync(start, new Installation());
        await client.Playback.ReplanAsync("session", PlaybackReplan.ForTrackChange(start, Plan, "plan-attempt", [], 1, 30, 1, null),
            new Installation());
        await client.Playback.ReplanAsync("session", PlaybackReplan.ForSeek(start, Plan, "plan-attempt", [], 2, 30), new Installation());

        Assert.Equal(expected, LearnedLimit(Body(stub, 1)));
        Assert.Equal(expected, LearnedLimit(Body(stub, 2)));
        Assert.Equal(expected, LearnedLimit(Body(stub, 3)));
    }

    [Fact]
    public async Task AStartWithoutBufferingQualitySendsNoLearnedLimit()
    {
        // A metered link (or a server without the feature) leaves buffering_quality_v1 out, so the field must go too.
        var stub = new StubHandler().Enqueue(SpeedCapabilities).Enqueue(HttpStatusCode.Created, Decision).Enqueue(Decision);
        using var client = Create(stub);
        client.Playback.Speed.Metered = true;
        client.Settings.StoreBufferingQuality(new(BufferingQualityMode.Never, Supported: true));
        var start = Start();

        await client.Playback.StartAsync(start, new Installation());
        await client.Playback.ReplanAsync("session", PlaybackReplan.ForSeek(start, Plan, "plan-attempt", [], 1, 30), new Installation());

        Assert.Null(LearnedLimit(Body(stub, 1)));
        Assert.Null(LearnedLimit(Body(stub, 2)));
    }

    [Fact]
    public async Task AnExplicitClearRidesOnTheReplan()
    {
        var stub = new StubHandler().Enqueue(SpeedCapabilities).Enqueue(HttpStatusCode.Created, Decision).Enqueue(Decision);
        using var client = Create(stub);
        client.Settings.StoreBufferingQuality(new(BufferingQualityMode.Always, Supported: true));
        var start = Start();
        await client.Playback.StartAsync(start, new Installation());

        var restore = PlaybackReplan.ForSeek(start, Plan, "plan-attempt", [], 1, 30) with { LearnedLimit = PlaybackProtocol.LearnedLimitClear };
        await client.Playback.ReplanAsync("session", restore, new Installation());

        Assert.Equal("clear", LearnedLimit(Body(stub, 2)));
    }
}
