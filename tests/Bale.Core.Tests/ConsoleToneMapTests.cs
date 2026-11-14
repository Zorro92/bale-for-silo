using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using FFmpegInteropX;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Bale.Core.Tests;

[Collection("Player seek")]
public sealed class ConsoleToneMapTests
{
    private static DeviceCapabilities Device => ConsoleEvidence.Default with
    {
        DisplayHdrEvidence = "exact", OriginalDynamicRangeMappingSupported = true,
        VideoCodecs = ["h264", "hevc", "vp9", "av1"],
        VideoDecode = [.. ConsoleEvidence.Default.VideoDecode, new("vp9", "hardware", [], [], [8, 10], 3840, 2160, 60, null, true)],
    };

    private static FileVersion Version(string codec = "hevc", long? depth = 10, string? transfer = "smpte2084") => new()
    {
        FileId = "file", CodecVideo = codec, Container = "mp4",
        VideoTracks = [new() { Codec = codec, BitDepth = depth, ColorTransfer = transfer,
            ColorPrimaries = "bt2020", ColorSpace = "bt2020nc", Width = 1920, Height = 1080, FrameRate = "24" }],
    };

    private static ItemDetail Detail(params FileVersion[] versions) =>
        new("item", "movie", "", null, null, null, null, null, [], null, [], [], null, [], null,
            "item", null, null, new(false, false, false), []) { Versions = versions };

    private static PlaybackStartRequestV3 Start(FileVersion version, DeviceCapabilities? device = null) =>
        PlaybackStartBuilder.Build(Detail(version), PlaybackSelection.Auto, "profile", "attempt", 25,
            device ?? Device, PlaybackTestSupport.Identity);

    private static bool Claimed(PlaybackStartRequestV3 start) => start.ClientPlaybackContext.Deliveries["original_http"]
        .ValidatedClaims.Contains(PlaybackProtocol.ClientManagedDynamicRangeClaim);

    [Theory]
    [InlineData("hevc", 10, "smpte2084", true)]
    [InlineData("h265", 10, "smpte2084", true)]
    [InlineData("hevc", 10, "arib-std-b67", true)]
    [InlineData("vp9", 10, "smpte2084", true)]
    [InlineData("vp9", 10, "arib-std-b67", true)]
    [InlineData("av1", 10, "smpte2084", false)]
    [InlineData("av1", 10, "arib-std-b67", false)]
    [InlineData("h264", 10, "smpte2084", false)]
    [InlineData("hevc", 8, "smpte2084", false)]
    [InlineData("hevc", 12, "smpte2084", false)]
    [InlineData("hevc", null, "smpte2084", false)]
    [InlineData("hevc", 10, null, false)]
    [InlineData("hevc", 10, "unknown", false)]
    [InlineData("hevc", 10, "bt709", false)]
    [InlineData("hevc", 10, "bt2020-10", false)]
    public void ClaimRequiresAnEligibleCodecDepthAndTransfer(string codec, int? depth, string? transfer, bool expected)
        => Assert.Equal(expected, Claimed(Start(Version(codec, depth, transfer))));

