using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class PlaybackEmbeddedSubtitleFailureTests
{
    private static PlaybackPlan NativePlan(string? mode = "render", long? index = 3, string delivery = "original_http")
    {
        var plan = PlaybackRoutePolicyTests.Plan(delivery, "hevc", "eac3");
        plan.Source!.Container = "mkv";
        plan.SelectedTracks!.Subtitle = new TrackIdentityV3 { Id = "file:file:subtitle:2", Index = 2 };
        plan.Subtitle = new SubtitleDecisionV3
        {
            Mode = mode,
            TrackId = "file:file:subtitle:2",
            Embedded = new EmbeddedSubtitleV3 { StreamIndex = index, ContainerTrackId = "4" },
        };
        return plan;
    }

    private static PlaybackPlan SidecarPlan(string mode = "render")
    {
        var plan = PlaybackRoutePolicyTests.Plan("original_http", "hevc", "eac3");
        plan.Subtitle = new SubtitleDecisionV3
        {
            Mode = mode,
            TrackId = "file:file:subtitle:2",
            Artifact = mode == "burn_in" ? null : new SubtitleArtifactV3 { Url = "/api/v2/stream/s/subtitles/2.vtt", Format = "vtt" },
        };
        return plan;
    }

    [Fact]
    public void OnlyARenderedEmbeddedDecisionIsNative()
    {
        Assert.True(PlaybackEmbeddedSubtitleFailure.IsNative(NativePlan()));
        Assert.False(PlaybackEmbeddedSubtitleFailure.IsNative(null));
        Assert.False(PlaybackEmbeddedSubtitleFailure.IsNative(PlaybackRoutePolicyTests.Plan("original_http", "hevc", "aac")));
        Assert.False(PlaybackEmbeddedSubtitleFailure.IsNative(SidecarPlan()));
        Assert.False(PlaybackEmbeddedSubtitleFailure.IsNative(SidecarPlan("convert")));
        Assert.False(PlaybackEmbeddedSubtitleFailure.IsNative(SidecarPlan("burn_in")));
        Assert.False(PlaybackEmbeddedSubtitleFailure.IsNative(NativePlan("burn_in")));
        Assert.False(PlaybackEmbeddedSubtitleFailure.IsNative(NativePlan("off")));
        Assert.False(PlaybackEmbeddedSubtitleFailure.IsNative(NativePlan(null)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(3L)]
    [InlineData(65535L)]
    public void AMountableNativePlanIsValid(long index) =>
        Assert.Null(PlaybackEmbeddedSubtitleFailure.PlanFailure(NativePlan(index: index)));

    [Theory]
    [InlineData(-1L)]
    [InlineData(65536L)]
    [InlineData(null)]
    public void ANativePlanWithoutAUsableStreamIndexFails(long? index) =>
        Assert.Equal("stream_index", PlaybackEmbeddedSubtitleFailure.PlanFailure(NativePlan(index: index)));

    [Theory]
    [InlineData("hls")]
    [InlineData("server_remux_progressive")]
    public void NativeSelectionOffOriginalHttpFails(string delivery) =>
        Assert.Equal("delivery", PlaybackEmbeddedSubtitleFailure.PlanFailure(NativePlan(delivery: delivery)));

    [Fact]
    public void NonNativePlansNeverFailTheNativeCheck()
    {
        Assert.Null(PlaybackEmbeddedSubtitleFailure.PlanFailure(null));
        Assert.Null(PlaybackEmbeddedSubtitleFailure.PlanFailure(SidecarPlan()));
        Assert.Null(PlaybackEmbeddedSubtitleFailure.PlanFailure(SidecarPlan("burn_in")));
        Assert.Null(PlaybackEmbeddedSubtitleFailure.PlanFailure(NativePlan("burn_in", index: -1, delivery: "hls")));
        Assert.Null(PlaybackEmbeddedSubtitleFailure.PlanFailure(NativePlan("off")));
    }

    [Fact]
    public void TheBoundStreamMustBeInTheDemuxersSubtitleInventory()
    {
        Assert.True(PlaybackEmbeddedSubtitleFailure.Mounted(3, [2, 3, 5]));
        Assert.False(PlaybackEmbeddedSubtitleFailure.Mounted(4, [2, 3, 5]));
        Assert.False(PlaybackEmbeddedSubtitleFailure.Mounted(3, []));
    }

    [Fact]
    public void ANativeFailureReplansOnceThenPlaysOnWithoutSubtitles()
    {
        Assert.True(PlaybackEmbeddedSubtitleFailure.Replans(NativePlan(), recoveryUsed: false));
        // A second failure in the same attempt (the budget is spent) never loops or ends playback.
        Assert.False(PlaybackEmbeddedSubtitleFailure.Replans(NativePlan(), recoveryUsed: true));
        // Sidecars, packaged artifacts and burn-in never send it.
        Assert.False(PlaybackEmbeddedSubtitleFailure.Replans(SidecarPlan(), recoveryUsed: false));
        Assert.False(PlaybackEmbeddedSubtitleFailure.Replans(SidecarPlan("burn_in"), recoveryUsed: false));
        Assert.False(PlaybackEmbeddedSubtitleFailure.Replans(null, recoveryUsed: false));
    }

    [Fact]
    public void TheFailureIsClassifiedWithAFixedMessage()
    {
        var failure = PlaybackEmbeddedSubtitleFailure.Failure;
        Assert.Equal("subtitle_embedded_failed", failure.Classification);
        Assert.True(PlaybackEmbeddedSubtitleFailure.Is(failure));
        Assert.False(PlaybackEmbeddedSubtitleFailure.Is(null));
        Assert.False(PlaybackEmbeddedSubtitleFailure.Is(new(PlaybackAudioSelectionException.Classification)));
        Assert.False(PlaybackEmbeddedSubtitleFailure.Is(PlaybackReplan.Classify(null, false, true)));
        Assert.Null(failure.DecoderName);
        Assert.False(failure.AudioDecoderUnsupported);
    }

    public static TheoryData<string, PlaybackRouteDecision?> FailedRoutes => new()
    {
        { "no route", null },
        { "system decoder", new PlaybackRouteDecision(true, false, "hevc", "eac3", 3, 1920, 1080) },
        { "software decode", new PlaybackRouteDecision(true, true, "av1", "eac3", 3, 1920, 1080) },
        { "console tone map", new PlaybackRouteDecision(true, false, "hevc", "eac3", 3, 3840, 2160, ConsoleToneMap: true) },
        { "console downscale", new PlaybackRouteDecision(true, false, "hevc", "eac3", 3, 3840, 2160, ConsoleDownscale: true) },
    };

    [Theory]
    [MemberData(nameof(FailedRoutes))]
    public void TheRecoveryKeepsTheRouteTracksAndPosition(string name, PlaybackRouteDecision? route)
    {
        Assert.NotNull(name);
        var start = Start(new PlaybackSelection(AudioIndex: 3, SubtitleIndex: 2)) with { QualityPreference = "original" };
        var plan = NativePlan();
        var request = PlaybackReplan.ForFailure(start, plan, "plan-attempt", ["prior"], 1, 42.5,
            PlaybackEmbeddedSubtitleFailure.Failure, route);

        Assert.Equal("failure_recovery", request.Operation);
        Assert.Same(PlaybackEmbeddedSubtitleFailure.Failure, request.Failure);
        Assert.Equal(start.PlaybackAttemptId, request.PlaybackAttemptId);
        Assert.Equal("plan", request.FailedPlanId);
        Assert.Equal("plan-attempt", request.PlanAttemptId);
        Assert.Equal("attempt-key", request.PlanAttemptKey);
        Assert.Equal(new[] { "prior", "attempt-key" }, request.AttemptedPlanKeys);
        Assert.Equal(1, request.AttemptCount);
        Assert.Equal(42.5, request.PositionSeconds);
        Assert.Equal(new PlaybackTrackIdentityV3("audio-3", 3), request.SelectedTracks.Audio);
        Assert.Equal(new PlaybackTrackIdentityV3("file:file:subtitle:2", 2), request.SelectedTracks.Subtitle);
        // Not a codec, delivery or range withdrawal: the working video/audio route is offered as it was, and the
        // server drops native selection itself (withoutFailedSubtitleRoutesV3).
        Assert.Same(start.ClientFeatures, request.ClientFeatures);
        Assert.Same(start.ClientCapabilities, request.ClientCapabilities);
        Assert.Same(start.ClientPlaybackContext, request.ClientPlaybackContext);
        Assert.Equal("original", request.QualityPreference);
    }

    [Fact]
    public void ADolbyVision7RecoveryKeepsItsConversionClaims()
    {
        var start = Start(new PlaybackSelection(AudioIndex: 1, SubtitleIndex: 2));
        var plan = new PlaybackPlan
        {
            PlanId = "plan", PlanAttemptKey = "key", Delivery = "original_http",
            DecisionReason = PlaybackProtocol.ClientDolbyVision7ToHdr10,
            Source = new() { MediaFileId = "file", VideoCodec = "hevc", AudioCodec = "eac3", Container = "mkv",
                DolbyVisionProfile = 7, DynamicRange = "dolby_vision", Width = 1920, Height = 1080, BitDepth = 10 },
            EffectiveRecipe = new() { VideoCodec = "hevc", DynamicRange = "hdr10" },
            Transformations = [new() { Name = "client_dv7_to_hdr10", Executor = "client", RecipeVersion = "1",
                ValidatedClaims = ["dolby_vision_metadata_removed", "hdr10_base_layer_preserved", "enhancement_layer_discarded"] }],
            SelectedTracks = new() { Audio = new() { Id = "audio", Index = 1 }, Subtitle = new() { Id = "subtitle", Index = 2 } },
            Subtitle = new() { Mode = "render", TrackId = "subtitle", Embedded = new() { StreamIndex = 5 } },
        };
        Assert.True(DolbyVision7Policy.RequiresBaseLayer(plan));
        var request = PlaybackReplan.ForFailure(start, plan, "plan-attempt", [], 1, 10, PlaybackEmbeddedSubtitleFailure.Failure);
        Assert.Equal(PlaybackEmbeddedSubtitleFailure.Classification, request.Failure!.Classification);
        Assert.Same(start.ClientPlaybackContext, request.ClientPlaybackContext);
    }

    [Fact]
    public void TheRequestBodyCarriesTheClassificationAndOnlyProtocolFields()
    {
        var start = Start(new PlaybackSelection(AudioIndex: 3, SubtitleIndex: 2));
        var request = PlaybackReplan.ForFailure(start, NativePlan(), "plan-attempt", [], 1, 42,
            PlaybackEmbeddedSubtitleFailure.Failure, new PlaybackRouteDecision(true, true, "av1", "eac3", 3, 1920, 1080));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request, PlaybackJson.Default.PlaybackReplanRequestV3));
        var root = json.RootElement;
        Assert.Equal("failure_recovery", root.GetProperty("operation").GetString());
        var failure = root.GetProperty("failure");
        Assert.Equal("subtitle_embedded_failed", failure.GetProperty("classification").GetString());
        Assert.False(failure.TryGetProperty("decoder_name", out var decoder) && decoder.ValueKind != JsonValueKind.Null);
        Assert.False(failure.TryGetProperty("audio_decoder_unsupported", out _));
        Assert.Equal(42, root.GetProperty("position_seconds").GetDouble());
        Assert.Equal(2, root.GetProperty("selected_tracks").GetProperty("subtitle").GetProperty("index").GetInt32());
        Assert.Equal(3, root.GetProperty("selected_tracks").GetProperty("audio").GetProperty("index").GetInt32());
        // The software route is still offered: no withdrawal on a subtitle-only failure.
        Assert.Equal(start.ClientCapabilities.VideoDecode.Count,
            root.GetProperty("client_capabilities").GetProperty("video_decode").GetArrayLength());
        Assert.True(root.GetProperty("client_playback_context").GetProperty("deliveries").GetProperty("original_http").GetProperty("enabled").GetBoolean());
    }
}
