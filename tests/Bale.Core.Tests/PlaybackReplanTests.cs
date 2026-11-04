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
