using System.Net;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using FFmpegInteropX;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public sealed class DolbyVision7Tests
{
    private static PlaybackPlan Plan(bool managed = false) => new()
    {
        PlanId = "plan", PlanAttemptKey = "key", Delivery = "original_http",
        DecisionReason = managed ? PlaybackProtocol.ClientManagedDynamicRangeReason : PlaybackProtocol.ClientDolbyVision7ToHdr10,
        Source = new() { MediaFileId = "file", VideoCodec = "hevc", AudioCodec = "eac3", Container = "mp4",
            DolbyVisionProfile = 7, DynamicRange = "dolby_vision", Width = 1920, Height = 1080, BitDepth = 10 },
        EffectiveRecipe = new() { VideoCodec = "hevc", DynamicRange = managed ? "dolby_vision" : "hdr10" },
        Transformations = managed ? [] : [new() { Name = "client_dv7_to_hdr10", Executor = "client", RecipeVersion = "1",
            ValidatedClaims = ["dolby_vision_metadata_removed", "hdr10_base_layer_preserved", "enhancement_layer_discarded"] }],
        SelectedTracks = new() { Audio = new() { Id = "chosen-audio", Index = 1 }, Subtitle = new() { Id = "chosen-subtitle", Index = 2 } },
    };

    private static FileVersion Version(bool dualTrack = false) => new()
    {
        FileId = "file", CodecVideo = "hevc", Container = "mp4",
        VideoTracks = [new() { Codec = "hevc", DvProfile = 7, DvConfigPresent = true, DvBlPresent = true,
            DvElPresent = true, DvRpuPresent = true, BitDepth = 10, PixelFormat = "yuv420p10le",
            ColorTransfer = "smpte2084", ColorPrimaries = "bt2020", ColorSpace = "bt2020nc",
            Width = 1920, Height = 1080, FrameRate = "24" },
            .. dualTrack ? new VideoTrack[] { new() { Codec = "hevc", Width = 960, Height = 540 } } : []],
    };

    private static PlaybackStartRequestV3 SdrStart(bool dualTrack = false)
    {
        var detail = new ItemDetail("item", "movie", "", null, null, null, null, null, [], null, [], [], null, [], null,
            "item", null, null, new(false, false, false), []) { Versions = [Version(dualTrack)] };
        return PlaybackStartBuilder.Build(detail, PlaybackSelection.Auto, "profile", "attempt", 40,
            ConsoleEvidence.Default with { DisplayHdrEvidence = "exact", OriginalDynamicRangeMappingSupported = true }, Identity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartJsonDeclaresTheExactClientRecipeOnOriginalOnly(bool hdr)
    {
        var stub = new StubHandler().Enqueue(Capabilities).Enqueue(HttpStatusCode.Created, Decision);
        using var client = Create(stub);
        var device = ConsoleEvidence.Default with { Hdr = new() { Hdr10 = hdr }, ProgressiveSupported = true };
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", 0, PlaybackSelection.Auto, device, Identity);
        await client.Playback.StartAsync(start, new Installation());
        using var json = JsonDocument.Parse(stub.RequestBodies[1]!);
        var body = json.RootElement;
        Assert.Single(body.GetProperty("client_features").EnumerateArray(), value => value.GetString() == "client_video_transformations_v1");
        var deliveries = body.GetProperty("client_playback_context").GetProperty("deliveries");
        var recipe = Assert.Single(deliveries.GetProperty("original_http").GetProperty("transformations").EnumerateArray());
        Assert.Equal(new[] { "executor", "name", "recipe_version", "validated_claims" }, recipe.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("client_dv7_to_hdr10", recipe.GetProperty("name").GetString());
        Assert.Equal("client", recipe.GetProperty("executor").GetString());
        Assert.Equal("1", recipe.GetProperty("recipe_version").GetString());
        Assert.Equal(new[] { "dolby_vision_metadata_removed", "hdr10_base_layer_preserved", "enhancement_layer_discarded" },
            recipe.GetProperty("validated_claims").EnumerateArray().Select(value => value.GetString()));
        foreach (var delivery in new[] { "progressive", "hls" })
            Assert.Empty(deliveries.GetProperty(delivery).GetProperty("transformations").EnumerateArray());
        Assert.DoesNotContain("client_dv7_to_dv81", stub.RequestBodies[1]);
        Assert.Empty(start.ClientCapabilities.HdrDetails.DolbyVisionProfiles);
    }

    [Fact]
    public void DisabledExecutorDoesNotPromiseTheTransformation()
    {
        foreach (var device in new[] { ConsoleEvidence.Default with { OriginalHttpSupported = false },
            ConsoleEvidence.Default with { OriginalDolbyVision7BaseLayerSupported = false },
            SoftwareDecodePolicy.WithoutFallbacks(ConsoleEvidence.Default),
            new DebugFfmpegInputTuning(Decoder: DebugFfmpegDecoder.System).ToneMapCapabilities(ConsoleEvidence.Default),
            new DebugFfmpegInputTuning(Decoder: DebugFfmpegDecoder.Software).ToneMapCapabilities(ConsoleEvidence.Default) })
        {
            var start = PlaybackStartBuilder.Build("file", "profile", "attempt", 0, PlaybackSelection.Auto, device, Identity);
            Assert.All(start.ClientPlaybackContext.Deliveries.Values, delivery => Assert.Empty(delivery.Transformations));
            Assert.False(ConsoleToneMapPolicy.CanManage(Version(), device with
            {
                DisplayHdrEvidence = "exact", OriginalDynamicRangeMappingSupported = true,
            }));
        }
    }

    [Theory]
    [InlineData(false, false, "mp4")]
    [InlineData(false, false, "mkv")]
    [InlineData(true, false, "mp4")]
    [InlineData(false, true, "mp4")]
    [InlineData(true, true, "mp4")]
    [InlineData(true, true, "mkv")]
    public void HdrAndSdrPlansAlwaysDecodeTheBaseLayerWithFfmpeg(bool managed, bool sdr, string container)
    {
        var plan = Plan(managed);
        plan.Source!.Container = container;
        DolbyVision7Policy.ValidatePlan(plan);
        var route = PlaybackRoutePolicy.Decide(plan, false, sdrOutput: sdr);
        Assert.True(route.UseFfmpeg);
        Assert.True(route.DolbyVision7BaseLayer);
        Assert.True(route.DolbyVisionBaseLayer);
        Assert.True(route.RequiresD3D11);
        Assert.False(route.SoftwareVideo);
        Assert.Equal(sdr, route.ConsoleToneMap);
        var config = FfmpegSource.Configuration(route);
        Assert.Equal(VideoDecoderMode.Automatic, config.Video.VideoDecoderMode);
        Assert.Equal(sdr ? HdrSupport.Automatic : HdrSupport.Enabled, config.Video.HdrSupport);
        Assert.Equal(sdr ? "1" : null, config.FFmpegOptions.GetValueOrDefault("bale_tonemap"));
        var disabled = PlaybackRoutePolicy.Decide(plan, false, ffmpegEnabled: false, sdrOutput: sdr);
        Assert.False(disabled.UseFfmpeg);
        Assert.True(disabled.DolbyVision7BaseLayer); // The mount guard must reject the native route.
        if (!sdr) Assert.True(DisplayHdrPolicy.RequiresHdr10(plan));
    }

    [Fact]
    public void SourceProfileAndClientRecipeEachPreventNativeDv7EvenWithoutAReason()
    {
        var plan = Plan();
        plan.DecisionReason = null;
        Assert.True(PlaybackRoutePolicy.Decide(plan, false).DolbyVision7BaseLayer);
        plan.Source!.DolbyVisionProfile = null;
        Assert.True(PlaybackRoutePolicy.Decide(plan, false).DolbyVision7BaseLayer);
        plan.Transformations = [];
        plan.DecisionReason = PlaybackProtocol.ClientDolbyVision7ToHdr10;
        Assert.True(PlaybackRoutePolicy.Decide(plan, false).UseFfmpeg);
        Assert.Throws<InvalidDataException>(() => DolbyVision7Policy.ValidatePlan(plan));
    }

    [Theory]
    [InlineData("hls")]
    [InlineData("server_transcode_hls")]
    [InlineData("server_remux_hls")]
    [InlineData("server_remux_progressive")]
    [InlineData("progressive")]
    public void ServerProducedDeliveriesDoNotExecuteTheClientRecipe(string delivery)
    {
        var plan = Plan();
        plan.Delivery = delivery;
        var route = PlaybackRoutePolicy.Decide(plan, delivery.Contains("hls"), sdrOutput: true);
        Assert.False(route.DolbyVision7BaseLayer);
        Assert.False(route.ConsoleToneMap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleAndDualTrackDv7AreEligibleForSdrToneMapping(bool dualTrack)
    {
        var start = SdrStart(dualTrack);
        Assert.Contains(PlaybackProtocol.ClientManagedDynamicRangeClaim, start.ClientPlaybackContext.Deliveries["original_http"].ValidatedClaims);
        Assert.False(start.AllowAlternateVersions);
        var version = Version(dualTrack);
        var device = ConsoleEvidence.Default with { DisplayHdrEvidence = "exact", OriginalDynamicRangeMappingSupported = true };
        version.VideoTracks![0].DvProfile = 5;
        Assert.False(ConsoleToneMapPolicy.CanManage(version, device));
        version.VideoTracks[0].DvProfile = 7;
        version.VideoTracks[0].ColorPrimaries = "bt709";
        version.VideoTracks[0].ColorSpace = "bt709";
        Assert.False(ConsoleToneMapPolicy.CanManage(version, device)); // DV7 BL must be PQ BT.2020.
    }

    [Fact]
    public void BaseLayerSelectionUsesDemuxOrderAndRejectsMissingOrAmbiguousInventory()
    {
        Assert.Equal(0, DolbyVision7Policy.BaseLayerTrackIndex([3], 1));
        Assert.Equal(0, DolbyVision7Policy.BaseLayerTrackIndex([3, 8], 2));
        Assert.Equal(1, DolbyVision7Policy.BaseLayerTrackIndex([8, 3], 2)); // FFmpeg preferred EL first.
        foreach (var (streams, count) in new (int[], int?)[]
        {
            ([], 0), ([3], null), ([3], 2), ([3, 8], 1), ([3, 3], 2), ([-1, 3], 2),
        }) Assert.Throws<InvalidDataException>(() => DolbyVision7Policy.BaseLayerTrackIndex(streams, count));
    }

    [Fact]
    public void BaseLayerMustUseD3D11AndAnEnhancementStreamIsNeverAcceptedAsDefault()
    {
        var route = PlaybackRoutePolicy.Decide(Plan(), false);
        foreach (var engine in Enum.GetValues<DecoderEngine>())
        {
            var source = new FFmpegMediaSource();
            source.VideoStreams.Add(new() { StreamIndex = 0, CodecName = "hevc", DecoderEngine = engine });
            source.VideoStreams.Add(new() { StreamIndex = 1, CodecName = "hevc", DecoderEngine = DecoderEngine.FFmpegSoftwareDecoder });
            if (engine == DecoderEngine.FFmpegD3D11HardwareDecoder) FfmpegSource.ValidateVideoDecoders(source, route);
            else Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(source, route));
        }
        var elDefault = new FFmpegMediaSource();
        elDefault.VideoStreams.Add(new() { StreamIndex = 1, CodecName = "hevc", DecoderEngine = DecoderEngine.FFmpegD3D11HardwareDecoder });
        elDefault.VideoStreams.Add(new() { StreamIndex = 0, CodecName = "hevc", DecoderEngine = DecoderEngine.FFmpegD3D11HardwareDecoder });
        Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(elDefault, route));
        Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(new(), route));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveGuardRejectsEnhancementSelectionOrDecoderFallbackBeforePresentation(bool toneMap)
    {
        var source = new FFmpegMediaSource();
        var video = new VideoStreamInfo { StreamIndex = 0, CodecName = "hevc", DecoderEngine = DecoderEngine.FFmpegD3D11HardwareDecoder };
        source.VideoStreams.Add(video);
        var failures = 0;
        using var guard = new FfmpegVideoOutputGuard(source, () => failures++, toneMap, baseLayerStreamIndex: 0);
        var descriptor = new VideoStreamDescriptor { EncodingProperties = Format(toneMap) };
        Assert.NotNull(source.Source.Request(descriptor).Sample);
        Assert.Equal(0, failures);
        video.StreamIndex = 1;
        Assert.Null(source.Source.Request(descriptor).Sample);
        Assert.Equal(1, failures);
        video.StreamIndex = 0;
        Assert.Null(source.Source.Request(descriptor).Sample); // Latch until replan, never resume a rejected output.
        guard.Dispose();
        Assert.Equal(0, source.Source.Observers);

        using var decoderGuard = new FfmpegVideoOutputGuard(source, () => failures++, toneMap, baseLayerStreamIndex: 0);
        video.DecoderEngine = DecoderEngine.FFmpegSoftwareDecoder;
        Assert.Null(source.Source.Request(descriptor).Sample);
        Assert.Equal(2, failures);
    }

    [Fact]
    public void HdrGuardRequiresP010PqBt2020AndLimitedRange()
    {
        var format = Format(false);
        Assert.True(FfmpegVideoOutputGuard.IsHdr10Output(format));
        foreach (var key in format.Properties.Keys.ToArray())
        {
            var value = format.Properties[key];
            format.Properties.Remove(key);
            Assert.False(FfmpegVideoOutputGuard.IsHdr10Output(format));
            format.Properties[key] = value;
        }
        format.Subtype = "30313050-0000-0010-8000-00aa00389b71";
        Assert.True(FfmpegVideoOutputGuard.IsHdr10Output(format));
        format.Subtype = "NV12";
        Assert.False(FfmpegVideoOutputGuard.IsHdr10Output(format));
        var source = new FFmpegMediaSource();
        source.VideoStreams.Add(new() { CodecName = "hevc", DecoderEngine = DecoderEngine.FFmpegD3D11HardwareDecoder });
        using var guard = new FfmpegVideoOutputGuard(source, () => { }, toneMap: false, baseLayerStreamIndex: 0);
        Assert.Null(source.Source.Request(new VideoStreamDescriptor { EncodingProperties = format }).Sample);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureReplanWithdrawsBothDv7PromisesAndKeepsCodecsTracksAndSourcePosition(bool managed)
    {
        var start = SdrStart(true) with { Metered = true, BandwidthCapKbps = 12000 };
        var plan = Plan(managed);
        var request = PlaybackReplan.ForFailure(start, plan, "mount", ["prior"], 1, 125,
            new("decoder_failure"), PlaybackRoutePolicy.Decide(plan, false, sdrOutput: managed));
        AssertWithdrawn(request);
        Assert.Same(start.ClientCapabilities, request.ClientCapabilities);
        Assert.Same(start.ClientFeatures, request.ClientFeatures);
        Assert.Same(start.ClientPlaybackContext.Output, request.ClientPlaybackContext.Output);
        Assert.Equal("DV7 base-layer playback failed; server conversion required.", request.Failure!.Message);
        Assert.Equal("failure_recovery", request.Operation);
        Assert.Equal(new[] { "prior", "key" }, request.AttemptedPlanKeys);
        Assert.Equal("mount", request.PlanAttemptId);
        Assert.Equal("plan", request.FailedPlanId);
        Assert.Equal(125, request.PositionSeconds);
        Assert.Equal(new PlaybackTrackIdentityV3("chosen-audio", 1), request.SelectedTracks.Audio);
        Assert.Equal(new PlaybackTrackIdentityV3("chosen-subtitle", 2), request.SelectedTracks.Subtitle);
        Assert.True(request.Metered);
        Assert.Equal(12000, request.BandwidthCapKbps);
        Assert.Single(start.ClientPlaybackContext.Deliveries["original_http"].Transformations);
        AssertWithdrawn(PlaybackReplan.ForHdrOutputFailure(start, plan, "mount", [], 1, 125));
        AssertWithdrawn(PlaybackReplan.ForToneMapFailure(start, plan, "mount", [], 1, 125));
        AssertWithdrawn(PlaybackReplan.ForFailure(start, plan, "mount", [], 1, 125, new("player_failure")));
        var seek = PlaybackReplan.ForSeek(start, plan, "mount", [], 1, 125);
        Assert.Single(seek.ClientPlaybackContext.Deliveries["original_http"].Transformations);
        Assert.Contains(PlaybackProtocol.ClientManagedDynamicRangeClaim, seek.ClientPlaybackContext.Deliveries["original_http"].ValidatedClaims);
    }

    [Fact]
    public void AudioFailureAlsoWithdrawsTheRecipeWithoutBlamingHevc()
    {
        var plan = Plan(true);
        var request = PlaybackReplan.ForFailure(SdrStart(), plan, "mount", [], 1, 25,
            new PlaybackAudioSelectionException(decoderUnsupported: true).Failure, PlaybackRoutePolicy.Decide(plan, false, sdrOutput: true));
        AssertWithdrawn(request);
        Assert.Contains("hevc", request.ClientCapabilities.CodecsVideo);
        Assert.DoesNotContain("eac3", request.ClientCapabilities.CodecsAudio);
        Assert.False(request.ClientPlaybackContext.Deliveries["original_http"].Enabled);
    }

    private static void AssertWithdrawn(PlaybackReplanRequestV3 request)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request, PlaybackJson.Default.PlaybackReplanRequestV3));
        foreach (var delivery in json.RootElement.GetProperty("client_playback_context").GetProperty("deliveries").EnumerateObject())
        {
            Assert.DoesNotContain(delivery.Value.GetProperty("transformations").EnumerateArray(),
                transform => transform.GetProperty("name").GetString() == "client_dv7_to_hdr10");
            Assert.DoesNotContain(delivery.Value.GetProperty("validated_claims").EnumerateArray(),
                claim => claim.GetString() == "client_managed_dynamic_range_v1");
        }
    }

    [Fact]
    public void UnsupportedRecipesFailClosed()
    {
        var plan = Plan();
        plan.Transformations![0].RecipeVersion = "2";
        Assert.Throws<InvalidDataException>(() => DolbyVision7Policy.ValidatePlan(plan));
        plan = Plan();
        plan.Transformations![0].ValidatedClaims!.RemoveAt(0);
        Assert.Throws<InvalidDataException>(() => DolbyVision7Policy.ValidatePlan(plan));
        plan = Plan();
        plan.Transformations = [];
        Assert.Throws<InvalidDataException>(() => DolbyVision7Policy.ValidatePlan(plan));
    }

    [Fact]
    public void StatsNameTheBaseLayerAndSdrMapping()
    {
        Assert.Contains("Dolby Vision 7 → HDR10 base layer", PlaybackStats.FromPlan(Plan()).Delivery);
        Assert.Contains("Dolby Vision 7 → HDR10 base layer → SDR", PlaybackStats.FromPlan(Plan(true)).Delivery);
        Assert.Equal("HDR10 HDMI · Dolby Vision 7 → HDR10 base layer", DebugHdrPlayback.Output(false, Plan()));
        Assert.Equal("SDR / console tone map · Dolby Vision 7 → HDR10 base layer", DebugHdrPlayback.Output(false, Plan(true), true));
    }

    private static VideoEncodingProperties Format(bool sdr) => new()
    {
        Subtype = sdr ? "NV12" : "P010",
        Properties =
        {
            [new("dbfbe4d7-0740-4ee0-8192-850ab0e21935")] = sdr ? 2u : 9u,
            [new("5fb0fce9-be5c-4935-a811-ec838f8eed93")] = sdr ? 5u : 15u,
            [new("3e23d450-2c75-4d25-a00e-b91670d12327")] = sdr ? 1u : 4u,
            [new("c21b8ee5-b956-4071-8daf-325edf5cab11")] = 2u,
        },
    };
}
