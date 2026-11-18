using System.Net;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

/// <summary>docs/apple-protocol-parity.md: the protocol fields Bale took over from the Apple TV client.</summary>
public class AppleProtocolParityTests
{
    private const string SpeedCapabilities = """
        {"installation_id":"installation","state":"available","allowed":true,"protocol_versions":[3],
         "features":["sequenced_progress_v1","buffering_quality_v1","connection_speed_v1","subrip_sidecar_v1"],
         "deliveries":["original_http","hls"],"revision":"1",
         "bandwidth_probe_url":"/api/v2/playback/bandwidth-probe","bandwidth_probe_max_age_seconds":604800}
        """;

    private static readonly PlaybackPlan Plan = new()
    {
        PlanId = "plan-0123456789", PlanAttemptKey = "v3:0123456789abcdef", Delivery = "original_http",
    };

    private static string[] Features(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("client_features").EnumerateArray().Select(value => value.GetString()!).ToArray();

    [Fact]
    public void StartsAndEveryReplanDeclareHeaderAuthenticatedMedia()
    {
        var start = Start();
        Assert.Contains(PlaybackProtocol.HeaderAuthenticatedMediaFeature, start.ClientFeatures);
        Assert.Contains(PlaybackProtocol.HeaderAuthenticatedMediaFeature,
            PlaybackReplan.ForSeek(start, Plan, "plan-attempt", [], 1, 30).ClientFeatures);
        Assert.Contains(PlaybackProtocol.HeaderAuthenticatedMediaFeature,
            PlaybackReplan.ForTrackChange(start, Plan, "plan-attempt", [], 1, 30, 1, null).ClientFeatures);
        Assert.Contains(PlaybackProtocol.HeaderAuthenticatedMediaFeature,
            PlaybackReplan.ForFailure(start, Plan, "plan-attempt", [], 1, 30, new("decoder_failure")).ClientFeatures);
    }

    [Fact]
    public void AFailedSoftwareRouteKeepsHeaderAuthenticatedMedia()
    {
        // The single replan after a failed FFmpeg software mount rebuilds the features in Rust.
        var route = new PlaybackRouteDecision(true, true, "av1", "aac", 0, 1920, 1080);
        var replan = PlaybackReplan.ForFailure(Start(), Plan, "plan-attempt", [], 1, 30, new("decoder_failure"), route);
        Assert.DoesNotContain(PlaybackProtocol.SoftwareVideoDecodeFeature, replan.ClientFeatures);
        Assert.Contains(PlaybackProtocol.HeaderAuthenticatedMediaFeature, replan.ClientFeatures);
    }

    [Fact]
    public async Task AReplanRepeatsWhatItsStartNegotiated()
    {
        var stub = new StubHandler().Enqueue(SpeedCapabilities).Enqueue(HttpStatusCode.Created, Decision).Enqueue(Decision);
        using var client = Create(stub);
        client.Playback.Speed.EstimatesEnabled = true;
        var start = Start();

        await client.Playback.StartAsync(start, new Installation());
        // The player's replans are built from its own start request, which never held the negotiated features.
        Assert.DoesNotContain(ConnectionSpeed.BufferingQualityFeature, start.ClientFeatures);
        await client.Playback.ReplanAsync("session", PlaybackReplan.ForTrackChange(start, Plan, "plan-attempt", [], 1, 30, 1, null),
            new Installation());

        var started = Features(stub.RequestBodies[1]!);
        var replanned = Features(stub.RequestBodies[2]!);
        Assert.Contains(ConnectionSpeed.BufferingQualityFeature, replanned);
        Assert.Contains(PlaybackProtocol.SubripSidecarFeature, replanned);
        Assert.Equal(started.Order(), replanned.Order());
        // Replans still never carry an estimate: the server fills in the connection's reading itself.
        Assert.False(JsonDocument.Parse(stub.RequestBodies[2]!).RootElement.TryGetProperty("bandwidth_estimate_source", out _));
    }

    [Fact]
    public async Task AnotherAttemptsReplanGetsNothingNegotiated()
    {
        var stub = new StubHandler().Enqueue(SpeedCapabilities).Enqueue(HttpStatusCode.Created, Decision).Enqueue(Decision);
        using var client = Create(stub);
        client.Playback.Speed.EstimatesEnabled = true;
        var start = Start();
        await client.Playback.StartAsync(start, new Installation());

        var other = start with { PlaybackAttemptId = "other-attempt" };
        await client.Playback.ReplanAsync("session", PlaybackReplan.ForSeek(other, Plan, "plan-attempt", [], 1, 30), new Installation());

        Assert.Equal(other.ClientFeatures, Features(stub.RequestBodies[2]!));
    }

    [Fact]
    public async Task AStartThatNegotiatedNothingAddsNothingToItsReplans()
    {
        var stub = new StubHandler().Enqueue(Capabilities).Enqueue(HttpStatusCode.Created, Decision).Enqueue(Decision);
        using var client = Create(stub);
        var start = Start();
        await client.Playback.StartAsync(start, new Installation());
        await client.Playback.ReplanAsync("session", PlaybackReplan.ForSeek(start, Plan, "plan-attempt", [], 1, 30), new Installation());

        Assert.Equal(start.ClientFeatures, Features(stub.RequestBodies[2]!));
    }

    [Fact]
    public void AnXboxNamesItsMakerAndConsoleModel()
    {
        var device = Start().ClientPlaybackContext.Device;
        Assert.Equal("xbox", device.Platform);
        Assert.Equal("Microsoft", device.Manufacturer);
        Assert.Equal("Test Xbox", device.Model);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(Start(), PlaybackJson.Default.PlaybackStartRequestV3)).RootElement
            .GetProperty("client_playback_context").GetProperty("device");
        Assert.Equal("Microsoft", json.GetProperty("manufacturer").GetString());
        Assert.Equal("Test Xbox", json.GetProperty("model").GetString());
    }

    [Fact]
    public void APcNameIsNeverSentAsAModel()
    {
        var device = PlaybackStartBuilder.DeviceContext(Identity with { Platform = "windows", DeviceName = "Someone's PC", DeviceModel = "Some PC" });
        Assert.Equal("windows", device.Platform);
        Assert.Null(device.Manufacturer);
        Assert.Null(device.Model);
    }

    [Fact]
    public void ModelsStayWithinTheContractBound()
    {
        Assert.Equal(128, PlaybackStartBuilder.DeviceContext(Identity with { DeviceModel = new string('x', 300) }).Model!.Length);
        Assert.Null(PlaybackStartBuilder.DeviceContext(Identity with { DeviceModel = "  " }).Model);
    }
}
