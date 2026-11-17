using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackOptionsInfoTests
{
    [Fact]
    public void StreamRowsUseNamesPeopleKnowAndPreferTheRecipe()
    {
        var direct = new PlaybackPlan
        {
            Delivery = "original_http",
            Source = new PlaybackSource { VideoCodec = "hevc", AudioCodec = "eac3", AudioChannels = 6, Height = 2160, DynamicRange = "hdr10" },
        };
        var rows = PlaybackOptionsInfo.StreamRows(direct, "English", "Opening");
        Assert.Equal(new[] { "Playback", "Video", "Audio", "Subtitles", "Chapter" }, rows.Select(row => row.Label));
        Assert.StartsWith("Direct play", rows[0].Value);
        Assert.Equal("HEVC", rows[1].Value);
        Assert.Equal("Dolby Digital Plus · 5.1", rows[2].Value);
        Assert.Equal(new[] { "HDR10", "4K" }, PlaybackOptionsInfo.Badges(direct));

        var transcode = new PlaybackPlan
        {
            Delivery = "server_transcode_hls",
            Source = direct.Source,
            EffectiveRecipe = new EffectiveRecipeV3 { VideoCodec = "h264", AudioCodec = "aac", AudioChannels = 2, Height = 1080 },
        };
        var converted = PlaybackOptionsInfo.StreamRows(transcode, null, null);
        Assert.Equal("H.264", converted.Single(row => row.Label == "Video").Value);
        Assert.Equal("AAC · Stereo", converted.Single(row => row.Label == "Audio").Value);
        Assert.Equal("Off", converted.Single(row => row.Label == "Subtitles").Value);
        Assert.DoesNotContain(converted, row => row.Label == "Chapter");
    }

    [Fact]
    public void TheCurrentChapterIsTheLastStartAtOrBeforeThePosition()
    {
        VersionChapter[] chapters = [new() { StartSeconds = 300 }, new() { StartSeconds = 0, Title = "Opening" }, new() { StartSeconds = 600, Title = " Finale " }];
        Assert.Equal("Opening", PlaybackOptionsInfo.CurrentChapter(chapters, 120));
        Assert.Equal("Chapter 2", PlaybackOptionsInfo.CurrentChapter(chapters, 300));
        Assert.Equal("Finale", PlaybackOptionsInfo.CurrentChapter(chapters, 900));
        Assert.Null(PlaybackOptionsInfo.CurrentChapter([], 10));
    }

    [Theory]
    [InlineData("avc1.640029", "H.264")]
    [InlineData("video/hevc", "HEVC")]
    [InlineData("dvhe.08.06", "Dolby Vision")]
    [InlineData("av01.0.08M.10", "AV1")]
    [InlineData("vc1", "VC-1")]
    [InlineData(null, null)]
    public void VideoCodecShortNames(string? codec, string? expected) => Assert.Equal(expected, PlaybackOptionsInfo.VideoCodecName(codec));
}
