using System.Text.Json;
using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackCapabilitiesTests
{
    [Fact]
    public void OriginalAudioSelectionDoesNotAuthorizeAnUnclaimedCodecOrExceedTheChannelCeiling()
    {
        var device = ConsoleEvidence.Default with { AudioDecodeCodecs = ["aac"], MaxChannels = 2 };
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null,
            new PlaybackSelection(AudioIndex: 1), device, PlaybackTestSupport.Identity);
        Assert.Contains(PlaybackProtocol.ClientSelectedAudioTrackClaim, start.ClientPlaybackContext.Deliveries["original_http"].ValidatedClaims);
        Assert.Equal(new[] { "aac" }, start.ClientCapabilities.CodecsAudio);
        Assert.Empty(start.ClientCapabilities.AudioPassthrough.PassthroughCodecs);
        Assert.All(start.ClientPlaybackContext.Deliveries.Values, delivery =>
        {
            Assert.Equal(new[] { "aac" }, delivery.AudioDecodeCodecs);
            Assert.Empty(delivery.AudioPassthroughCodecs);
            Assert.Equal(2, delivery.MaxChannels);
        });
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void OriginalAudioSelectionClaimRequiresAnEnabledExecutorAndNeverLeaksToPackagedDeliveries(bool original, bool selection)
    {
        var device = ConsoleEvidence.Default with
        {
            OriginalHttpSupported = original, OriginalAudioTrackSelectionSupported = selection,
            ProgressiveSupported = true,
        };
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null,
            new PlaybackSelection(AudioIndex: 1), device, PlaybackTestSupport.Identity);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(start, PlaybackJson.Default.PlaybackStartRequestV3));
        foreach (var delivery in json.RootElement.GetProperty("client_playback_context").GetProperty("deliveries").EnumerateObject())
        {
            Assert.Equal(delivery.Name == "original_http" && original && selection,
                delivery.Value.GetProperty("validated_claims").EnumerateArray()
                    .Any(claim => claim.GetString() == PlaybackProtocol.ClientSelectedAudioTrackClaim));
            // Selecting a source track grants no new codec, channel, or sink capability.
            Assert.Equal(device.MaxChannels, delivery.Value.GetProperty("max_channels").GetInt32());
            Assert.Empty(delivery.Value.GetProperty("audio_passthrough_codecs").EnumerateArray());
        }
        Assert.Same(device.AudioDecodeCodecs, start.ClientCapabilities.CodecsAudio);
        Assert.Same(device.AudioPassthroughCodecs, start.ClientCapabilities.AudioPassthrough.PassthroughCodecs);
        Assert.Empty(start.ClientCapabilities.AudioPassthrough.Entries);
        Assert.Equal(device.AudioDecodeCodecs, start.ClientPlaybackContext.Deliveries["original_http"].AudioDecodeCodecs);
        Assert.Equal(1, start.AudioTrackIndex);
        Assert.Equal("file:file:audio:1", start.AudioTrackId);
    }

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
            Assert.Equal(60, av1.MaxFrameRate);
        }
    }

    [Fact]
    public void RaisedBoundAddsA4K30Av1EntryBesideTheMeasured1080p60OneAndNeverMutatesTheDefault()
    {
        var claims = ConsoleEvidence.BuildVideoDecode(3840, 2160);
        Assert.Equal(new[] { "h264", "hevc", "av1", "av1" }, claims.Select(decode => decode.Codec));
        Assert.Equal(new[] { (1920, 1080, 60.0), (3840, 2160, 30.0) },
            claims.Where(decode => decode.Codec == "av1").Select(decode => (decode.MaxWidth ?? 0, decode.MaxHeight ?? 0, decode.MaxFrameRate ?? 0)));
        Assert.All(claims.Where(decode => decode.Codec == "av1"), av1 =>
        {
            Assert.Equal("dav1d", av1.DecoderName);
            Assert.Equal(new[] { "main" }, av1.Profiles);
            Assert.Empty(av1.Levels);
            Assert.Equal(new[] { 8, 10 }, av1.BitDepths);
            Assert.Null(av1.MaxBitrateKbps);
            Assert.False(av1.Hardware);
        });
        Assert.All(claims.Where(decode => decode.Codec != "av1"), decode =>
        {
            Assert.Equal("Media Foundation", decode.DecoderName);
            Assert.Equal(1920, decode.MaxWidth);
            Assert.Equal(1080, decode.MaxHeight);
            Assert.Equal(60, decode.MaxFrameRate);
            Assert.Null(decode.MaxBitrateKbps);
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
        var av1 = json.RootElement.GetProperty("client_capabilities").GetProperty("video_decode").EnumerateArray()
            .Where(decode => decode.GetProperty("codec").GetString() == "av1").ToArray();
        Assert.Equal(width > 1920 ? [(1920, 1080, 60.0), (width, height, 30.0)] : new[] { (width, height, 60.0) },
            av1.Select(decode => (decode.GetProperty("max_width").GetInt32(), decode.GetProperty("max_height").GetInt32(),
                decode.GetProperty("max_frame_rate").GetDouble())));
        Assert.All(av1, decode => Assert.False(decode.GetProperty("hardware").GetBoolean()));
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
            PlaybackReplan.ForSeek(start, plan, "plan-attempt", [], 1, 42),
            PlaybackReplan.ForFailure(start, plan, "plan-attempt", [], 1, 42, new("http_failure")),
            PlaybackReplan.ForHdrOutputFailure(start, plan, "plan-attempt", [], 1, 42),
        })
        {
            Assert.Same(start.ClientCapabilities.VideoDecode, request.ClientCapabilities.VideoDecode);
            Assert.Contains(request.ClientCapabilities.VideoDecode,
                decode => decode is { Codec: "av1", MaxWidth: 3840, MaxHeight: 2160, MaxFrameRate: 30 });
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
