using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackStatsTests
{
    [Fact]
    public void HudRowsKeepTheOfficialOrderWithBaleExtrasLast()
    {
        var stats = new PlaybackStats
        {
            BackendDisplayName = "Windows MediaPlayer",
            BackendRoute = "Direct play",
            SubtitleRendering = "burn_in",
            HardContainers = "mp4, mkv",
            VideoCodec = "HEVC",
            Resolution = "1920x1080",
            FrameRate = 23.976023,
            HdrMode = "SDR",
            VideoDecoderName = "Media Foundation (FullySupported)",
            AudioCodec = "AAC",
            AudioDecoderName = "Media Foundation (FullySupported)",
            EstimatedBandwidthBps = 151_300_000,
            DroppedFrames = 2,
            AudioUnderruns = 1,
            Source = "mkv · hevc · Main 10 · 10-bit · 1920x1080 · aac 6 ch",
            MediaBitrateBps = 19_000_000,
            BufferingCount = 3,
            BufferingSeconds = 2.75,
        };
        Assert.Equal(new (string, string)[]
        {
            ("Backend", "Windows MediaPlayer"),
            ("Route", "Direct play"),
            ("Subtitles", "burn_in"),
            ("Hard containers", "mp4, mkv"),
            ("Video codec", "HEVC"),
            ("Resolution", "1920x1080"),
            ("Frame rate", "23.976 fps"),
            ("HDR mode", "SDR"),
            ("Video decoder", "Media Foundation (FullySupported)"),
            ("Audio codec", "AAC"),
            ("Audio decoder", "Media Foundation (FullySupported)"),
            ("Estimated bandwidth", "151.3 Mbps"),
            ("Dropped frames", "2"),
            ("Audio underruns", "1"),
            ("Source", "mkv · hevc · Main 10 · 10-bit · 1920x1080 · aac 6 ch"),
            ("Media bitrate", "19.0 Mbps"),
            ("Buffering", "3 times, 2.8 s"),
        }, stats.HudRows());
    }

    [Fact]
    public void MissingValuesAndZeroCountersDoNotCreateRows()
    {
        Assert.Empty(new PlaybackStats().HudRows());
        Assert.Empty(new PlaybackStats
        {
            BackendDisplayName = " ",
            VideoCodec = "",
            FrameRate = double.NaN,
            EstimatedBandwidthBps = -1,
            DroppedFrames = -1,
            AudioUnderruns = 0,
            AudioChannelCount = 6,
            AudioSampleRate = 48000,
            MediaBitrateBps = 0,
            BufferingCount = 0,
            BufferingSeconds = 10,
        }.HudRows());
    }

    [Fact]
    public void SparseRowsKeepTheirRelativeOrderAndZeroBandwidthIsAValue()
    {
        var stats = new PlaybackStats { AudioCodec = "AAC", EstimatedBandwidthBps = 0, Source = "mp4" };
        Assert.Equal(new (string, string)[]
        {
            ("Audio codec", "AAC"), ("Estimated bandwidth", "0 bps"), ("Source", "mp4"),
        }, stats.HudRows());
    }

    [Fact]
    public void AudioEncodingDetailsStayWithTheAudioCodecRow()
    {
        var stats = new PlaybackStats { AudioCodec = "AAC", AudioChannelCount = 6, AudioSampleRate = 48000 };
        Assert.Equal(("Audio codec", "AAC · 6 ch · 48000 Hz"), Assert.Single(stats.HudRows()));
    }

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

    [Fact]
    public void AllNumbersUseInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1.3 Mbps", PlaybackStats.FormatBitrate(1_250_000));
            var stats = new PlaybackStats { FrameRate = 59.940059, BufferingCount = 1, BufferingSeconds = 1.25 };
            Assert.Equal(new (string, string)[] { ("Frame rate", "59.940 fps"), ("Buffering", "1 times, 1.2 s") }, stats.HudRows());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("original_http", "Direct play")]
    [InlineData("server_remux_hls", "Remux")]
    [InlineData("server_remux_progressive", "Remux")]
    [InlineData("server_transcode_hls", "Transcode")]
    [InlineData("progressive", "progressive")]
    [InlineData(null, null)]
    public void RouteDescribesDeliveryAndAppendsTheDecisionReason(string? delivery, string? route)
    {
        var plan = new PlaybackPlan { Delivery = delivery, DecisionReason = "source_compatible" };
        Assert.Equal(route is null ? null : route + " (source_compatible)", PlaybackStats.FromPlan(plan).BackendRoute);
        plan.DecisionReason = null;
        Assert.Equal(route, PlaybackStats.FromPlan(plan).BackendRoute);
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
        Assert.Equal("burn_in", stats.SubtitleRendering);
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
        Assert.Equal("Direct play", stats.BackendRoute);
        Assert.Equal("mkv · hevc", stats.Source);
        Assert.DoesNotContain(stats.HudRows(), row => row.Value.Contains("secret", StringComparison.Ordinal));
    }
}
