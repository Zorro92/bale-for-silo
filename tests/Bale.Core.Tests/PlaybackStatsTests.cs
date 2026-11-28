using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackStatsTests
{
    [Theory]
    [InlineData(null, 7, null)]
    [InlineData("  hevc \n main10 ", 80, "hevc main10")]
    [InlineData("abcdefghij", 7, "abcd...")]
    public void CompactValueKeepsOneBoundedLine(string? text, int maxLength, string? expected) =>
        Assert.Equal(expected, PlaybackStats.CompactValue(text, maxLength));

    [Theory]
    [InlineData(0, "0 bps")]
    [InlineData(999, "999 bps")]
    [InlineData(1000, "1 Kbps")]
    [InlineData(12500, "13 Kbps")]
    [InlineData(999999, "1000 Kbps")]
    [InlineData(1000000, "1.0 Mbps")]
    [InlineData(1250000, "1.3 Mbps")]
    [InlineData(151300000, "151.3 Mbps")]
    public void BitrateMatchesTheOfficialThresholdsAndRounding(long bps, string expected) =>
        Assert.Equal(expected, PlaybackStats.FormatBitrate(bps));

    [Theory]
    [InlineData("original_http", "Direct play", "Native Direct")]
    [InlineData("server_remux_hls", "Remux", "HLS")]
    [InlineData("server_remux_progressive", "Remux", "Native Direct")]
    [InlineData("server_transcode_hls", "Transcode", "HLS")]
    [InlineData("progressive", "Direct play", "Native Direct")]
    [InlineData(null, null, null)]
    public void BackendRouteIsSeparateFromDeliveryAndItsDecisionReason(string? delivery, string? decision, string? route)
    {
        var plan = new PlaybackPlan { Delivery = delivery, DecisionReason = "subtitle_burn_in_required" };
        var stats = PlaybackStats.FromPlan(plan);
        Assert.Equal(route, stats.BackendRoute);
        Assert.Equal(decision is null ? null : decision + " (subtitles burned in)", stats.Decision);
        plan.DecisionReason = null;
        stats = PlaybackStats.FromPlan(plan);
        Assert.Equal(route, stats.BackendRoute);
        Assert.Equal(decision, stats.Decision);
    }

    [Theory]
    [InlineData("subtitle_burn_in_required", "subtitles burned in")]
    [InlineData("hls_audio_adaptation", "audio converted")]
    [InlineData("quality_bandwidth_limit", "connection too slow")]
    [InlineData(" Quality_Device_Limit ", "above max resolution")]
    [InlineData("some_future_reason", "some future reason")]
    [InlineData("validated_original_playback", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DecisionReasonsReadAsText(string? code, string? expected) =>
        Assert.Equal(expected, PlaybackStats.DescribeDecisionReason(code));

    [Fact]
    public void EveryServerDecisionReasonHasReadableText()
    {
        // Every decision_reason silo-server's plan_v3.go can set; none may fall back to the raw code.
        string[] codes =
        [
            "client_managed_dynamic_range", "client_dv8_base_layer", "client_dv7_to_dv81", "client_dv7_to_hdr10",
            "container_normalization", "audio_adaptation", "hls_audio_adaptation", "hls_packaging_required",
            "subtitle_burn_in_required", "evidence_insufficient_for_direct", "quality_auto_source",
            "quality_original", "quality_fixed_rung", "quality_device_limit", "quality_bandwidth_limit",
            "quality_bandwidth_cap",
        ];
        foreach (var code in codes)
        {
            var text = PlaybackStats.DescribeDecisionReason(code);
            Assert.False(string.IsNullOrEmpty(text), code);
            Assert.NotEqual(code.Replace('_', ' '), text);
        }
        Assert.Equal("Direct play", PlaybackStats.FromPlan(new PlaybackPlan
            { Delivery = "original_http", DecisionReason = "validated_original_playback" }).Decision);
    }

    [Fact]
    public void RouteAndFrameRateUseTheStreamProtocolOrActualTransport()
    {
        var plan = new PlaybackPlan
        {
            Delivery = "original_http",
            Stream = new StreamV3 { Protocol = "HLS" },
            Source = new PlaybackSource { FrameRate = 23.976 },
            EffectiveRecipe = new EffectiveRecipeV3 { FrameRate = 24 },
        };
        var stats = PlaybackStats.FromPlan(plan);
        Assert.Equal("HLS", stats.BackendRoute);
        Assert.Equal(24, stats.FrameRate);
        stats = PlaybackStats.FromPlan(plan, isHls: false);
        Assert.Equal("Native Direct", stats.BackendRoute);
        Assert.Equal(23.976, stats.FrameRate);
        plan.Stream = null;
        stats = PlaybackStats.FromPlan(plan, isHls: true);
        Assert.Equal("HLS", stats.BackendRoute);
        Assert.Equal(24, stats.FrameRate);
    }

    [Theory]
    [InlineData(true, null, 23.976, 30.0, 23.976)]
    [InlineData(true, 24.0, 23.976, 30.0, 24.0)]
    [InlineData(true, 24.0, null, 30.0, 24.0)]
    [InlineData(true, null, null, 30.0, null)]
    [InlineData(true, null, 23.976, null, 23.976)]
    [InlineData(false, 24.0, 23.976, 30.0, 30.0)]
    [InlineData(false, 24.0, 23.976, null, 23.976)]
    [InlineData(false, 24.0, null, null, null)]
    [InlineData(false, null, null, 30.0, 30.0)]
    public void HlsUsesPlanFrameRateWhileDirectPlayPrefersTheVideoTrack(bool isHls,
        double? recipeFrameRate, double? sourceFrameRate, double? trackFrameRate, double? expected)
    {
        var plan = new PlaybackPlan
        {
            Delivery = isHls ? "server_transcode_hls" : "original_http",
            EffectiveRecipe = new EffectiveRecipeV3 { FrameRate = recipeFrameRate },
            Source = new PlaybackSource { FrameRate = sourceFrameRate },
        };
        Assert.Equal(expected, PlaybackStats.ResolveFrameRate(plan, isHls, trackFrameRate));
        Assert.Equal(isHls ? recipeFrameRate ?? sourceFrameRate : sourceFrameRate, PlaybackStats.FromPlan(plan).FrameRate);
    }

    [Fact]
    public void PlanStatsSeparateSourceAndDeliveredBitrateAndPreferTheDeliveredHdrMode()
    {
        var plan = new PlaybackPlan
        {
            Source = new PlaybackSource
            {
                Container = "mkv", VideoCodec = "hevc", VideoProfile = "Main 10", BitDepth = 10,
                Width = 3840, Height = 2160, AudioCodec = "eac3", AudioChannels = 6,
                DynamicRange = "hdr10", BitrateKbps = 50000,
            },
            EffectiveRecipe = new EffectiveRecipeV3 { DynamicRange = "sdr", BitrateKbps = 19000 },
            Subtitle = new SubtitleDecisionV3 { Mode = "burn_in" },
        };
        var stats = PlaybackStats.FromPlan(plan);
        Assert.Equal("mkv · hevc · Main 10 · 10-bit · 3840x2160 · eac3 6 ch", stats.Source);
        Assert.Equal("SDR", stats.HdrMode);
        Assert.DoesNotContain(stats.HudRows(), row => row.Label == "Subtitles");
        Assert.Equal(19_000_000, stats.MediaBitrateBps);
        Assert.Null(stats.EstimatedBandwidthBps);
        Assert.Null(stats.VideoCodec); // Runtime codec/resolution must come from the opened item.
        Assert.Null(stats.Resolution);
        plan.EffectiveRecipe = null;
        stats = PlaybackStats.FromPlan(plan);
        Assert.Equal("HDR10", stats.HdrMode);
        Assert.Null(stats.MediaBitrateBps); // Never silently substitute the source bitrate.
    }

    [Fact]
    public void MissingPlanMetadataDoesNotInventSourceDetails()
    {
        var stats = PlaybackStats.FromPlan(new PlaybackPlan());
        Assert.Equal("SDR", stats.HdrMode);
        Assert.Null(stats.Source);
        Assert.Null(stats.BackendRoute);
        Assert.Null(stats.Decision);
        Assert.Null(stats.FrameRate);
        Assert.Null(stats.MediaBitrateBps);
        Assert.Null(PlaybackStats.FromPlan(new PlaybackPlan { EffectiveRecipe = new EffectiveRecipeV3 { BitrateKbps = long.MaxValue } }).MediaBitrateBps);
    }

    [Fact]
    public void PlanDiagnosticsNeverCopyRequestDataOrUnexpectedFreeFormMetadata()
    {
        var plan = new PlaybackPlan
        {
            Delivery = "original_http",
            DecisionReason = "failed https://example.invalid/video?st=secret",
            Source = new PlaybackSource
            {
                Container = "mkv", VideoCodec = "hevc", VideoProfile = "Authorization: Bearer secret",
                AudioCodec = "st=secret",
            },
            Stream = new StreamV3 { Url = "https://example.invalid/video?st=secret" },
        };
        var stats = PlaybackStats.FromPlan(plan);
        Assert.Equal("Native Direct", stats.BackendRoute);
        Assert.Equal("Direct play", stats.Decision);
        Assert.Equal("mkv · hevc", stats.Source);
        Assert.DoesNotContain(stats.HudRows(), row => row.Value.Contains("secret", StringComparison.Ordinal));
    }

    private static readonly FileVersion[] SwapVersions =
    [
        new() { FileId = "202779", Resolution = "4K", CodecVideo = "dvhe.05.06" },
        new() { FileId = "24796", Resolution = "1080p", CodecVideo = "h264" },
        new() { FileId = "24797", Resolution = "1080p", CodecVideo = "hevc" },
        new() { FileId = "unlabelled" },
        new() { FileId = "odd", Resolution = "see https://example.invalid/?st=secret" },
    ];

    [Fact]
    public void DecisionNamesTheVersionTheServerPlayedInstead()
    {
        // The real case: a 4K Dolby Vision pick on an SDR display, played from the 1080p version.
        var plan = new PlaybackPlan { Delivery = "original_http", RequestedMediaFileId = "202779", EffectiveMediaFileId = "24796" };
        Assert.Equal("Direct play · 1080P version instead of 4K · DV", PlaybackStats.FromPlan(plan, versions: SwapVersions).Decision);
        Assert.Contains(("Diagnostics", "1080P version instead of 4K · DV"), PlaybackStats.FromPlan(plan, versions: SwapVersions).HudRows());
        // The decision reason keeps its place.
        plan.DecisionReason = "quality_device_limit";
        Assert.Equal("Direct play (above max resolution) · 1080P version instead of 4K · DV",
            PlaybackStats.FromPlan(plan, isHls: false, SwapVersions).Decision);
        // Same readout label on both sides.
        plan = new PlaybackPlan { Delivery = "server_transcode_hls", RequestedMediaFileId = "24797", EffectiveMediaFileId = "24796" };
        Assert.Equal("Transcode · other 1080P version played", PlaybackStats.FromPlan(plan, versions: SwapVersions).Decision);
    }

    [Theory]
    [InlineData("202779", "missing")]
    [InlineData("missing", "24796")]
    [InlineData("202779", "unlabelled")]
    [InlineData("202779", "odd")]
    public void SwapToOrFromAnUnknownVersionSaysOnlyThatAnotherPlayed(string requested, string effective)
    {
        var plan = new PlaybackPlan { Delivery = "original_http", RequestedMediaFileId = requested, EffectiveMediaFileId = effective };
        Assert.Equal("Direct play · other version played", PlaybackStats.FromPlan(plan, versions: SwapVersions).Decision);
        Assert.Equal("Direct play · other version played", PlaybackStats.FromPlan(plan, versions: []).Decision);
        Assert.Equal("Direct play · other version played", PlaybackStats.FromPlan(plan).Decision);
    }

    [Theory]
    [InlineData("24796", "24796")]   // No swap.
    [InlineData(" 24796", "24796 ")]
    [InlineData(null, "24796")]      // Requested missing: nothing to compare with.
    [InlineData("", "24796")]
    [InlineData("202779", null)]     // Effective missing.
    [InlineData("202779", " ")]
    [InlineData(null, null)]
    public void DeliveryHasNoNoteWithoutASwap(string? requested, string? effective)
    {
        var plan = new PlaybackPlan
        {
            Delivery = "original_http", DecisionReason = "quality_device_limit",
            RequestedMediaFileId = requested, EffectiveMediaFileId = effective,
        };
        Assert.Null(PlaybackStats.DescribeVersionSwap(plan, SwapVersions));
        Assert.Equal("Direct play (above max resolution)", PlaybackStats.FromPlan(plan, versions: SwapVersions).Decision);
    }

    [Fact]
    public void ASwapShowsEvenWithoutAReadableDelivery()
    {
        var plan = new PlaybackPlan { RequestedMediaFileId = "202779", EffectiveMediaFileId = "24796" };
        Assert.Equal("1080P version instead of 4K · DV", PlaybackStats.FromPlan(plan, versions: SwapVersions).Decision);
    }
}
