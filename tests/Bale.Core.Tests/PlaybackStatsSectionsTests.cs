using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>
/// The Stats tab (owner, 2026-11-27): Apple's tvOS sections and labels, Apple's value formats, and no row for a value Bale
/// does not measure.
/// </summary>
public class PlaybackStatsSectionsTests
{
    private static PlaybackStats Full() => new()
    {
        BackendDisplayName = "FFmpegInteropX",
        BackendRoute = "FFmpeg Direct",
        PlayMethod = "Direct play",
        Container = "MKV",
        Notes = ["container not supported"],
        Subtitles = "English",
        Phase = PlaybackStatsPhase.Playing,
        EstimatedBandwidthBps = 12_300_000,
        DroppedFrames = 2,
        Smoothness = new PlaybackSmoothnessSnapshot(2, 20, 2.5, 1, 0.3, 0.3, 1, 0.3, 0, 0, 10, 9.7),
        Delivery = "Server Remux HLS",
        VideoDecoderName = "Media Foundation (FullySupported)",
        AudioDecoderName = "FFmpeg (AAC)",
        VideoCodec = "hevc",
        Resolution = PlaybackStats.Dimensions(3840, 2160),
        FrameRate = 23.976023,
        HdrMode = "HDR10",
        AudioCodec = "eac3",
        AudioChannelCount = 6,
        Decision = "Direct play (container not supported)",
        Source = "mkv · hevc · 3840x2160",
        MediaBitrateBps = 19_000_000,
        BufferingCount = 3,
        BufferingSeconds = 2.5,
    };

    private static IReadOnlyList<(string Label, string Value)> Section(IReadOnlyList<PlaybackStatsSection> sections, string id) =>
        Assert.Single(sections, section => section.Id == id).Rows;

    [Fact]
    public void SectionsFollowAppleOrderAndTitles()
    {
        var sections = Full().OfficialSections();
        Assert.Equal(new (string Id, string Title)[] { ("playback", "Playback"), ("stream", "Stream"), ("engine", "Engine") },
            sections.Select(section => (section.Id, section.Title)));
    }

    [Fact]
    public void PlaybackRowsUseAppleLabelsAndValueFormats()
    {
        Assert.Equal(new (string, string)[]
        {
            ("Playback", "Direct play · MKV"),
            ("Video", "HEVC · 3840×2160 · 23.976 fps"),
            ("Dynamic range", "HDR10"),
            ("Audio", "EAC3 5.1"),
            ("Subtitles", "English"),
            ("Diagnostics", "Container not supported"),
        }, Section(Full().OfficialSections(), "playback"));
    }

    [Fact]
    public void StreamRowsNameThePhaseAndOnlyMeasuredNumbers()
    {
        Assert.Equal(new (string, string)[]
        {
            ("Playback status", "Playing"),
            ("Network throughput", "12.3 Mbps"),
            ("Dropped frames", "2"),
            ("Missed frames", "20 (2.5%)"),
        }, Section(Full().OfficialSections(), "stream"));
    }

    [Fact]
    public void EngineRowsNameTheRouteDeliveryBackendAndDecoders()
    {
        Assert.Equal(new (string, string)[]
        {
            ("Route", "FFmpeg Direct"),
            ("Delivery", "Server Remux HLS"),
            ("Backend", "FFmpegInteropX"),
            ("Video", "Media Foundation (FullySupported)"),
            ("Audio", "FFmpeg (AAC)"),
        }, Section(Full().OfficialSections(), "engine"));
    }

    [Fact]
    public void DebugOverlayRowsAreTheOfficialRowsThenBaleDiagnostics()
    {
        var official = Full().OfficialSections().SelectMany(section => section.Rows).ToArray();
        var rows = Full().HudRows();
        Assert.Equal(official, rows.Take(official.Length));
        Assert.Equal(new (string, string)[]
        {
            ("Source", "mkv · hevc · 3840x2160"),
            ("Media bitrate", "19.0 Mbps"),
            ("Buffering", "3 times, 2.5 s"),
        }, rows.Skip(official.Length));
        Assert.DoesNotContain(official, row => row.Label is "Source" or "Media bitrate" or "Buffering");
    }

    [Fact]
    public void RowsWithoutAMeasuredValueAreLeftOutAndEmptySectionsDisappear()
    {
        // Missed frames needs a frame rate (MissedFrames), and the Stream section has no other measured value here.
        var stats = new PlaybackStats
        {
            BackendRoute = "Native Direct",
            Smoothness = new PlaybackSmoothnessSnapshot(0, null, null, 0, 0, 0, 0, 0, 0, 0, 1, 1),
        };
        var sections = stats.OfficialSections();
        Assert.Equal(new[] { "engine" }, sections.Select(section => section.Id));
        Assert.Equal(new (string, string)[] { ("Route", "Native Direct") }, Section(sections, "engine"));
        Assert.Empty(new PlaybackStats().OfficialSections());
        Assert.Empty(new PlaybackStats().HudRows());
    }

