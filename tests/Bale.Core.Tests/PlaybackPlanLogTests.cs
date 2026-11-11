using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackPlanLogTests
{
    [Fact]
    public void EveryFieldIsWrittenInAFixedOrder()
    {
        var plan = new PlaybackPlan
        {
            Delivery = "original_http",
            DecisionReason = "quality_device_limit",
            RequestedMediaFileId = "202779",
            EffectiveMediaFileId = "24796",
            ActiveQuality = "1080p",
            DegradationWarnings =
            [
                new DegradationWarningV3 { Code = "quality_reduction_unavailable", Message = "Could not reduce quality." },
                new DegradationWarningV3 { Code = "subtitle_track_unavailable", Message = "Playing without it." },
            ],
            Transformations =
            [
                new TransformationV3 { Name = "server_dv7_to_hdr10", Executor = "server", RecipeVersion = "2", ValidatedClaims = ["hdr10"] },
                new TransformationV3 { Name = "audio_to_aac" },
            ],
        };
        Assert.Equal("plan delivery=original_http reason=quality_device_limit requested=202779 effective=24796 "
            + "quality=1080p warnings=quality_reduction_unavailable,subtitle_track_unavailable "
            + "transformations=server_dv7_to_hdr10,audio_to_aac", PlaybackPlanLog.Describe(plan));
    }

    [Fact]
    public void EmptyFieldsAreOmitted()
    {
        Assert.Equal("plan", PlaybackPlanLog.Describe(null));
        Assert.Equal("plan", PlaybackPlanLog.Describe(new PlaybackPlan()));
        var plan = new PlaybackPlan
        {
            Delivery = "server_transcode_hls",
            DecisionReason = " ",
            RequestedMediaFileId = "7",
            EffectiveMediaFileId = "",
            DegradationWarnings = [],
            Transformations = [new TransformationV3 { Executor = "server" }, new TransformationV3 { Name = "  " }],
        };
        Assert.Equal("plan delivery=server_transcode_hls requested=7", PlaybackPlanLog.Describe(plan));
    }

    [Theory]
    [InlineData("https://example.invalid/video?st=secret")]
    [InlineData("Authorization: Bearer secret")]
    [InlineData("a=b")]
    [InlineData("two\nlines")]
    [InlineData("café")]
    public void UnsafeValuesAreReplacedNotWritten(string unsafeValue)
    {
        var plan = new PlaybackPlan
        {
            Delivery = unsafeValue,
            DecisionReason = unsafeValue,
            RequestedMediaFileId = unsafeValue,
            EffectiveMediaFileId = unsafeValue,
            ActiveQuality = unsafeValue,
            DegradationWarnings = [new DegradationWarningV3 { Code = unsafeValue }, new DegradationWarningV3 { Code = "kept_code" }],
            Transformations = [new TransformationV3 { Name = unsafeValue }],
        };
        Assert.Equal("plan delivery=? reason=? requested=? effective=? quality=? warnings=?,kept_code transformations=?",
            PlaybackPlanLog.Describe(plan));
        Assert.Equal("plan delivery=?", PlaybackPlanLog.Describe(new PlaybackPlan { Delivery = new string('a', 161) }));
    }

    [Fact]
    public void OnlyIdsAreLoggedNeverMessagesUrlsOrSourceDetails()
    {
        var plan = new PlaybackPlan
        {
            Delivery = "original_http",
            PlanId = "plan-secret",
            SessionId = "session-secret",
            PlanAttemptKey = "key-secret",
            DegradationWarnings = [new DegradationWarningV3 { Code = "audio_track_unavailable", Message = "The secret track is not on this file." }],
            Transformations = [new TransformationV3 { Name = "video_to_h264", Executor = "secret", RecipeVersion = "secret", ValidatedClaims = ["secret"] }],
            RuntimeCorrections = ["secret"],
            AppliedQuirks = [new AppliedQuirkV3 { Id = "secret", Reason = "secret" }],
            Source = new PlaybackSource { Container = "secret", VideoCodec = "secret" },
            Stream = new StreamV3 { Url = "https://example.invalid/video?st=secret", Protocol = "secret" },
        };
        var line = PlaybackPlanLog.Describe(plan);
        Assert.Equal("plan delivery=original_http warnings=audio_track_unavailable transformations=video_to_h264", line);
        Assert.DoesNotContain("secret", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LongListsAreBounded()
    {
        var plan = new PlaybackPlan
        {
            DegradationWarnings = [.. Enumerable.Range(1, 11).Select(i => new DegradationWarningV3 { Code = $"w{i}" })],
        };
        Assert.Equal("plan warnings=w1,w2,w3,w4,w5,w6,w7,w8,+3", PlaybackPlanLog.Describe(plan));
    }
}
