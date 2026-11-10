using System.Text.Json;
using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackCapabilitiesTests
{
    [Fact]
    public void DefaultAndParameterlessBuilderKeepTheMeasuredAv1Bound()
    {
        foreach (var claims in new[] { ConsoleEvidence.Default.VideoDecode, ConsoleEvidence.BuildVideoDecode() })
        {
            var av1 = Assert.Single(claims, decode => decode.Codec == "av1");
            // The server fails an empty or non-Main source profile, so those AV1 streams transcode.
            Assert.Equal(new[] { "main" }, av1.Profiles);
            Assert.Equal(1920, av1.MaxWidth);
            Assert.Equal(1080, av1.MaxHeight);
            Assert.Equal(30, av1.MaxFrameRate);
        }
    }

    [Fact]
    public void RaisedBoundChangesOnlyAv1DimensionsAndNeverMutatesTheDefault()
    {
        var claims = ConsoleEvidence.BuildVideoDecode(3840, 2160);
        Assert.Equal(new[] { "h264", "hevc", "av1" }, claims.Select(decode => decode.Codec));
        var av1 = Assert.Single(claims, decode => decode.Codec == "av1");
        Assert.Equal("dav1d", av1.DecoderName);
        Assert.Equal(new[] { "main" }, av1.Profiles);
        Assert.Empty(av1.Levels);
        Assert.Equal(new[] { 8, 10 }, av1.BitDepths);
        Assert.Equal(3840, av1.MaxWidth);
        Assert.Equal(2160, av1.MaxHeight);
        Assert.Equal(30, av1.MaxFrameRate);
        Assert.Null(av1.MaxBitrateKbps);
        Assert.False(av1.Hardware);
        Assert.All(claims.Where(decode => decode.Codec != "av1"), decode =>
        {
            Assert.Equal("Media Foundation", decode.DecoderName);
            Assert.Equal(1920, decode.MaxWidth);
            Assert.Equal(1080, decode.MaxHeight);
            Assert.Equal(60, decode.MaxFrameRate);
            Assert.Equal(25000, decode.MaxBitrateKbps);
            Assert.True(decode.Hardware);
        });
        var baseline = Assert.Single(ConsoleEvidence.Default.VideoDecode, decode => decode.Codec == "av1");
        Assert.Equal(1920, baseline.MaxWidth);
        Assert.Equal(1080, baseline.MaxHeight);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(-1, 1080)]
    [InlineData(1920, 0)]
    [InlineData(1920, -1)]
    public void BuilderRejectsNonpositiveBounds(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ConsoleEvidence.BuildVideoDecode(width, height));

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void PlaybackStartSerializesTheRequestedAv1BoundWithoutExpandingHls(int width, int height)
    {
        var device = ConsoleEvidence.Default with { VideoDecode = ConsoleEvidence.BuildVideoDecode(width, height) };
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null,
            PlaybackSelection.Auto, device, PlaybackTestSupport.Identity);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(start, PlaybackJson.Default.PlaybackStartRequestV3));
        var av1 = Assert.Single(json.RootElement.GetProperty("client_capabilities").GetProperty("video_decode").EnumerateArray(),
            decode => decode.GetProperty("codec").GetString() == "av1");
        Assert.Equal(width, av1.GetProperty("max_width").GetInt32());
        Assert.Equal(height, av1.GetProperty("max_height").GetInt32());
        Assert.Equal(30, av1.GetProperty("max_frame_rate").GetDouble());
        Assert.False(av1.GetProperty("hardware").GetBoolean());
        Assert.Same(device.VideoDecode, start.ClientCapabilities.VideoDecode);
        Assert.Contains(PlaybackProtocol.SoftwareVideoDecodeFeature, start.ClientFeatures);
        Assert.Contains("av1", start.ClientPlaybackContext.Deliveries["original_http"].VideoCodecs);
        Assert.DoesNotContain("av1", start.ClientPlaybackContext.Deliveries["hls"].VideoCodecs);
    }

    [Fact]
    public void ReplansRetainTheRaisedSnapshotExceptWhenTheAv1DecoderFails()
    {
        var device = ConsoleEvidence.Default with { VideoDecode = ConsoleEvidence.BuildVideoDecode(3840, 2160) };
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null,
            PlaybackSelection.Auto, device, PlaybackTestSupport.Identity);
        var plan = PlaybackRoutePolicyTests.Plan("original_http", "av1", "aac");
        plan.Source!.Width = 3840;
        plan.Source.Height = 2160;
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.True(route.UseFfmpeg);
        Assert.True(route.SoftwareVideo);
        Assert.Equal(3840, route.VideoWidth);
        Assert.Equal(2160, route.VideoHeight);
        foreach (var request in new[]
        {
            PlaybackReplan.ForSeek(start, plan, "plan-attempt", [], 0, 42),
            PlaybackReplan.ForFailure(start, plan, "plan-attempt", [], 1, 42, new("http_failure")),
            PlaybackReplan.ForHdrOutputFailure(start, plan, "plan-attempt", [], 1, 42),
        })
        {
            Assert.Same(start.ClientCapabilities.VideoDecode, request.ClientCapabilities.VideoDecode);
            var av1 = Assert.Single(request.ClientCapabilities.VideoDecode, decode => decode.Codec == "av1");
            Assert.Equal(3840, av1.MaxWidth);
            Assert.Equal(2160, av1.MaxHeight);
        }
        var failed = PlaybackReplan.ForFailure(start, plan, "plan-attempt", [], 1, 42, new("decoder_failure"), route);
        Assert.DoesNotContain(failed.ClientCapabilities.VideoDecode, decode => decode.Codec == "av1");
        Assert.DoesNotContain("av1", failed.ClientCapabilities.CodecsVideo);
        Assert.DoesNotContain(PlaybackProtocol.SoftwareVideoDecodeFeature, failed.ClientFeatures);
        Assert.All(failed.ClientPlaybackContext.Deliveries.Values, delivery => Assert.DoesNotContain("av1", delivery.VideoCodecs));
        Assert.Contains("server transcode required", failed.Failure!.Message);
        Assert.Contains(start.ClientCapabilities.VideoDecode, decode => decode.Codec == "av1" && decode.MaxHeight == 2160);
    }
}