    [Fact]
    public void DiagnosticsCarryTheReasonAndTheWarningsUnderOneLabel()
    {
        var stats = new PlaybackStats { PlayMethod = "Direct play", Notes = ["container not supported", "audio converted"] };
        Assert.Equal(new (string, string)[]
        {
            ("Playback", "Direct play"),
            ("Diagnostics", "Container not supported"),
            (string.Empty, "Audio converted"),
        }, Section(stats.OfficialSections(), "playback"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1.0, null)]
    [InlineData(1.0004, null)]
    [InlineData(double.NaN, null)]
    [InlineData(0.0, null)]
    [InlineData(1.25, "1.25×")]
    [InlineData(0.5, "0.5×")]
    public void PlaybackRateRowAppearsOnlyWhileItIsNotOneTimesNormal(double? rate, string? expected)
    {
        var playback = Section(new PlaybackStats { PlayMethod = "Direct play", PlaybackRate = rate }.OfficialSections(), "playback");
        Assert.Equal(expected, playback.SingleOrDefault(row => row.Label == "Playback rate").Value);
    }

    [Theory]
    [InlineData("hevc", "HEVC")]
    [InlineData("h264", "H.264")]
    [InlineData("av01", "AV1")]
    [InlineData("vp9", "VP9")]
    [InlineData("mpeg2video", "MPEG-2")]
    [InlineData("mp4v", "MPEG-4")]
    [InlineData("vc1", "VC-1")]
    [InlineData("theora", "THEORA")]
    public void VideoCodecNamesAreApples(string codec, string expected) =>
        Assert.Equal(expected, PlaybackStats.VideoCodecLabel(codec));

    [Theory]
    [InlineData("eac3", "EAC3")]
    [InlineData("ac-3", "AC3")]
    [InlineData("aac_latm", "AAC")]
    [InlineData("truehd", "TrueHD")]
    [InlineData("dts-hd", "DTS")]
    [InlineData("opus", "OPUS")]
    public void AudioCodecNamesAreAppleTrackLabels(string codec, string expected) =>
        Assert.Equal(expected, PlaybackStats.AudioCodecLabel(codec));

    [Theory]
    [InlineData(1L, "Mono")]
    [InlineData(2L, "Stereo")]
    [InlineData(6L, "5.1")]
    [InlineData(8L, "7.1")]
    [InlineData(3L, "3 channels")]
    [InlineData(null, null)]
    public void ChannelLayoutsAreAppleNames(long? channels, string? expected) =>
        Assert.Equal(expected, PlaybackStats.ChannelLayoutLabel(channels));

    [Theory]
    [InlineData(23.976023, "23.976 fps")]
    [InlineData(24.0, "24 fps")]
    [InlineData(59.94005994, "59.94 fps")]
    [InlineData(0.0, null)]
    [InlineData(double.NaN, null)]
    public void FrameRatesKeepAtMostThreeDecimalsAndNoTrailingZeros(double fps, string? expected) =>
        Assert.Equal(expected, PlaybackStats.FrameRateLabel(fps));

    [Theory]
    [InlineData("original_http", "Direct play", "Original HTTP")]
    [InlineData("progressive", "Direct play", "Progressive")]
    [InlineData("server_remux_progressive", "Remuxed by the server", "Server Remux")]
    [InlineData("server_remux_hls", "Remuxed by the server", "Server Remux HLS")]
    [InlineData("server_transcode_hls", "Transcoded by the server", "Server Transcode HLS")]
    [InlineData("some_new_delivery", null, "some new delivery")]
    public void DeliveryNamesAreAppleLabels(string delivery, string? playMethod, string label)
    {
        Assert.Equal(playMethod, PlaybackStats.PlayMethodText(delivery));
        Assert.Equal(label, PlaybackStats.DeliveryText(delivery));
    }

    [Theory]
    [InlineData("matroska", "MKV")]
    [InlineData("mkv", "MKV")]
    [InlineData("mpegts", "MPEG-TS")]
    [InlineData("mov", "MOV")]
    [InlineData("mp4", "MP4")]
    [InlineData("webm", "webm")]
    public void ContainersReadAsApple(string container, string expected) =>
        Assert.Equal(expected, PlaybackStats.ContainerText(container));

    [Theory]
    [InlineData(PlaybackStatsPhase.Loading, "Loading")]
    [InlineData(PlaybackStatsPhase.Playing, "Playing")]
    [InlineData(PlaybackStatsPhase.Paused, "Paused")]
    [InlineData(PlaybackStatsPhase.Seeking, "Seeking")]
    [InlineData(PlaybackStatsPhase.Rebuffering, "Rebuffering")]
    [InlineData(PlaybackStatsPhase.Ended, "Ended")]
    [InlineData(PlaybackStatsPhase.Error, "Error")]
    public void PhasesHaveAppleStatusNames(PlaybackStatsPhase phase, string expected) =>
        Assert.Equal(expected, PlaybackStats.PhaseLabel(phase));

    [Fact]
    public void PictureSizesUseTheMultiplicationSign() =>
        Assert.Equal("3840×2160", PlaybackStats.Dimensions(3840, 2160));
}