    [Fact]
    public void Hdr10PlusUsesPqAndDoesNotClaimNativeHdrSupport()
    {
        var version = Version();
        version.VideoTracks![0].Hdr10Plus = true;
        version.VideoTracks[0].VideoRangeType = "HDR10+";
        var start = Start(version);
        Assert.True(Claimed(start));
        Assert.False(start.ClientCapabilities.Hdr);
        Assert.False(start.ClientCapabilities.HdrDetails.Hdr10);
        Assert.False(start.ClientPlaybackContext.Output.HdrDetails.Hlg);
        Assert.False(start.AllowAlternateVersions);
        Assert.All(start.ClientPlaybackContext.Deliveries.Where(pair => pair.Key != "original_http"),
            pair => Assert.DoesNotContain(PlaybackProtocol.ClientManagedDynamicRangeClaim, pair.Value.ValidatedClaims));
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(start, PlaybackJson.Default.PlaybackStartRequestV3));
        Assert.False(body.RootElement.GetProperty("allow_alternate_versions").GetBoolean());
        Assert.Contains(PlaybackProtocol.ClientManagedDynamicRangeClaim,
            body.RootElement.GetProperty("client_playback_context").GetProperty("deliveries").GetProperty("original_http")
                .GetProperty("validated_claims").EnumerateArray().Select(claim => claim.GetString()));
    }

    [Theory]
    [InlineData("hdr10")]
    [InlineData("hdr10plus")]
    [InlineData("hlg")]
    [InlineData("dv")]
    [InlineData("unknown")]
    [InlineData("missing-stage")]
    [InlineData("disabled-original")]
    [InlineData("ffmpeg-off")]
    public void OtherOutputsAndUnavailableStagesNeverClaim(string mode)
    {
        var device = mode switch
        {
            "hdr10" => Device with { Hdr = new() { Hdr10 = true } },
            "hdr10plus" => Device with { Hdr = new() { Hdr10Plus = true } },
            "hlg" => Device with { Hdr = new() { Hlg = true } },
            "dv" => Device with { Hdr = new() { DolbyVisionProfiles = [5] } },
            "unknown" => Device with { DisplayHdrEvidence = "unknown" },
            "missing-stage" => Device with { OriginalDynamicRangeMappingSupported = false },
            "disabled-original" => Device with { OriginalHttpSupported = false },
            _ => SoftwareDecodePolicy.WithoutFallbacks(Device),
        };
        Assert.False(Claimed(Start(Version(), device)));
    }

    [Theory]
    [InlineData("smpte2084", "bt2020", "bt2020nc", true)]
    [InlineData("smpte2084", "bt709", "bt709", true)]
    [InlineData("arib-std-b67", "bt2020", "bt2020nc", true)]
    [InlineData("arib-std-b67", "bt709", "bt709", false)]
    [InlineData("smpte2084", null, "bt2020nc", false)]
    [InlineData("smpte2084", "bt2020", null, false)]
    [InlineData("smpte2084", "bt2020", "bt709", false)]
    public void ColourFactsMustMatchTheStage(string transfer, string? primaries, string? matrix, bool expected)
    {
        var version = Version(transfer: transfer);
        version.VideoTracks![0].ColorPrimaries = primaries;
        version.VideoTracks[0].ColorSpace = matrix;
        Assert.Equal(expected, Claimed(Start(version)));
    }

    [Theory]
    [InlineData(5, 0, "smpte2084", true, false)]
    [InlineData(8, 1, "smpte2084", true, true)]
    [InlineData(8, 4, "arib-std-b67", true, true)]
    [InlineData(8, 2, "bt709", true, false)]
    [InlineData(8, 0, "smpte2084", true, false)]
    [InlineData(8, null, "smpte2084", true, false)]
    [InlineData(8, 4, "smpte2084", true, false)]
    [InlineData(8, 1, "arib-std-b67", true, false)]
    [InlineData(8, 1, "smpte2084", false, false)]
    [InlineData(7, 1, "smpte2084", true, true)]
    [InlineData(7, 0, "smpte2084", true, true)]
    [InlineData(7, null, "smpte2084", true, true)]
    [InlineData(7, 4, "arib-std-b67", true, false)]
    [InlineData(7, 1, "smpte2084", false, false)]
    [InlineData(9, 1, "smpte2084", true, false)]
    [InlineData(null, 1, "smpte2084", true, false)]
    public void DolbyVisionRequiresACompatibleBaseLayer(int? profile, int? compatibility, string transfer, bool baseLayer, bool expected)
    {
        var version = Version(transfer: transfer);
        var track = version.VideoTracks![0];
        track.DolbyVision = "Dolby Vision";
        track.DvConfigPresent = true;
        track.DvBlCompatIdPresent = true;
        track.DvProfile = profile;
        track.DvBlCompatId = compatibility;
        track.DvBlPresent = baseLayer;
        Assert.Equal(expected, Claimed(Start(version)));
        track.Codec = "vp9";
        Assert.False(Claimed(Start(version)));
    }

    [Fact]
    public void MissingInventoryAndInsufficientHardwareEvidenceFailClosed()
    {
        var version = Version();
        version.VideoTracks = null;
        Assert.False(Claimed(Start(version)));
        version.VideoTracks = [];
        Assert.False(Claimed(Start(version)));
        version.VideoTracks = [.. Version().VideoTracks!, .. Version().VideoTracks!];
        Assert.False(Claimed(Start(version)));
        Assert.False(Claimed(Start(Version(), Device with { VideoDecode = [] })));
        Assert.False(Claimed(Start(Version(), Device with { VideoDecode = [Device.VideoDecode[0]] })));
        version = Version();
        version.VideoTracks![0].Width = 7680;
        Assert.False(Claimed(Start(version)));
        Assert.False(Claimed(PlaybackStartBuilder.Build("file", "profile", "attempt", null,
            PlaybackSelection.Auto, Device, PlaybackTestSupport.Identity)));
    }

    [Fact]
    public void ClaimsFollowTheResolvedVersionIncludingExplicitAndLastPlayedPicks()
    {
        var hdr = Version();
        var av1 = Version("av1");
        av1.FileId = "other";
        var detail = Detail(hdr, av1) with { LastFileId = "other" };
        PlaybackStartRequestV3 Build(PlaybackSelection selection) => PlaybackStartBuilder.Build(detail,
            selection, "profile", "attempt", null, Device, PlaybackTestSupport.Identity);
        Assert.False(Claimed(Build(PlaybackSelection.Auto)));
        Assert.True(Claimed(Build(new("file"))));
        Assert.False(Claimed(Build(new("other"))));
    }

    [Theory]
    [InlineData("yuv420p10le", true)]
    [InlineData("p010le", true)]
    [InlineData("yuv422p10le", false)]
    [InlineData("yuv444p10le", false)]
    public void KnownPixelFormatsMustFitP010HardwareDecoding(string format, bool expected)
    {
        var version = Version();
        version.VideoTracks![0].PixelFormat = format;
        Assert.Equal(expected, Claimed(Start(version)));
    }

    [Fact]
    public void DolbyVisionPresenceFactsCannotBeMissingOrContradictory()
    {
        var version = Version();
        var track = version.VideoTracks![0];
        track.DvProfile = 8;
        track.DvBlCompatId = 1;
        track.DvBlPresent = true;
        track.DvConfigPresent = true;
        track.DvBlCompatIdPresent = true;
        Assert.True(Claimed(Start(version)));
        track.DvConfigPresent = null;
        Assert.False(Claimed(Start(version)));
        track.DvConfigPresent = true;
        track.DvBlCompatIdPresent = null;
        Assert.False(Claimed(Start(version)));
        track.DvBlCompatIdPresent = true;
        track.DvElPresent = true;
        Assert.False(Claimed(Start(version)));
        // Both profile 7 MEL and FEL discard enhancement pixels and use the HDR10 base.
        track.DvProfile = 7;
        foreach (var enhancement in new[] { "mel", "fel" })
        {
            track.DvEnhancementLayer = enhancement;
            Assert.True(Claimed(Start(version)));
        }
    }

    [Theory]
    [InlineData("2.1.0-bale.25", false)]
    [InlineData("2.1.0-bale.28", false)]
    [InlineData("2.1.0-bale.29", true)]
    [InlineData("2.1.0-bale.30", true)]
    [InlineData("2.1.0-bale.29-beta", false)]
    [InlineData("2.1.0", false)]
    public void ProductionStageRequiresTheHlgCapableWrapper(string version, bool supported) =>
        Assert.Equal(supported, ConsoleToneMapPolicy.WrapperSupported(version));

    [Theory]
    [InlineData("hdr10")]
    [InlineData("hdr10plus")]
    [InlineData("hlg")]
    [InlineData("dolby_vision")]
    public void HdrOriginalsOnSdrAlwaysUseTheToneMapRouteEvenWithoutTheDecisionReason(string range)
    {
        var plan = Plan(range);
        var route = PlaybackRoutePolicy.Decide(plan, false, sdrOutput: true);
        Assert.True(route.UseFfmpeg);
        Assert.True(route.ConsoleToneMap);
        Assert.False(route.SoftwareVideo);
        Assert.False(PlaybackRoutePolicy.Decide(plan, false, sdrOutput: false).ConsoleToneMap);
        Assert.False(PlaybackRoutePolicy.Decide(plan, false, sdrOutput: false).UseFfmpeg); // MP4 + AAC stays native on HDR.
        Assert.True(PlaybackRoutePolicy.Decide(plan, false, ffmpegEnabled: false, sdrOutput: true).ConsoleToneMap);
        plan.Delivery = "server_transcode_hls";
        Assert.False(PlaybackRoutePolicy.Decide(plan, true, sdrOutput: true).ConsoleToneMap);
        plan.Delivery = "progressive";
        Assert.False(PlaybackRoutePolicy.Decide(plan, false, sdrOutput: true).ConsoleToneMap);
    }

    [Fact]
    public void ExistingDolbyVision82SdrBaseLayerDoesNotNeedAToneMap()
    {
        var plan = Plan("dolby_vision");
        plan.DecisionReason = PlaybackProtocol.ClientDolbyVision8BaseLayerReason;
        plan.Source!.DolbyVisionProfile = 8;
        plan.Source.DvBlCompatId = 2;
        plan.EffectiveRecipe = new() { DynamicRange = "sdr" };
        var route = PlaybackRoutePolicy.Decide(plan, false, sdrOutput: true);
        Assert.True(route.UseFfmpeg);
        Assert.True(route.DolbyVisionBaseLayer);
        Assert.False(route.ConsoleToneMap);
        Assert.Equal(VideoDecoderMode.ForceSystemDecoder, FfmpegSource.Configuration(route).Video.VideoDecoderMode);
    }

    [Fact]
    public void DecisionReasonForcesTheRouteEvenIfRangeFactsAreAbsent()
    {
        var plan = Plan(null);
        Assert.False(PlaybackRoutePolicy.Decide(plan, false, sdrOutput: true).ConsoleToneMap);
        plan.DecisionReason = PlaybackProtocol.ClientManagedDynamicRangeReason;
        Assert.True(PlaybackRoutePolicy.Decide(plan, false, sdrOutput: true).ConsoleToneMap);
        Assert.Equal("SDR / console tone map", DebugHdrPlayback.Output(false, plan, consoleToneMap: true));
    }

    [Theory]
    [InlineData("hevc")]
    [InlineData("vp9")]
    public void FailedMappingDropsOnlyTheRangeClaimAndPreservesRecoveryAndSeekContext(string codec)
    {
        var start = Start(Version(codec)) with { Metered = true, BandwidthCapKbps = 12000 };
        var plan = Plan("hdr10");
        plan.Source!.VideoCodec = codec;
        plan.SelectedTracks = new() { Audio = new() { Id = "chosen-audio", Index = 1 } };
        var route = PlaybackRoutePolicy.Decide(plan, false, sdrOutput: true);
        var request = PlaybackReplan.ForFailure(start, plan, "mount", ["prior"], 1, 125,
            PlaybackReplan.Classify(unchecked((int)0x80070057), true, false), route);
        Assert.DoesNotContain(PlaybackProtocol.ClientManagedDynamicRangeClaim,
            request.ClientPlaybackContext.Deliveries["original_http"].ValidatedClaims);
        Assert.Contains(PlaybackProtocol.ClientSelectedAudioTrackClaim, request.ClientPlaybackContext.Deliveries["original_http"].ValidatedClaims);
        Assert.Same(start.ClientCapabilities, request.ClientCapabilities);
        Assert.Same(start.ClientFeatures, request.ClientFeatures);
        Assert.Same(start.ClientPlaybackContext.Output, request.ClientPlaybackContext.Output);
        Assert.Equal("Console tone mapping failed; server tone mapping required.", request.Failure!.Message);
        Assert.Equal(new[] { "prior", "key" }, request.AttemptedPlanKeys);
        Assert.Equal("plan", request.FailedPlanId);
        Assert.Equal("mount", request.PlanAttemptId);
        Assert.Equal("attempt", request.PlaybackAttemptId);
        Assert.Equal(125, request.PositionSeconds);
        Assert.Equal(new PlaybackTrackIdentityV3("chosen-audio", 1), request.SelectedTracks.Audio);
        Assert.Equal(12000, request.BandwidthCapKbps);
        Assert.True(request.Metered);
        Assert.True(Claimed(start));
        Assert.Contains(PlaybackProtocol.ClientManagedDynamicRangeClaim,
            PlaybackReplan.ForSeek(start, plan, "mount", [], 1, 130).ClientPlaybackContext.Deliveries["original_http"].ValidatedClaims);
        Assert.DoesNotContain(PlaybackProtocol.ClientManagedDynamicRangeClaim,
            PlaybackReplan.ForHdrOutputFailure(start, plan, "mount", [], 1, 130).ClientPlaybackContext.Deliveries["original_http"].ValidatedClaims);
    }

    [Theory]
    [InlineData(null, null, "Automatic", "1", true, true)]
    [InlineData("Auto", null, "Automatic", "1", true, true)]
    [InlineData("Auto", "Bt2390", "Automatic", "1", true, true)]
    [InlineData("Auto", "Hable", "Automatic", "2", true, true)]
    [InlineData("Auto", "Off", "Automatic", "0", false, false)]
    [InlineData("System", null, "ForceSystemDecoder", null, false, false)]
    [InlineData("Software", null, "ForceFFmpegSoftwareDecoder", null, false, false)]
    public void ProductionConfigurationAndDebugOverridesAgreeWithClaimAndGuard(string? decoder, string? curve,
        string mode, string? option, bool claim, bool guard)
    {
        var tuning = new DebugFfmpegInputTuning(Decoder: decoder is null ? null : Enum.Parse<DebugFfmpegDecoder>(decoder),
            ToneMap: curve is null ? null : Enum.Parse<DebugToneMap>(curve));
        Assert.Equal(claim, Claimed(Start(Version(), tuning.ToneMapCapabilities(Device))));
        var route = PlaybackRoutePolicy.Decide(Plan("hdr10"), false, sdrOutput: true);
        var config = FfmpegSource.Configuration(route, tuning);
        Assert.Equal(mode, config.Video.VideoDecoderMode.ToString());
        Assert.Equal(option, config.FFmpegOptions.GetValueOrDefault("bale_tonemap"));
        Assert.Equal(guard, tuning.RequiresToneMapGuard(route));
        Assert.DoesNotContain("bale_zero_copy", config.FFmpegOptions.Keys);
        if (decoder is null)
            Assert.Equal("1", FfmpegSource.Configuration(route).FFmpegOptions["bale_tonemap"]);
    }

    [Fact]
    public void ProductionRejectsSoftwareAndSystemDecoderFallbacksOnTheToneMapRoute()
    {
        var route = PlaybackRoutePolicy.Decide(Plan("hdr10"), false, sdrOutput: true);
        foreach (var engine in Enum.GetValues<DecoderEngine>())
        {
            var source = new FFmpegMediaSource();
            source.VideoStreams.Add(new() { CodecName = "hevc", DecoderEngine = engine });
            var error = Record.Exception(() => FfmpegSource.ValidateVideoDecoders(source, route));
            if (engine == DecoderEngine.FFmpegD3D11HardwareDecoder) Assert.Null(error);
            else Assert.IsType<InvalidDataException>(error);
        }
        Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(route with { VideoCodec = "av1" }));
    }

    [Fact]
    public void GuardRejectsADeclinedStageBeforePresentingAndDetachesOnDispose()
    {
        var source = new FFmpegMediaSource();
        source.VideoStreams.Add(new() { DecoderEngine = DecoderEngine.FFmpegD3D11HardwareDecoder });
        var failures = 0;
        using var guard = new FfmpegToneMapGuard(source, () => failures++);
        source.Source.Request(new object()); // Audio descriptor is not a video failure.
        var format = SdrFormat();
        Assert.NotNull(source.Source.Request(new VideoStreamDescriptor { EncodingProperties = format }).Sample);
        Assert.Equal(0, failures);
        // A container tagged BT.709 over HLG declines the stage; its output remains P010.
        format.Subtype = "P010";
        Assert.Null(source.Source.Request(new VideoStreamDescriptor { EncodingProperties = format }).Sample);
        Assert.Equal(1, source.Source.Errors);
        Assert.Equal(1, failures);
        Assert.Null(source.Source.Request(new VideoStreamDescriptor { EncodingProperties = SdrFormat() }).Sample);
        Assert.Equal(1, failures); // Latch, never recover more than once for a mount.
        guard.Dispose();
        Assert.Equal(0, source.Source.Observers);
    }

    [Fact]
    public void GuardRequiresAllColourAttributesAndKeepsCheckingTheLiveDecoder()
    {
        var format = SdrFormat();
        Assert.True(FfmpegToneMapGuard.IsSdrOutput(format));
        foreach (var key in format.Properties.Keys.ToArray())
        {
            var value = format.Properties[key];
            format.Properties.Remove(key);
            Assert.False(FfmpegToneMapGuard.IsSdrOutput(format));
            format.Properties[key] = value;
        }
        var source = new FFmpegMediaSource();
        source.VideoStreams.Add(new() { DecoderEngine = DecoderEngine.FFmpegSoftwareDecoder });
        var failures = 0;
        using var guard = new FfmpegToneMapGuard(source, () => failures++);
        Assert.Null(source.Source.Request(new VideoStreamDescriptor { EncodingProperties = format }).Sample);
        Assert.Equal(1, failures);
    }

    private static VideoEncodingProperties SdrFormat() => new()
    {
        Subtype = "NV12",
        Properties =
        {
            [new("dbfbe4d7-0740-4ee0-8192-850ab0e21935")] = 2u,
            [new("5fb0fce9-be5c-4935-a811-ec838f8eed93")] = 5u,
            [new("3e23d450-2c75-4d25-a00e-b91670d12327")] = 1u,
            [new("c21b8ee5-b956-4071-8daf-325edf5cab11")] = 2u,
        },
    };

    private static PlaybackPlan Plan(string? range) => new()
    {
        PlanId = "plan", PlanAttemptKey = "key", Delivery = "original_http",
        Source = new() { VideoCodec = "hevc", AudioCodec = "aac", Container = "mp4", DynamicRange = range,
            BitDepth = 10, Width = 1920, Height = 1080 },
    };
}
