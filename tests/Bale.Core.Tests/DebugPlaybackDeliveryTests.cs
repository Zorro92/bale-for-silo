using System.Net;
using System.Text.Json;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class DebugPlaybackDeliveryTests
{
    [Fact]
    public void SwitchDefaultsToAutoAndRejectsInvalidValuesWithoutChangingIt()
    {
        var previous = DebugPlaybackDelivery.Mode;
        try
        {
            Assert.Equal("auto", previous);
            Assert.True(DebugPlaybackDelivery.TrySet("hls"));
            Assert.Equal("hls", DebugPlaybackDelivery.Mode);
            foreach (var invalid in new string?[] { null, "", "HLS", "off", "hls ", " hls", "hls&mode=auto" })
            {
                Assert.False(DebugPlaybackDelivery.TrySet(invalid));
                Assert.Equal("hls", DebugPlaybackDelivery.Mode);
            }
            Assert.True(DebugPlaybackDelivery.TrySet("auto"));
            Assert.Equal("auto", DebugPlaybackDelivery.Mode);
        }
        finally { DebugPlaybackDelivery.TrySet(previous); }
    }

    [Fact]
    public void HlsDisablesOnlyOtherDeliveriesAndAutoLeavesTheRequestUntouched()
    {
        var start = Start(new(AudioIndex: 1), 0);
        // Progressive is disabled today, but must not become an escape from a forced HLS attempt.
        start = start with
        {
            ClientPlaybackContext = start.ClientPlaybackContext with
            {
                Deliveries = start.ClientPlaybackContext.Deliveries.ToDictionary(pair => pair.Key, pair => pair.Key == "progressive"
                    ? pair.Value with { Enabled = true, SupportedOnDevice = true } : pair.Value),
            },
        };
        var forced = DebugPlaybackDelivery.Apply("hls", start);
        Assert.NotSame(start, forced);
        Assert.Equal(start, forced with { ClientPlaybackContext = start.ClientPlaybackContext });
        Assert.Equal(start.ClientPlaybackContext, forced.ClientPlaybackContext with { Deliveries = start.ClientPlaybackContext.Deliveries });
        foreach (var pair in start.ClientPlaybackContext.Deliveries)
        {
            var actual = forced.ClientPlaybackContext.Deliveries[pair.Key];
            if (pair.Key == "hls") Assert.Same(pair.Value, actual);
            else Assert.Equal(pair.Value with { Enabled = false, SupportedOnDevice = false }, actual);
        }
        Assert.True(start.ClientPlaybackContext.Deliveries["original_http"].Enabled);
        Assert.True(start.ClientPlaybackContext.Deliveries["progressive"].Enabled);
        Assert.Same(start, DebugPlaybackDelivery.Apply("auto", start));
        // Recovery/reanchor reuse the captured context even after the in-memory switch is reset.
        Assert.False(forced.ClientPlaybackContext.Deliveries["original_http"].Enabled);
    }

    [Theory]
    [InlineData("original_http", "auto", false)]
    [InlineData("server_remux_progressive", "auto", true)]
    [InlineData("server_remux_hls", "hls", false)]
    [InlineData("server_transcode_hls", "hls", true)]
    public async Task ExplicitStartOverIsZeroOnTheWireBeforeEveryDelivery(string delivery, string mode, bool suppressRewind)
    {
        var response = Decision.TrimEnd()[..^1] + ",\"playback_plan\":{\"delivery\":\"" + delivery + "\"}}";
        var stub = new StubHandler().Enqueue(Capabilities).Enqueue(HttpStatusCode.Created, response);
        using var client = Create(stub);
        var position = PlaybackStartPosition.ForStartRequest(0, 1560, suppressRewind ? 0 : PlaybackStartPosition.DefaultResumeRewindSeconds);
        var start = DebugPlaybackDelivery.Apply(mode, Start(new(AudioIndex: 1), position));
        var decision = await client.Playback.StartAsync(start, new Installation());
        Assert.Equal(delivery, decision.PlaybackPlan!.Delivery);
        using var json = JsonDocument.Parse(stub.RequestBodies[1]!);
        var body = json.RootElement;
        Assert.Equal(0, body.GetProperty("start_position").GetDouble());
        Assert.Equal(1, body.GetProperty("audio_track_index").GetInt32());
        Assert.Equal(mode == "auto", body.GetProperty("client_playback_context").GetProperty("deliveries")
            .GetProperty("original_http").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void SwitchAndEveryPlayerReferenceAreCompiledOutWithoutDebugServer()
    {
        var source = Read("DebugPlaybackDelivery.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER\n", source);
        Assert.EndsWith("#endif\n", source);
        Assert.DoesNotContain("Settings", source);
        Assert.DoesNotContain("Storage", source);
        var player = Read("PlayerPage.cs.txt");
        foreach (var marker in new[]
        {
            "public string DeliveryMode { get; } = DebugPlaybackDelivery.Mode;",
            "start = DebugPlaybackDelivery.Apply(attempt.DeliveryMode, start);",
            "Log.Info(\"player: delivery forced=\" + attempt.DeliveryMode)",
        })
        {
            var index = player.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index >= 0);
            Assert.True(player.LastIndexOf("#if DEBUG_SERVER", index, StringComparison.Ordinal)
                > player.LastIndexOf("#endif", index, StringComparison.Ordinal));
        }
        Assert.Contains("attempt.Start = start;", player);
        var server = Read("DebugServer.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", server);
        Assert.Contains("case (\"GET\", \"/delivery\"):", server);
        Assert.Contains("case (\"POST\", \"/delivery\"):", server);
        Assert.Contains("query.Count != 1 || !DebugPlaybackDelivery.TrySet(deliveryMode)", server);
        var duplicateGuard = server.Split('\n').Single(line => line.Contains("duplicateQueryKeys && path is", StringComparison.Ordinal));
        Assert.Contains("\"/delivery\"", duplicateGuard);
        var script = Read("bale-debug.sh");
        Assert.Contains("scripts/bale-debug.sh delivery [hls|progressive|auto]", script);
        Assert.Contains("\"$BASE/delivery\" --data-urlencode \"mode=$2\"", script);
        // The shipping configuration removes DEBUG_SERVER, even though CI's development MSIX
        // uses Configuration=Release with BaleDebugServer=true like the other diagnostic switches.
        Assert.Contains("<DefineConstants Condition=\"'$(BaleDebugServer)' == 'true'\">$(DefineConstants);DEBUG_SERVER</DefineConstants>",
            Read("Bale.Xbox.csproj.txt"));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
