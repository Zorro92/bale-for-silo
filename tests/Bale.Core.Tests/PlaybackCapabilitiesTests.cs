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
    public void Rav1gAddsA2160p60EntryAtThe40MbpsCeilingBesideTheMeasured1080p60OneAndNeverMutatesTheDefault()
    {
        var claims = ConsoleEvidence.BuildVideoDecode(rav1g: true);
        Assert.Equal(new[] { "h264", "hevc", "av1", "av1" },
            claims.Where(decode => decode.Hardware || decode.Codec == "av1").Select(decode => decode.Codec));
        // Owner, 2026-11-26: rav1g is tested up to 4K60 and reliable to about 40-60 Mbps; 40 Mbps is the soft ceiling.
        Assert.Equal(new (string?, int, int, double, int?)[] { ("dav1d", 1920, 1080, 60.0, null), ("rav1g", 3840, 2160, 60.0, 40_000) },
            claims.Where(decode => decode.Codec == "av1").Select(decode =>
                (decode.DecoderName, decode.MaxWidth ?? 0, decode.MaxHeight ?? 0, decode.MaxFrameRate ?? 0, decode.MaxBitrateKbps)));
        Assert.All(claims.Where(decode => decode.Codec == "av1"), av1 =>
        {
            Assert.Equal(new[] { "main" }, av1.Profiles);
            Assert.Empty(av1.Levels);
            Assert.Equal(new[] { 8, 10 }, av1.BitDepths);
            Assert.False(av1.Hardware);
        });
        Assert.All(claims.Where(decode => decode.Hardware), decode =>
        {
            Assert.Equal("Media Foundation", decode.DecoderName);
            Assert.Equal(1920, decode.MaxWidth);
            Assert.Equal(1080, decode.MaxHeight);
            Assert.Equal(60, decode.MaxFrameRate);
            Assert.Null(decode.MaxBitrateKbps);
            Assert.True(decode.Hardware);
        });
        // Everything but rav1g's entry is the claim without it.
        Assert.Equal(ConsoleEvidence.BuildVideoDecode().Select(Text), claims.Where(decode => decode.DecoderName != "rav1g").Select(Text));
        var baseline = Assert.Single(ConsoleEvidence.Default.VideoDecode, decode => decode.Codec == "av1");
        Assert.Equal(1920, baseline.MaxWidth);
        Assert.Equal(1080, baseline.MaxHeight);
    }

    /// <summary>A claim's every field, as the wire would carry it (the records' lists compare by reference).</summary>
    private static string Text(VideoDecodeCapability decode) => JsonSerializer.Serialize(decode);

    [Fact]
    public void ForHardwareCarriesRav1gOnlyWhenAsked()
    {
        Assert.Same(ConsoleEvidence.Default, ConsoleEvidence.ForHardware(null));
        var failedProbe = ConsoleEvidence.ForHardware(null, rav1g: true);
        Assert.Equal(ConsoleEvidence.Default with { VideoDecode = failedProbe.VideoDecode }, failedProbe);
        Assert.Contains(failedProbe.VideoDecode, decode => decode is { Codec: "av1", DecoderName: "rav1g", MaxHeight: 2160 });
        Assert.DoesNotContain(ConsoleEvidence.Default.VideoDecode, decode => decode.DecoderName == "rav1g");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlaybackStartSerializesTheAv1ClaimsWithoutExpandingHls(bool rav1g)
    {
        var device = ConsoleEvidence.Default with { VideoDecode = ConsoleEvidence.BuildVideoDecode(rav1g) };
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null,
            PlaybackSelection.Auto, device, PlaybackTestSupport.Identity);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(start, PlaybackJson.Default.PlaybackStartRequestV3));
        var av1 = json.RootElement.GetProperty("client_capabilities").GetProperty("video_decode").EnumerateArray()
            .Where(decode => decode.GetProperty("codec").GetString() == "av1").ToArray();
        // The server holds the source's bitrate to max_bitrate_kbps (capabilities_v3.go videoDecodeTakesSourceV3).
        Assert.Equal(rav1g ? [(1920, 1080, 60.0, 0), (3840, 2160, 60.0, 40_000)] : new[] { (1920, 1080, 60.0, 0) },
            av1.Select(decode => (decode.GetProperty("max_width").GetInt32(), decode.GetProperty("max_height").GetInt32(),
                decode.GetProperty("max_frame_rate").GetDouble(),
                decode.TryGetProperty("max_bitrate_kbps", out var kbps) && kbps.ValueKind == JsonValueKind.Number ? kbps.GetInt32() : 0)));
        Assert.All(av1, decode => Assert.False(decode.GetProperty("hardware").GetBoolean()));
        Assert.Same(device.VideoDecode, start.ClientCapabilities.VideoDecode);
        Assert.Contains(PlaybackProtocol.SoftwareVideoDecodeFeature, start.ClientFeatures);
        Assert.Contains("av1", start.ClientPlaybackContext.Deliveries["original_http"].VideoCodecs);
        Assert.DoesNotContain("av1", start.ClientPlaybackContext.Deliveries["hls"].VideoCodecs);
    }

    [Fact]
    public void ReplansRetainTheRaisedSnapshotExceptWhenTheAv1DecoderFails()
    {
        var device = ConsoleEvidence.Default with { VideoDecode = ConsoleEvidence.BuildVideoDecode(rav1g: true) };
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
                decode => decode is { Codec: "av1", MaxWidth: 3840, MaxHeight: 2160, MaxFrameRate: 60, MaxBitrateKbps: 40_000 });
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
