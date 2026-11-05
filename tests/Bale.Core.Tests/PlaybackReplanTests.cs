using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class PlaybackReplanTests
{
    private static PlaybackPlan Plan() => new()
    {
        PlanId = "failed-plan", PlanAttemptKey = "opaque-key",
        SelectedTracks = new SelectedTracksV3
        {
            Audio = new TrackIdentityV3 { Id = "selected-audio", Index = 7 },
            Subtitle = new TrackIdentityV3 { Id = "selected-subtitle", Index = 8 },
        },
    };

    [Fact]
    public void FailureRecoveryEchoesTheAttemptContextAndNetworkPreferences()
    {
        var start = Start(new PlaybackSelection(AudioIndex: 1, SubtitleIndex: 2)) with
        {
            QualityPreference = "1080p", Metered = true, BandwidthCapKbps = 5000,
        };
        var failure = PlaybackReplan.Classify(unchecked((int)0xC00D5212), true, false);
        var request = PlaybackReplan.ForFailure(start, Plan(), "plan-attempt", ["prior"], 2, 123.5, failure);
        Assert.Equal(start.PlaybackAttemptId, request.PlaybackAttemptId);
        Assert.True(Guid.TryParse(request.ReplanRequestId, out _));
        Assert.Equal("failed-plan", request.FailedPlanId);
        Assert.Equal("plan-attempt", request.PlanAttemptId);
        Assert.Equal("opaque-key", request.PlanAttemptKey);
        Assert.Equal(new[] { "prior", "opaque-key" }, request.AttemptedPlanKeys);
        Assert.Equal(2, request.AttemptCount);
        Assert.Equal(123.5, request.PositionSeconds);
        Assert.Equal(new PlaybackTrackIdentityV3("selected-audio", 7), request.SelectedTracks.Audio);
        Assert.Equal(new PlaybackTrackIdentityV3("selected-subtitle", 8), request.SelectedTracks.Subtitle);
        Assert.Same(start.ClientFeatures, request.ClientFeatures);
        Assert.Same(start.ClientCapabilities, request.ClientCapabilities);
        Assert.Same(start.ClientPlaybackContext, request.ClientPlaybackContext);
        Assert.Equal("failure_recovery", request.Operation);
        Assert.Same(failure, request.Failure);
        Assert.Equal("1080p", request.QualityPreference);
        Assert.True(request.Metered);
        Assert.Equal(5000, request.BandwidthCapKbps);
        Assert.Equal(3, request.ProtocolVersion);
    }

    [Fact]
    public void EveryRecoveryGetsANewRequestIdAndDistinctNonblankAttemptKeys()
    {
        var first = PlaybackReplan.ForFailure(Start(), Plan(), "attempt", ["", "prior", " ", "prior", "opaque-key"], 1, 0, new("player_failure"));
        var second = PlaybackReplan.ForFailure(Start(), Plan(), "attempt", [], 1, 0, new("player_failure"));
        Assert.Equal(new[] { "prior", "opaque-key" }, first.AttemptedPlanKeys);
        Assert.Equal(new[] { "opaque-key" }, second.AttemptedPlanKeys);
        Assert.NotEqual(first.ReplanRequestId, second.ReplanRequestId);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidPositionsBecomeZero(double position)
    {
        var request = PlaybackReplan.ForFailure(Start(), Plan(), "attempt", [], 1, position, new("player_failure"));
        Assert.Equal(0, request.PositionSeconds);
    }

    [Fact]
    public void MissingPlanTracksFallBackToTheStartRequest()
    {
        var start = Start(new PlaybackSelection(AudioIndex: 1, SubtitleIndex: 3));
        var plan = Plan();
        plan.SelectedTracks = null;
        var request = PlaybackReplan.ForFailure(start, plan, "attempt", [], 1, 0, new("player_failure"));
        Assert.Equal(new PlaybackTrackIdentityV3(start.AudioTrackId!, 1), request.SelectedTracks.Audio);
        Assert.Equal(new PlaybackTrackIdentityV3(start.SubtitleTrackId!, 3), request.SelectedTracks.Subtitle);
    }

    [Fact]
    public void MissingTrackFieldsFallBackIndividually()
    {
        var start = Start(new PlaybackSelection(AudioIndex: 1, SubtitleIndex: 3));
        var plan = Plan();
        plan.SelectedTracks = new SelectedTracksV3 { Audio = new TrackIdentityV3(), Subtitle = new TrackIdentityV3() };
        var request = PlaybackReplan.ForFailure(start, plan, "attempt", [], 1, 0, new("player_failure"));
        Assert.Equal(new PlaybackTrackIdentityV3(start.AudioTrackId!, 1), request.SelectedTracks.Audio);
        Assert.Equal(new PlaybackTrackIdentityV3(start.SubtitleTrackId!, 3), request.SelectedTracks.Subtitle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NegativeSubtitleIndexMeansNoneWhetherFromPlanOrStart(bool fromPlan)
    {
        var plan = Plan();
        var start = Start(new PlaybackSelection(AudioIndex: 1, SubtitleIndex: 3));
        if (fromPlan) plan.SelectedTracks!.Subtitle!.Index = -1;
        else
        {
            plan.SelectedTracks!.Subtitle = null;
            start = start with { SubtitleTrackId = "ignored", SubtitleTrackIndex = -1 };
        }
        var request = PlaybackReplan.ForFailure(start, plan, "attempt", [], 1, 0, new("player_failure"));
        Assert.Null(request.SelectedTracks.Subtitle);
    }

    [Fact]
    public void MissingTracksRemainAbsent()
    {
        var plan = Plan();
        plan.SelectedTracks = null;
        var request = PlaybackReplan.ForFailure(Start(), plan, "attempt", [], 1, 0, new("player_failure"));
        Assert.Null(request.SelectedTracks.Audio);
        Assert.Null(request.SelectedTracks.Subtitle);
    }

    [Theory]
    [InlineData(null, "key")]
    [InlineData("", "key")]
    [InlineData("  ", "key")]
    [InlineData("plan", null)]
    [InlineData("plan", "")]
    [InlineData("plan", "  ")]
    public void MissingPlanIdentityIsRejected(string? id, string? key)
    {
        var plan = Plan();
        plan.PlanId = id;
        plan.PlanAttemptKey = key;
        var error = Assert.Throws<InvalidDataException>(() => PlaybackReplan.ForFailure(Start(), plan, "attempt", [], 1, 0, new("player_failure")));
        Assert.Equal("The playback plan has no recovery identity.", error.Message);
    }

    [Theory]
    [InlineData(unchecked((int)0xC00D5212), true, false, "decoder_failure", "MediaFailed 0xC00D5212")]
    [InlineData(42, true, true, "decoder_failure", "MediaFailed 0x0000002A")]
    [InlineData(42, false, true, "http_failure", "MediaFailed 0x0000002A")]
    [InlineData(42, false, false, "player_failure", "MediaFailed 0x0000002A")]
    [InlineData(null, false, false, "player_failure", "MediaFailed")]
    public void FailureClassificationUsesOnlyNonSensitivePlayerEvidence(int? hresult, bool decoder, bool network, string classification, string message)
    {
        var failure = PlaybackReplan.Classify(hresult, decoder, network);
        Assert.Equal(classification, failure.Classification);
        Assert.Equal(message, failure.Message);
        Assert.Null(failure.DecoderName);
    }

    [Theory]
    [InlineData("av1", "eac3", true, false)]
    [InlineData("hevc", "dts", false, true)]
    [InlineData("av1", "truehd", true, true)]
    public void FfmpegFailureRemovesOnlyTheTriggeringClaimsAndRequestsServerAdaptation(string video, string audio, bool dropAv1, bool dropAudio)
    {
        var start = Start();
        var plan = PlaybackRoutePolicyTests.Plan("original_http", video, audio);
        var route = PlaybackRoutePolicy.Decide(plan, false);
        var request = PlaybackReplan.ForFailure(start, plan, "plan-attempt", [], 1, 42,
            PlaybackReplan.Classify(unchecked((int)0xC00D5212), false, false), route);
        Assert.Equal(!dropAv1, request.ClientCapabilities.CodecsVideo.Contains("av1"));
        Assert.Equal(!dropAv1, request.ClientCapabilities.VideoDecode.Any(decode => decode.Codec == "av1"));
        Assert.Equal(!dropAv1, request.ClientFeatures.Contains(PlaybackProtocol.SoftwareVideoDecodeFeature));
        Assert.Equal(!dropAudio, request.ClientCapabilities.CodecsAudio.Contains(audio));
        foreach (var delivery in request.ClientPlaybackContext.Deliveries.Where(pair => pair.Key != "hls").Select(pair => pair.Value))
        {
            Assert.Equal(!dropAv1, delivery.VideoCodecs.Contains("av1"));
            Assert.Equal(!dropAudio, delivery.AudioDecodeCodecs.Contains(audio));
        }
        Assert.Contains("hevc", request.ClientCapabilities.CodecsVideo);
        Assert.Contains("aac", request.ClientCapabilities.CodecsAudio);
        Assert.Contains("flac", request.ClientCapabilities.CodecsAudio);
        Assert.Equal("decoder_failure", request.Failure!.Classification);
        Assert.Equal("FFmpegInteropX", request.Failure.DecoderName);
        Assert.Contains("server transcode required", request.Failure.Message);
        Assert.Contains("0xC00D5212", request.Failure.Message);
        Assert.Equal(3, request.SelectedTracks.Audio!.Index);
        Assert.Equal(42, request.PositionSeconds);
        Assert.Equal("plan-attempt", request.PlanAttemptId);
        // Recovery must not mutate the baseline or the original request shared by Retry.
        Assert.Contains("av1", start.ClientCapabilities.CodecsVideo);
        Assert.Contains(audio, start.ClientCapabilities.CodecsAudio);
        Assert.Contains("av1", ConsoleEvidence.Default.VideoCodecs);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request, PlaybackJson.Default.PlaybackReplanRequestV3));
        Assert.Equal(!dropAv1, json.RootElement.GetProperty("client_features").EnumerateArray()
            .Any(feature => feature.GetString() == PlaybackProtocol.SoftwareVideoDecodeFeature));
    }

    [Fact]
    public void SystemFailuresDoNotWithdrawWorkingFfmpegClaims()
    {
        var start = Start();
        var plan = PlaybackRoutePolicyTests.Plan("original_http", "hevc", "eac3");
        var request = PlaybackReplan.ForFailure(start, plan, "attempt", [], 1, 0,
            PlaybackReplan.Classify(1, false, true), PlaybackRoutePolicy.Decide(plan, false));
        Assert.Same(start.ClientCapabilities, request.ClientCapabilities);
        Assert.Same(start.ClientFeatures, request.ClientFeatures);
        Assert.Equal("http_failure", request.Failure!.Classification);
    }

    [Fact]
    public void HdrOutputFailureWithdrawsEveryHdrClaimWithoutDroppingCodecsOrRecoveryContext()
    {
        var hdr = new HdrCapabilities { Hdr10 = true, Hdr10Plus = true, Hlg = true, DolbyVisionProfiles = [5] };
        var device = ConsoleEvidence.Default with { Hdr = hdr, DisplayHdrEvidence = "exact" };
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", 123.5,
            new PlaybackSelection(AudioIndex: 1, SubtitleIndex: 2), device, Identity) with
        {
            QualityPreference = "1080p", Metered = true, BandwidthCapKbps = 5000,
        };
        var request = PlaybackReplan.ForHdrOutputFailure(start, Plan(), "plan-attempt", ["prior"], 1, 123.5);
        Assert.False(request.ClientCapabilities.Hdr);
        AssertNoHdr(request.ClientCapabilities.HdrDetails);
        AssertNoHdr(request.ClientPlaybackContext.Output.HdrDetails);
        AssertNoHdr(request.ClientPlaybackContext.Output.Display!.HdrTypes);
        Assert.Equal("unknown", request.ClientPlaybackContext.Output.Display.HdrEvidence);
        Assert.All(request.ClientPlaybackContext.Deliveries.Values, delivery => AssertNoHdr(delivery.HdrDetails));
        Assert.Same(start.ClientCapabilities.CodecsVideo, request.ClientCapabilities.CodecsVideo);
        Assert.Same(start.ClientCapabilities.VideoDecode, request.ClientCapabilities.VideoDecode);
        Assert.Same(start.ClientCapabilities.CodecsAudio, request.ClientCapabilities.CodecsAudio);
        Assert.Same(start.ClientFeatures, request.ClientFeatures);
        Assert.Same(start.ClientPlaybackContext.Output.AudioPassthrough, request.ClientPlaybackContext.Output.AudioPassthrough);
        Assert.Equal("failed-plan", request.FailedPlanId);
        Assert.Equal("plan-attempt", request.PlanAttemptId);
        Assert.Equal(new[] { "prior", "opaque-key" }, request.AttemptedPlanKeys);
        Assert.Equal(start.PlaybackAttemptId, request.PlaybackAttemptId);
        Assert.Equal(123.5, request.PositionSeconds);
        Assert.Equal(new PlaybackTrackIdentityV3("selected-audio", 7), request.SelectedTracks.Audio);
        Assert.Equal(new PlaybackTrackIdentityV3("selected-subtitle", 8), request.SelectedTracks.Subtitle);
        Assert.Equal("1080p", request.QualityPreference);
        Assert.True(request.Metered);
        Assert.Equal(5000, request.BandwidthCapKbps);
        Assert.Equal("failure_recovery", request.Operation);
        Assert.Equal("player_failure", request.Failure!.Classification);
        Assert.Equal("HDR output switch failed; server tone mapping required.", request.Failure.Message);
        // The device baseline and admitted request remain immutable for other sessions / Retry.
        Assert.True(start.ClientCapabilities.Hdr);
        Assert.Same(hdr, start.ClientPlaybackContext.Output.Display!.HdrTypes);
        Assert.Equal("exact", start.ClientPlaybackContext.Output.Display.HdrEvidence);
        Assert.All(start.ClientPlaybackContext.Deliveries.Values, delivery => Assert.Same(hdr, delivery.HdrDetails));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request, PlaybackJson.Default.PlaybackReplanRequestV3));
        var body = json.RootElement;
        Assert.False(body.GetProperty("client_capabilities").GetProperty("hdr").GetBoolean());
        Assert.False(body.GetProperty("client_capabilities").GetProperty("hdr_details").GetProperty("hdr10").GetBoolean());
        var output = body.GetProperty("client_playback_context").GetProperty("output");
        Assert.False(output.GetProperty("hdr_details").GetProperty("hdr10").GetBoolean());
        Assert.Equal("unknown", output.GetProperty("display").GetProperty("hdr_evidence").GetString());
        Assert.False(output.GetProperty("display").GetProperty("hdr_types").GetProperty("hdr10").GetBoolean());
        foreach (var delivery in body.GetProperty("client_playback_context").GetProperty("deliveries").EnumerateObject())
            Assert.False(delivery.Value.GetProperty("hdr_details").GetProperty("hdr10").GetBoolean());

        static void AssertNoHdr(HdrCapabilities capabilities)
        {
            Assert.False(capabilities.Hdr10);
            Assert.False(capabilities.Hdr10Plus);
            Assert.False(capabilities.Hlg);
            Assert.Empty(capabilities.DolbyVisionProfiles);
        }
    }

    [Fact]
    public void RecoverySerializationKeepsTheV3SnakeCaseContract()
    {
        var request = PlaybackReplan.ForFailure(Start(), Plan(), "plan-attempt", ["prior"], 1, 40,
            PlaybackReplan.Classify(null, false, true));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request, PlaybackJson.Default.PlaybackReplanRequestV3));
        var body = json.RootElement;
        Assert.Equal("failure_recovery", body.GetProperty("operation").GetString());
        Assert.Equal("http_failure", body.GetProperty("failure").GetProperty("classification").GetString());
        Assert.Equal(new[] { "prior", "opaque-key" }, body.GetProperty("attempted_plan_keys").EnumerateArray().Select(key => key.GetString()));
        var tracks = body.GetProperty("selected_tracks");
        Assert.Equal("selected-audio", tracks.GetProperty("audio").GetProperty("id").GetString());
        Assert.Equal(7, tracks.GetProperty("audio").GetProperty("index").GetInt32());
        Assert.Equal("selected-subtitle", tracks.GetProperty("subtitle").GetProperty("id").GetString());
        Assert.Equal(8, tracks.GetProperty("subtitle").GetProperty("index").GetInt32());
        Assert.False(body.TryGetProperty("selectedTracks", out _));
        Assert.False(body.TryGetProperty("attemptedPlanKeys", out _));
        Assert.True(body.TryGetProperty("client_playback_context", out _));
        Assert.True(body.TryGetProperty("client_capabilities", out _));
    }
}
