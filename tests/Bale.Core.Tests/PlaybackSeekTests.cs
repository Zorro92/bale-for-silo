using Bale.Core.Api.Models;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class PlaybackSeekTests
{
    [Theory]
    [InlineData(50, 100.0, false, 40)]
    [InlineData(50, 100.0, true, 80)]
    [InlineData(3, 100.0, false, 0)]
    [InlineData(95, 100.0, true, 100)]
    [InlineData(100, 100.0, true, 100)]
    [InlineData(0, 100.0, false, 0)]
    [InlineData(100, null, true, 130)]
    [InlineData(double.NaN, 100.0, true, 30)]
    public void SkipUsesOfficialDefaultsAndClampsSourceTime(double origin, double? duration, bool forward, double expected) =>
        Assert.Equal(expected, PlaybackSeek.SkipTarget(origin, duration, forward));

    [Fact]
    public void RapidPressesBuildOnEachRequestedTargetNotTheOldMediaClock()
    {
        var target = PlaybackSeek.SkipTarget(50, 500, true);
        target = PlaybackSeek.SkipTarget(target, 500, true);
        target = PlaybackSeek.SkipTarget(target, 500, false);
        Assert.Equal(100, target);
        Assert.Equal(100, new PlaybackSeek(target).PlayerPosition(new(0, 0, 0), 500));
        Assert.Equal(40, new PlaybackSeek(target).PlayerPosition(new(60, 0, 60), 500));
    }

    [Fact]
    public void NativeAndSoftwareVideoUseTheSameAbsoluteSourceMapping()
    {
        var plan = Plan();
        Assert.True(new PlaybackSeek(80).CanSeekInMount(plan, false, true, []));
        Assert.False(new PlaybackSeek(80).CanSeekInMount(plan, false, false, []));
        Assert.Null(new PlaybackSeek(double.NaN).PlayerPosition(new(0, 0, 0), 100));
        Assert.Null(new PlaybackSeek(-1).PlayerPosition(new(0, 0, 0), 100));
    }

    [Fact]
    public void HlsNeedsReanchorBeforeItsOriginOrBeyondItsProducedWindow()
    {
        var plan = Plan();
        plan.Timeline = new TimelineV3 { TimelineOffsetSeconds = 120, SourceStartSeconds = 120, PlayerStartSeconds = 0 };
        Assert.False(new PlaybackSeek(110).CanSeekInMount(plan, true, true, [(0, 40)]));
        Assert.True(new PlaybackSeek(150).CanSeekInMount(plan, true, true, [(0, 40)]));
        Assert.False(new PlaybackSeek(180).CanSeekInMount(plan, true, true, [(0, 40)]));
        Assert.False(new PlaybackSeek(150).CanSeekInMount(plan, true, true, []));
        plan.Timeline.TimelineOffsetSeconds = 100; // The reanchored plan maps the requested target.
        Assert.Equal(10, new PlaybackSeek(110).PlayerPosition(PlaybackTimeline.From(plan.Timeline), 1000));
    }

    [Theory]
    [InlineData(90, 100, 200, false)]
    [InlineData(150, 100, 200, true)]
    [InlineData(210, 100, 200, false)]
    [InlineData(150, 200, 100, false)]
    [InlineData(150, double.NaN, 200, false)]
    public void PublishedSeekBoundsRemainAuthoritative(double target, double start, double end, bool allowed)
    {
        var plan = Plan();
        plan.Timeline = new TimelineV3 { CanSeekAnywhere = true, SeekWindowStartSeconds = start, SeekWindowEndSeconds = end };
        Assert.Equal(allowed, new PlaybackSeek(target).CanSeekInMount(plan, true, true, [(0, 1000)]));
    }

    [Fact]
    public void SeekReanchorRetainsWorkingRouteAndDoesNotSpendFailureBudget()
    {
        var start = Start() with { QualityPreference = "1080p", Metered = true, BandwidthCapKbps = 5000 };
        var request = PlaybackReplan.ForSeek(start, Plan(), "mount", ["previous-failure"], 1, 80);
        Assert.Equal("seek_reanchor", request.Operation);
        Assert.Null(request.Failure);
        Assert.Equal(1, request.AttemptCount);
        Assert.Equal(new[] { "previous-failure" }, request.AttemptedPlanKeys);
        Assert.Equal(80, request.PositionSeconds);
        Assert.Equal("plan", request.FailedPlanId);
        Assert.Equal("route", request.PlanAttemptKey);
        Assert.Same(start.ClientCapabilities, request.ClientCapabilities);
        Assert.Same(start.ClientPlaybackContext, request.ClientPlaybackContext);
        Assert.Equal("1080p", request.QualityPreference);
        Assert.True(request.Metered);
        Assert.Equal(5000, request.BandwidthCapKbps);
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackReplan.ForSeek(start, Plan(), "mount", [], 0, double.NaN));
    }

    [Fact]
    public void ReanchorMayRotateTheTransportAndTimelineButNotTheSessionOrRecipe()
    {
        var current = Plan();
        var next = Plan();
        next.Timeline = new TimelineV3 { SourceStartSeconds = 80, TimelineOffsetSeconds = 80 };
        next.ExpiresAt = "later";
        next.Stream!.Url = "/api/v2/playback/rotated/stream";
        var decision = Decision(next);
        Assert.True(PlaybackSeekReanchor.Matches(current, decision, "session", "file"));
        next.PlanId = "another";
        Assert.False(PlaybackSeekReanchor.Matches(current, decision, "session", "file"));
        next.PlanId = current.PlanId;
        next.EffectiveRecipe!.VideoCodec = "hevc";
        Assert.False(PlaybackSeekReanchor.Matches(current, decision, "session", "file"));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("plan-session")]
    [InlineData("file")]
    [InlineData("track")]
    [InlineData("route")]
    [InlineData("delivery")]
    [InlineData("feature")]
    public void ReanchorRejectsIdentityOrRouteChanges(string mutation)
    {
        var next = Plan();
        var decision = Decision(next);
        switch (mutation)
        {
            case "session": decision.SessionId = "other"; break;
            case "plan-session": next.SessionId = "other"; break;
            case "file": next.EffectiveMediaFileId = "other"; break;
            case "track": next.SelectedTracks!.Audio!.Index = 2; break;
            case "route": next.PlanAttemptKey = "other"; break;
            case "delivery": next.Delivery = "original_http"; break;
            case "feature": decision.ServerFeatures = []; break;
        }
        Assert.False(PlaybackSeekReanchor.Matches(Plan(), decision, "session", "file"));
    }

    [Fact]
    public void RealInputAndDebugInputShareFirstDownAndAbsoluteSeekPaths()
    {
        var app = Read("App.cs.txt");
        Assert.Contains("transport.HandleSkipKey(e.Key, firstDown: !e.KeyStatus.WasKeyDown)", app);
        Assert.Contains("transport.HandleSkipKey(e.Key, firstDown: false)", app);
        var seek = Read("PlayerPage.Seek.cs.txt");
        Assert.Contains("VirtualKey.GamepadLeftTrigger or VirtualKey.J => -1", seek);
        Assert.Contains("VirtualKey.GamepadRightTrigger or VirtualKey.L => 1", seek);
        Assert.Contains("if (firstDown) QueueSkip(direction > 0)", seek);
        Assert.Contains("await SeekAsync(new PlaybackSeek(target))", seek);
        Assert.Contains("_skipTarget ?? SourcePosition(attempt)", seek);
        Assert.Contains("if (_recording || _seeking) return", Read("PlayerPage.cs.txt"));
        Assert.DoesNotContain("ex.Message", seek);
        var injection = Read("DebugServer.cs.txt");
        Assert.Contains("LeftTrigger = name == \"lt\" ? 1 : 0", injection);
        Assert.Contains("RightTrigger = name == \"rt\" ? 1 : 0", injection);
        Assert.Contains("_injector.InjectGamepadInput(new InjectedInputGamepadInfo());", injection);
    }

    private static PlaybackPlan Plan() => new()
    {
        PlanId = "plan", PlanAttemptKey = "route", SessionId = "session", Delivery = "server_transcode_hls",
        RequestedMediaFileId = "file", EffectiveMediaFileId = "file",
        Source = new PlaybackSource { DurationSeconds = 1000 },
        Stream = new StreamV3 { Protocol = "hls", MimeType = "application/vnd.apple.mpegurl" },
        EffectiveRecipe = new EffectiveRecipeV3 { VideoCodec = "h264", AudioCodec = "aac", DynamicRange = "sdr", Height = 1080 },
        SelectedTracks = new SelectedTracksV3 { Audio = new TrackIdentityV3 { Id = "audio", Index = 0 } },
    };

    private static PlaybackDecision Decision(PlaybackPlan plan) => new()
    {
        Outcome = "playable", SessionId = "session", PlaybackPlan = plan, ServerFeatures = [PlaybackSeekReanchor.Feature],
    };

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
