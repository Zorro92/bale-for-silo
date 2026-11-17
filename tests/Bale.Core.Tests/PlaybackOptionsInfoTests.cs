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
        Assert.Equal("Dolby Digital Plus · 5.1 → AAC · Stereo", converted.Single(row => row.Label == "Audio").Value);
        Assert.Equal("Off", converted.Single(row => row.Label == "Subtitles").Value);
        Assert.DoesNotContain(converted, row => row.Label == "Chapter");
    }

    [Fact]
    public void AudioComesFromThePlayingTrackWhenThePlanLeavesItOut()
    {
        var plan = new PlaybackPlan { Delivery = "original_http", Source = new PlaybackSource { VideoCodec = "h264" } };
        Assert.DoesNotContain(PlaybackOptionsInfo.StreamRows(plan, null, null), row => row.Label == "Audio");
        var commentary = new AudioTrack { Codec = "aac", Channels = 2 };
        Assert.Equal("AAC · Stereo", PlaybackOptionsInfo.StreamRows(plan, null, null, commentary).Single(row => row.Label == "Audio").Value);

        var remux = new PlaybackPlan
        {
            Delivery = "server_remux_hls",
            Source = new PlaybackSource { VideoCodec = "hevc" },
            EffectiveRecipe = new EffectiveRecipeV3 { AudioCodec = "eac3", AudioChannels = 6 },
        };
        var truehd = new AudioTrack { Codec = "truehd", Channels = 8 };
        Assert.Equal("Dolby TrueHD · 7.1 → Dolby Digital Plus · 5.1",
            PlaybackOptionsInfo.StreamRows(remux, null, null, truehd).Single(row => row.Label == "Audio").Value);
        var direct = new PlaybackPlan
        {
            Delivery = "original_http",
            Source = new PlaybackSource { VideoCodec = "hevc", AudioCodec = "aac", AudioChannels = 2 },
            EffectiveRecipe = new EffectiveRecipeV3 { AudioCodec = "aac", AudioChannels = 2 },
        };
        Assert.Equal("Dolby Digital · Stereo", PlaybackOptionsInfo.StreamRows(direct, null, null,
            new AudioTrack { Codec = "ac3", Channels = 2 }).Single(row => row.Label == "Audio").Value);
        var eac3 = new AudioTrack { Codec = "eac3", Channels = 6 };
        Assert.Equal("Dolby Digital Plus · 5.1", PlaybackOptionsInfo.StreamRows(remux, null, null, eac3).Single(row => row.Label == "Audio").Value);
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
