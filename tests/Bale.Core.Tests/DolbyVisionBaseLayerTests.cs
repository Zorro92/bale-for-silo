using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>Dolby Vision profile 8 base-layer fallback (silo-server client_dv8_base_layer_fallback_v1).</summary>
public class DolbyVisionBaseLayerTests
{
    private static PlaybackPlan BaseLayerPlan(string container)
    {
        var plan = PlaybackRoutePolicyTests.Plan("original_http", "hevc", "eac3");
        plan.Source!.Container = container;
        plan.DecisionReason = PlaybackProtocol.ClientDolbyVision8BaseLayerReason;
        return plan;
    }

    [Fact]
    public void TheConsoleClaimsTheBaseLayerOnTheOriginalDeliveryOnly()
    {
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null, PlaybackSelection.Auto,
            ConsoleEvidence.Default with { ProgressiveSupported = true }, PlaybackTestSupport.Identity);
        Assert.Contains(PlaybackProtocol.ClientDolbyVision8BaseLayerClaim, start.ClientPlaybackContext.Deliveries["original_http"].ValidatedClaims);
        Assert.DoesNotContain(PlaybackProtocol.ClientDolbyVision8BaseLayerClaim, start.ClientPlaybackContext.Deliveries["progressive"].ValidatedClaims);
        Assert.DoesNotContain(PlaybackProtocol.ClientDolbyVision8BaseLayerClaim, start.ClientPlaybackContext.Deliveries["hls"].ValidatedClaims);
        // The claim never promises Dolby Vision output: no DV profiles are claimed.
        Assert.Empty(start.ClientPlaybackContext.Deliveries["original_http"].HdrDetails.DolbyVisionProfiles);
    }

    [Fact]
    public void NoOriginalDeliveryOrNoFfmpegMeansNoClaim()
    {
        Assert.Empty(PlaybackStartBuilder.OriginalClaims(ConsoleEvidence.Default with { OriginalHttpSupported = false }));
        Assert.DoesNotContain(PlaybackProtocol.ClientDolbyVision8BaseLayerClaim,
            PlaybackStartBuilder.OriginalClaims(SoftwareDecodePolicy.WithoutFallbacks(ConsoleEvidence.Default)));
        Assert.Contains(PlaybackProtocol.ClientSelectedAudioTrackClaim,
            PlaybackStartBuilder.OriginalClaims(SoftwareDecodePolicy.WithoutFallbacks(ConsoleEvidence.Default)));
    }

    [Theory]
    [InlineData("mkv")]
    [InlineData("mp4")]
    public void ABaseLayerPlanUsesTheFfmpegDemuxerWithTheSystemDecoder(string container)
    {
        var route = PlaybackRoutePolicy.Decide(BaseLayerPlan(container), false);
        Assert.True(route.UseFfmpeg);
        Assert.False(route.SoftwareVideo);
        Assert.True(route.DolbyVisionBaseLayer);
        Assert.Equal("hevc", route.VideoCodec);
        // Debug FFmpeg off keeps Windows' own source and withdraws the claim (above).
        var native = PlaybackRoutePolicy.Decide(BaseLayerPlan(container), false, ffmpegEnabled: false);
        Assert.False(native.UseFfmpeg);
        Assert.False(native.DolbyVisionBaseLayer);
    }

    [Fact]
    public void OtherPlansKeepTheirRoute()
    {
        var mp4 = PlaybackRoutePolicyTests.Plan("original_http", "hevc", "eac3");
        mp4.Source!.Container = "mp4";
        Assert.False(PlaybackRoutePolicy.Decide(mp4, false).UseFfmpeg);
        var remux = BaseLayerPlan("mp4");
        remux.Delivery = "hls";
        Assert.False(PlaybackRoutePolicy.Decide(remux, true).DolbyVisionBaseLayer);
    }

    [Fact]
    public void AFailedBaseLayerMountWithdrawsTheClaimOnReplan()
    {
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null, PlaybackSelection.Auto,
            ConsoleEvidence.Default, PlaybackTestSupport.Identity);
        var plan = BaseLayerPlan("mkv");
        var failure = new PlaybackFailureV3("decoder_failure", "open failed", "MediaPlayer");
        var replan = PlaybackReplan.ForFailure(start, plan, "attempt", [], 1, 0, failure, PlaybackRoutePolicy.Decide(plan, false));
        Assert.All(replan.ClientPlaybackContext.Deliveries.Values,
            delivery => Assert.DoesNotContain(PlaybackProtocol.ClientDolbyVision8BaseLayerClaim, delivery.ValidatedClaims));
        // HEVC itself stays claimed: the server can still remux or transcode the file.
        Assert.Contains("hevc", replan.ClientCapabilities.CodecsVideo);
    }

    [Fact]
    public void AnHdr10BaseLayerPlanSwitchesTheDisplayLikeHdr10()
    {
        var plan = BaseLayerPlan("mkv");
        plan.Source!.DynamicRange = "dolby_vision";
        plan.EffectiveRecipe = new() { DynamicRange = "hdr10" };
        Assert.True(DisplayHdrPolicy.RequiresHdr10(plan));
        plan.EffectiveRecipe.DynamicRange = "sdr"; // Profile 8.2: SDR base layer.
        Assert.False(DisplayHdrPolicy.RequiresHdr10(plan));
    }
}
