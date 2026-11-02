using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Tests;

/// <summary>Readout cases ported from TvDetailVersionSelectionTest.kt and TvPlaybackFormattingTest.kt.</summary>
public class PlaybackFormattingTests
{
    [Fact]
    public void AutoDisplayUsesLastPlayedFileBeforeFirstServerVersion()
    {
        var versions = Versions();
        Assert.Same(versions[1], PlaybackFormatting.SelectDisplayVersion(versions, lastFileId: "4k", preferredQuality: "auto"));
        Assert.Same(versions[2], PlaybackFormatting.SelectDisplayVersion(versions, lastFileId: "720", preferredQuality: "original"));
    }

    [Fact]
    public void AutoDisplayUsesPreferredQualityWhenNothingWasSelectedOrLastPlayed()
    {
        var versions = Versions();
        Assert.Same(versions[0], PlaybackFormatting.SelectDisplayVersion(versions, preferredQuality: "1080p"));
        Assert.Same(versions[2], PlaybackFormatting.SelectDisplayVersion(versions, preferredQuality: "720p"));
    }

    [Fact]
    public void ExplicitVersionWinsAndMissingIdsFallBackToAuto()
    {
        var versions = Versions();
        Assert.Same(versions[0], PlaybackFormatting.SelectDisplayVersion(versions, "1080", "4k"));
        Assert.Same(versions[2], PlaybackFormatting.SelectDisplayVersion(versions, "missing", "720"));
        Assert.Same(versions[1], PlaybackFormatting.SelectDisplayVersion(versions, "missing", "missing"));
        Assert.Null(PlaybackFormatting.SelectDisplayVersion([]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData(" Original ")]
    [InlineData("source")]
    [InlineData("max")]
    [InlineData("480p")]
    [InlineData("unknown")]
    public void AutoUsesBestAvailableAndAnUnsatisfiedCapFallsBackToBest(string? quality)
    {
        var versions = Versions();
        Assert.Same(versions[1], PlaybackFormatting.SelectDisplayVersion(versions, preferredQuality: quality));
    }

    [Fact]
    public void ResolutionBitrateAndSizeBreakTiesInThatOrder()
    {
        var versions = new[]
        {
            new FileVersion { FileId = "big-low", Resolution = "720p", Bitrate = 100, FileSize = 100 },
            new FileVersion { FileId = "small", Resolution = "4K", Bitrate = 1, FileSize = 1 },
            new FileVersion { FileId = "bitrate", Resolution = "2160p", Bitrate = 2, FileSize = 1 },
            new FileVersion { FileId = "size", Resolution = "UHD", Bitrate = 2, FileSize = 2 },
        };
        Assert.Same(versions[3], PlaybackFormatting.SelectDisplayVersion(versions));
        Assert.Same(versions[0], PlaybackFormatting.SelectDisplayVersion(versions, preferredQuality: "1080p"));
    }

    [Fact]
    public void NumericResolutionLabelsRankByTheirActualHeight()
    {
        Assert.Equal(540, PlaybackFormatting.ResolutionRank("540p"));
        Assert.Equal(360, PlaybackFormatting.ResolutionRank("360"));
        var versions = new[]
        {
            new FileVersion { Resolution = "480p" },
            new FileVersion { Resolution = "540p" },
            new FileVersion { Resolution = "360p" },
        };
        Assert.Same(versions[1], PlaybackFormatting.SelectDisplayVersion(versions));
    }

    [Theory]
    [InlineData("8K", "8K")]
    [InlineData("4320p", "8K")]
    [InlineData("2160p", "4K")]
    [InlineData("4k", "4K")]
    [InlineData("UHD", "4K")]
    [InlineData("1080p", "1080P")]
    [InlineData("720p", "720P")]
    [InlineData(null, "Auto")]
    [InlineData("  ", "Auto")]
    public void CompactVersionUsesDisplayResolution(string? resolution, string expected) =>
        Assert.Equal(expected, PlaybackFormatting.VersionCompactLabel(new FileVersion { Resolution = resolution }));

    [Fact]
    public void CompactVersionKeepsResolutionAndDynamicRangeOnly()
    {
        var version = new FileVersion { Resolution = "2160p", CodecVideo = "hevc", CodecAudio = "truehd", Hdr = true };
        Assert.Equal("4K · HDR", PlaybackFormatting.VersionCompactLabel(version));
        version.VideoTracks = [new VideoTrack { Codec = "hevc", DolbyVision = "Profile 8", DvProfile = 8 }];
        Assert.Equal("4K · DV", PlaybackFormatting.VersionCompactLabel(version));
        Assert.Equal("Auto", PlaybackFormatting.VersionCompactLabel(null));
    }

    [Theory]
    [InlineData("dvhe.08.06", true)]
    [InlineData("dvh1", true)]
    [InlineData("dva1", true)]
    [InlineData("dvav", true)]
    [InlineData("Dolby Vision", true)]
    [InlineData("hevc", false)]
    [InlineData("dvd", false)]
    public void DolbyVisionDetectionMatchesOfficialCodecTokens(string codec, bool expected) =>
        Assert.Equal(expected, PlaybackFormatting.IsDolbyVision(new FileVersion { CodecVideo = codec }));

    [Fact]
    public void CompactVersionUsesAvailableTrackDimensionsAndRange()
    {
        var version = new FileVersion { VideoTracks = [new VideoTrack { Height = 1080, VideoRangeType = "HDR10" }] };
        Assert.Equal("1080P · HDR", PlaybackFormatting.VersionCompactLabel(version));
        version.VideoTracks[0].VideoRangeType = "Dolby Vision";
        Assert.Equal("1080P · DV", PlaybackFormatting.VersionCompactLabel(version));
        version.VideoTracks[0].VideoRangeType = "SDR";
        Assert.Equal("1080P", PlaybackFormatting.VersionCompactLabel(version));
    }

    private static FileVersion[] Versions() =>
    [
        new FileVersion { FileId = "1080", Resolution = "1080p" },
        new FileVersion { FileId = "4k", Resolution = "2160p" },
        new FileVersion { FileId = "720", Resolution = "720p" },
    ];
}
