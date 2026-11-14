using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public sealed class PlaybackVersionNoticeTests
{
    private static PlaybackSelection Picked => new("picked");
    private static IReadOnlyList<VideoDecodeCapability> Decode => ConsoleEvidence.Default.VideoDecode;
    private static FileVersion Version(string id, string codec, int height) => new()
    {
        FileId = id, CodecVideo = codec, Resolution = height == 2160 ? "4K" : $"{height}p",
        VideoTracks = [new() { Codec = codec, Width = height == 2160 ? 3840 : 1920,
            Height = height, BitDepth = 10, FrameRate = "24", Profile = "main" }],
    };
    private static FileVersion[] Versions => [Version("picked", "av1", 2160), Version("played", "hevc", 1080)];
    private static PlaybackPlan Plan(string effective = "played", string? reason = "validated_original_playback") => new()
    {
        Delivery = "original_http", RequestedMediaFileId = "picked", EffectiveMediaFileId = effective,
        Source = new() { MediaFileId = effective, Height = 1080, VideoCodec = "hevc" }, DecisionReason = reason,
    };

    [Fact]
    public void ExplicitFourKAv1PickAboveTheClaimNamesTheActualVersionAndConsoleLimit()
        => Assert.Equal("Playing 1080p — this console can't play the selected 4K version directly.",
            PlaybackVersionNotice.Describe(Plan(), Picked, Versions, Decode));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AutoStaysQuietEvenThoughStartResolvedARequestedFile(string? fileId)
        => Assert.Null(PlaybackVersionNotice.Describe(Plan(), new(fileId), Versions, Decode));

    [Fact]
    public void SameFileWithATranscodedResolutionDoesNotCountAsAnotherVersion()
    {
        var plan = Plan("picked", "quality_device_limit");
        plan.Delivery = "server_transcode_hls";
        plan.EffectiveRecipe = new() { Height = 720 };
        Assert.Null(PlaybackVersionNotice.Describe(plan, Picked, Versions, Decode));
    }

    [Fact]
    public void EffectiveIdentityWinsOverSourceAndRequestedIds()
    {
        var plan = Plan("picked");
        plan.Source!.MediaFileId = "played";
        Assert.Null(PlaybackVersionNotice.Describe(plan, Picked, Versions));
        plan.EffectiveMediaFileId = "played";
        plan.RequestedMediaFileId = "played"; // A later request does not erase the user's original pick.
        Assert.NotNull(PlaybackVersionNotice.Describe(plan, Picked, Versions));
    }

    [Fact]
    public void OlderPlansCanNameThePlayedFileInSourceButMissingIdentityStaysQuiet()
    {
        var plan = Plan();
        plan.EffectiveMediaFileId = null;
        Assert.NotNull(PlaybackVersionNotice.Describe(plan, Picked, Versions));
        plan.Source!.MediaFileId = null;
        Assert.Null(PlaybackVersionNotice.Describe(plan, Picked, Versions));
    }

    [Fact]
    public void ACodecMissingFromTheClaimsGetsTheConsoleExplanationToo()
    {
        var versions = Versions;
        versions[0] = Version("picked", "unsupported", 2160);
        Assert.Contains("this console can't play", PlaybackVersionNotice.Describe(Plan(), Picked, versions, Decode));
    }

    [Fact]
    public void ADecodeClaimThatFitsDoesNotBlameTheConsoleOrInventServerPolicy()
    {
        var decode = ConsoleEvidence.BuildVideoDecode(av1MaxWidth: 3840, av1MaxHeight: 2160);
        Assert.Equal("Playing 1080p instead of the selected 4K version.",
            PlaybackVersionNotice.Describe(Plan(), Picked, Versions, decode));
        // A successful alternate's validated_original_playback does not carry the 4K-disabled refusal.
        Assert.Equal("Playing 1080p instead of the selected 4K version.",
            PlaybackVersionNotice.Describe(Plan(), Picked, Versions));
    }

    [Theory]
    [InlineData("quality_bandwidth_cap", "limited by the playback bandwidth cap.")]
    [InlineData("quality_bandwidth_limit", "adjusted for the connection speed.")]
    [InlineData("quality_fixed_rung", "using the selected playback quality.")]
    [InlineData("quality_device_limit", "adjusted for this console's video limits.")]
    public void ExplicitQualityConstraintsGetTheirOwnWording(string reason, string explanation)
        => Assert.Equal("Playing 1080p — " + explanation, PlaybackVersionNotice.Describe(Plan(reason: reason), Picked, Versions, Decode));

    [Fact]
    public void BandwidthWarningSurvivesAnotherDecisionReasonWithoutEchoingItsMessage()
    {
        var plan = Plan();
        plan.DegradationWarnings = [new() { Code = "bandwidth_cap_applied", Message = "private-value" }];
        Assert.Equal("Playing 1080p — limited by the playback bandwidth cap.",
            PlaybackVersionNotice.Describe(plan, Picked, Versions, Decode));
    }

    [Fact]
    public void ArbitraryMessagesReasonsAndResolutionLabelsNeverReachTheNotice()
    {
        var plan = Plan(reason: "private-value");
        plan.Source!.Height = null;
        plan.DegradationWarnings = [new() { Code = "private-value", Message = "private-value" }];
        var versions = Versions;
        foreach (var version in versions)
        {
            version.Resolution = "private-value";
            version.VideoTracks = null;
        }
        Assert.Equal("Playing another version instead of the one selected.", PlaybackVersionNotice.Describe(plan, Picked, versions));
    }

    [Fact]
    public void SameResolutionVersionsAndMissingCatalogEntriesAreStillReported()
    {
        var versions = Versions;
        versions[0] = Version("picked", "hevc", 1080);
        Assert.Equal("Playing another 1080p version instead of the one selected.",
            PlaybackVersionNotice.Describe(Plan(), Picked, versions, Decode));
        Assert.Equal("Playing 1080p instead of the selected version.", PlaybackVersionNotice.Describe(Plan(), Picked, []));
    }

    [Fact]
    public void ATranscodedAlternateNamesTheOutputResolution()
    {
        var plan = Plan();
        plan.Delivery = "server_transcode_hls";
        plan.EffectiveRecipe = new() { Height = 720 };
        Assert.StartsWith("Playing 720p", PlaybackVersionNotice.Describe(plan, Picked, Versions));
    }

    [Fact]
    public void NoticeExpiresAndSameFileReanchorsOrRecoveryDoNotRestartIt()
    {
        var notice = new PlaybackVersionNotice();
        var plan = Plan();
        notice.Observe(plan, Picked, Versions, Decode, 1000);
        Assert.True(notice.Visible(1000));
        Assert.True(notice.Visible(5999));
        notice.Observe(plan, Picked, Versions, Decode, 5999);
        Assert.False(notice.Visible(6000));
        notice.Observe(Plan(), Picked, Versions, Decode, 7000);
        Assert.False(notice.Visible(7000));
        Assert.Equal(1000, notice.ShownAtMilliseconds);
    }

    [Fact]
    public void DifferentEffectiveFilesAndReturnToPickedFileUpdateFeedback()
    {
        var notice = new PlaybackVersionNotice();
        notice.Observe(Plan(), Picked, Versions, Decode, 0);
        notice.Observe(Plan("another"), Picked, Versions, Decode, 6000);
        Assert.True(notice.Visible(6000));
        notice.Observe(Plan("picked"), Picked, Versions, Decode, 6100);
        Assert.False(notice.Visible(6100));
        Assert.Null(notice.Text);
        notice.Observe(Plan(), Picked, Versions, Decode, 6200);
        Assert.True(notice.Visible(6200));
    }
}
