using System.Net;
using System.Text.Json;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class ConnectionSpeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConnectionSpeedSchedule Schedule = new(new Uri("https://silo.test/api/v2/playback/bandwidth-probe"),
        115_000, true, TimeSpan.FromDays(7));

    private const string SpeedCapabilities = """
        {"installation_id":"installation","state":"available","allowed":true,"protocol_versions":[3],
         "features":["sequenced_progress_v1","buffering_quality_v1","connection_speed_v1"],"deliveries":["original_http","hls"],"revision":"1",
         "bandwidth_probe_url":"/api/v2/playback/bandwidth-probe","bandwidth_probe_target_kbps":115000,
         "bandwidth_probe_automatic":true,"bandwidth_probe_max_age_seconds":604800}
        """;

    [Fact]
    public void ASettledCurrentProbeProvesTheLinkAndALowerBoundOnlyAtTheTarget()
    {
        var settled = new ConnectionStateV2 { Probe = new(48_000, false, Now.AddDays(-1), "wifi") };
        Assert.True(ConnectionSpeedPolicy.ProvesLink(settled, Schedule, Now));
        var lowBound = new ConnectionStateV2 { Probe = new(100_000, true, Now.AddHours(-1), "ethernet") };
        Assert.False(ConnectionSpeedPolicy.ProvesLink(lowBound, Schedule, Now));
        Assert.True(ConnectionSpeedPolicy.ProvesLink(lowBound with { Probe = lowBound.Probe! with { Kbps = 115_000 } }, Schedule, Now));
        // A playback sample is a lower bound too.
        Assert.True(ConnectionSpeedPolicy.ProvesLink(new() { PlaybackSample = new(120_000, Now) }, Schedule, Now));
        // Without a target any current reading proves the link.
        Assert.True(ConnectionSpeedPolicy.ProvesLink(lowBound, Schedule with { TargetKbps = null }, Now));
        // A stale reading proves nothing and is never sent.
        var stale = new ConnectionStateV2 { Probe = new(48_000, false, Now.AddDays(-8), "wifi") };
        Assert.False(ConnectionSpeedPolicy.ProvesLink(stale, Schedule, Now));
        Assert.Null(ConnectionSpeedPolicy.Estimate(stale, Schedule, Now));
    }

    [Fact]
    public void StartsSendTheHighestCurrentReading()
    {
        var state = new ConnectionStateV2 { Probe = new(48_000, false, Now.AddDays(-1), "wifi"), PlaybackSample = new(51_200, Now.AddHours(-2)) };
        Assert.Equal(51_200, ConnectionSpeedPolicy.Estimate(state, Schedule, Now));
        Assert.Null(ConnectionSpeedPolicy.Estimate(state, null, Now));
    }

    [Fact]
    public void AutomaticTestsHonourTheServerSwitchAndA429Hold()
    {
        var none = new ConnectionStateV2();
        Assert.True(ConnectionSpeedPolicy.AutomaticTestDue(none, Schedule, Now, null));
        Assert.False(ConnectionSpeedPolicy.AutomaticTestDue(none, Schedule with { Automatic = false }, Now, null));
        Assert.False(ConnectionSpeedPolicy.AutomaticTestDue(none, Schedule, Now, Now.AddSeconds(5)));
        Assert.True(ConnectionSpeedPolicy.AutomaticTestDue(none, Schedule, Now, Now.AddSeconds(-1)));
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("nonsense", 30)]
    [InlineData("0", 1)]
    [InlineData("45", 45)]
    public void RetryAfterDefaultsTo30SecondsAndNeverUnderOne(string? header, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ConnectionSpeed.RetryAfter(header, Now));

    [Theory]
    [InlineData(850, "850 kbps")]
    [InlineData(9_990, "9.9 Mbps")]
    [InlineData(48_000, "48 Mbps")]
    [InlineData(115_999, "115 Mbps")]
    public void SettingsRoundRatesDown(int kbps, string text) => Assert.Equal(text, ConnectionSpeedPolicy.FormatRate(kbps));

    [Fact]
    public async Task AStartDeclaresBufferingQualityAndSendsTheProbeEstimate()
    {
        var stub = new StubHandler().Enqueue(SpeedCapabilities).Enqueue(HttpStatusCode.Created, Decision);
        using var client = Create(stub);
        client.Playback.Speed.Update(null, new() { Probe = new(48_000, false, DateTimeOffset.UtcNow.AddHours(-1), "wifi") });

        await client.Playback.StartAsync(Start(), new Installation());

        using var json = JsonDocument.Parse(stub.RequestBodies[1]!);
        var body = json.RootElement;
        Assert.Contains("buffering_quality_v1", body.GetProperty("client_features").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(48_000, body.GetProperty("bandwidth_estimate_kbps").GetInt32());
        Assert.Equal("probe", body.GetProperty("bandwidth_estimate_source").GetString());
        Assert.Equal("/api/v2/playback/bandwidth-probe", client.Playback.Speed.Schedule!.ProbeUrl.AbsolutePath);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task NoEstimateWithoutTheServerFeatureOrOnAMeteredLink(bool offered, bool metered)
    {
        var stub = new StubHandler().Enqueue(offered ? SpeedCapabilities : Capabilities).Enqueue(HttpStatusCode.Created, Decision);
        using var client = Create(stub);
        client.Playback.Speed.Metered = metered;
        client.Playback.Speed.Update(null, new() { Probe = new(48_000, false, DateTimeOffset.UtcNow.AddHours(-1), "wifi") });

        await client.Playback.StartAsync(Start(), new Installation());

        using var json = JsonDocument.Parse(stub.RequestBodies[1]!);
        Assert.False(json.RootElement.TryGetProperty("bandwidth_estimate_kbps", out _));
        Assert.DoesNotContain("buffering_quality_v1", json.RootElement.GetProperty("client_features").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void AProbeOnAnotherHostIsNeverUsed()
    {
        using var client = Create(new StubHandler());
        var caps = new Bale.Core.Api.Models.PlaybackCapabilities
        {
            Features = ["connection_speed_v1", "buffering_quality_v1"], BandwidthProbeUrl = "https://elsewhere.example.net/api/v2/playback/bandwidth-probe",
        };
        Assert.Null(client.Playback.Schedule(caps));
        caps.BandwidthProbeUrl = "/api/v2/playback/bandwidth-probe";
        Assert.NotNull(client.Playback.Schedule(caps));
    }
}
