using System.Net;
using System.Text.Json;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class InactiveClientTransformationTests
{
    private static readonly PlaybackTransformation Dv7 = new(PlaybackProtocol.ClientDolbyVision7ToHdr10, "client");

    private static PlaybackStartRequestV3 WithOriginal(PlaybackStartRequestV3 start, bool enabled)
    {
        var context = start.ClientPlaybackContext;
        return start with
        {
            ClientPlaybackContext = context with
            {
                Deliveries = context.Deliveries.ToDictionary(pair => pair.Key, pair => pair.Key == "original_http"
                    ? pair.Value with { Enabled = enabled, SupportedOnDevice = enabled, Transformations = [Dv7, new("server_strip", "server")] }
                    : pair.Value),
            },
        };
    }

    private static string[] SentTransformations(string body)
    {
        using var json = JsonDocument.Parse(body);
        var original = json.RootElement.GetProperty("client_playback_context").GetProperty("deliveries").GetProperty("original_http");
        return original.TryGetProperty("transformations", out var list)
            ? list.EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray()
            : [];
    }

    [Fact]
    public async Task ADisabledDeliveryNeverSendsAClientTransformation()
    {
        // silo-server answers 422 for a client recipe on a disabled delivery (debug force, audio-mapping replan).
        var stub = new StubHandler().Enqueue(Capabilities).Enqueue(HttpStatusCode.Created, Decision);
        using var client = Create(stub);
        await client.Playback.StartAsync(WithOriginal(Start(), enabled: false), new Installation());
        Assert.Equal(["server_strip"], SentTransformations(stub.RequestBodies[1]!));
    }

    [Fact]
    public async Task AnEnabledDeliveryKeepsItsClientTransformation()
    {
        var stub = new StubHandler().Enqueue(Capabilities).Enqueue(HttpStatusCode.Created, Decision);
        using var client = Create(stub);
        await client.Playback.StartAsync(WithOriginal(Start(), enabled: true), new Installation());
        Assert.Contains(PlaybackProtocol.ClientDolbyVision7ToHdr10, SentTransformations(stub.RequestBodies[1]!));
    }
}
