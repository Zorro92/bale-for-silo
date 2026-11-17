using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>Mid-playback audio/subtitle picks: a track_change replan of the same session at the position.</summary>
public class PlaybackTrackChangeTests
{
    [Fact]
    public void AnAudioPickReplacesTheAudioAndKeepsTheSubtitle()
    {
        var start = Start();
        var request = PlaybackReplan.ForTrackChange(start, Plan(), "mount", ["old"], 1, 754.5, audioIndex: 2, subtitleIndex: null);
        Assert.Equal("track_change", request.Operation);
        Assert.Equal(754.5, request.PositionSeconds);
        Assert.Equal(new PlaybackTrackIdentityV3("file:file:audio:2", 2), request.SelectedTracks.Audio);
        Assert.Equal(new PlaybackTrackIdentityV3("file:file:subtitle:3", 3), request.SelectedTracks.Subtitle);
        Assert.Null(request.Failure);
        Assert.Same(start.ClientCapabilities, request.ClientCapabilities);
    }

    [Fact]
    public void ASubtitlePickOrOffReplacesOnlyTheSubtitle()
    {
        var on = PlaybackReplan.ForTrackChange(Start(), Plan(), "mount", [], 2, 10, audioIndex: null, subtitleIndex: 0);
        Assert.Equal(new PlaybackTrackIdentityV3("file:file:audio:1", 1), on.SelectedTracks.Audio);
        Assert.Equal(new PlaybackTrackIdentityV3("file:file:subtitle:0", 0), on.SelectedTracks.Subtitle);
        var off = PlaybackReplan.ForTrackChange(Start(), Plan(), "mount", [], 2, 10, audioIndex: null, subtitleIndex: -1);
        Assert.Null(off.SelectedTracks.Subtitle);
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackReplan.ForTrackChange(Start(), Plan(), "mount", [], 1, 10, -1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackReplan.ForTrackChange(Start(), Plan(), "mount", [], 1, 10, null, -2));
    }

    [Fact]
    public void TheCommittedTracksBecomeTheStartsTracks()
    {
        var start = PlaybackReplan.WithTracks(Start(), new(new("file:file:audio:2", 2), null));
        Assert.Equal(2, start.AudioTrackIndex);
        Assert.Equal("file:file:audio:2", start.AudioTrackId);
        Assert.Null(start.SubtitleTrackIndex);
        Assert.Null(start.SubtitleTrackId);
    }

    [Fact]
    public void ADirectPlayAudioPickSwitchesOnThePlayerAndARemuxReplans()
    {
        Assert.True(PlaybackReplan.SwitchesAudioLocally(Plan(), 2));
        Assert.False(PlaybackReplan.SwitchesAudioLocally(Plan(), 1));
        Assert.False(PlaybackReplan.SwitchesAudioLocally(Plan(), -1));
        Assert.False(PlaybackReplan.SwitchesAudioLocally(null, 2));
        foreach (var delivery in new[] { "server_remux_progressive", "server_remux_hls", "server_transcode_hls" })
        {
            var packaged = Plan();
            packaged.Delivery = delivery;
            Assert.False(PlaybackReplan.SwitchesAudioLocally(packaged, 2));
        }
    }

    [Fact]
    public void ALocalAudioSwitchIsRecordedLikeACommittedPickAndKeepsTheSubtitle()
    {
        var plan = Plan();
        var start = PlaybackReplan.WithLocalAudio(Start(), plan, 2);
        Assert.Equal(2, start.AudioTrackIndex);
        Assert.Equal("file:file:audio:2", start.AudioTrackId);
        Assert.Equal(3, start.SubtitleTrackIndex);
        Assert.Equal(2, plan.SelectedTracks!.Audio!.Index);
        Assert.Equal("file:file:audio:2", plan.SelectedTracks.Audio.Id);
        Assert.Equal(3, plan.SelectedTracks.Subtitle!.Index);
        // A later replan (seek reanchor, failure fallback) carries the switched track.
        var later = PlaybackReplan.ForTrackChange(start, plan, "mount", [], 1, 10, audioIndex: null, subtitleIndex: null);
        Assert.Equal(new PlaybackTrackIdentityV3("file:file:audio:2", 2), later.SelectedTracks.Audio);

        var bare = Plan();
        bare.SelectedTracks = null;
        PlaybackReplan.WithLocalAudio(Start(), bare, 0);
        Assert.Equal(0, bare.SelectedTracks!.Audio!.Index);
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackReplan.WithLocalAudio(Start(), Plan(), -1));
    }

    [Fact]
    public void ASubtitleAnswerOnTheSameStreamKeepsTheTransport()
    {
        PlaybackPlan Answer(Action<PlaybackPlan>? change = null)
        {
            var plan = Plan();
            plan.PlanId = "plan-2";
            plan.Stream!.Url = "/api/v2/stream/session/original";
            plan.Subtitle = new SubtitleDecisionV3 { Mode = "render", TrackId = "t2" };
            plan.SelectedTracks!.Subtitle = new TrackIdentityV3 { Id = "file:file:subtitle:2", Index = 2 };
            change?.Invoke(plan);
            return plan;
        }
        var playing = Plan();
        playing.Stream!.Url = "/api/v2/stream/session/original";
        playing.Subtitle = new SubtitleDecisionV3 { Mode = "render", TrackId = "t3" };
        Assert.True(PlaybackReplan.KeepsTransport(playing, Answer()));
        // A freshly signed stream token is the same stream.
        Assert.True(PlaybackReplan.KeepsTransport(playing, Answer(plan => plan.Stream!.Url = "/api/v2/stream/session/original?st=fresh")));
        Assert.Equal("stream.url", PlaybackReplan.TransportChange(playing, Answer(plan => plan.Stream!.Url = "/api/v2/stream/other/original?st=x")));
        Assert.True(PlaybackReplan.KeepsTransport(playing, Answer(plan => plan.Subtitle = new SubtitleDecisionV3 { Mode = "off" })));
        // Anything the mount is built from changes: remount.
        Assert.False(PlaybackReplan.KeepsTransport(playing, Answer(plan => plan.Subtitle!.Mode = "burn_in")));
        Assert.False(PlaybackReplan.KeepsTransport(playing, Answer(plan => plan.Delivery = "server_transcode_hls")));
        Assert.False(PlaybackReplan.KeepsTransport(playing, Answer(plan => plan.Stream!.Url = "/api/v2/stream/other/original")));
        Assert.False(PlaybackReplan.KeepsTransport(playing, Answer(plan => plan.SelectedTracks!.Audio!.Index = 2)));
        Assert.False(PlaybackReplan.KeepsTransport(playing, Answer(plan => plan.Timeline = new TimelineV3 { StreamOriginSeconds = 600 })));
        Assert.False(PlaybackReplan.KeepsTransport(playing, Answer(plan => plan.EffectiveRecipe = new EffectiveRecipeV3 { VideoCodec = "h264" })));
        Assert.False(PlaybackReplan.KeepsTransport(playing, Answer(plan => plan.Source!.MediaFileId = "other")));
        var burnedIn = Plan();
        burnedIn.Stream!.Url = "/api/v2/stream/session/original";
        burnedIn.Subtitle = new SubtitleDecisionV3 { Mode = "burn_in" };
        Assert.False(PlaybackReplan.KeepsTransport(burnedIn, Answer()));
    }

    private static PlaybackStartRequestV3 Start() => PlaybackStartBuilder.Build("file", "profile",
        "attempt", null, new(AudioIndex: 1, SubtitleIndex: 3), ConsoleEvidence.Default, PlaybackTestSupport.Identity);

    private static PlaybackPlan Plan() => new()
    {
        PlanId = "plan", PlanAttemptKey = "plan-key", Delivery = "original_http",
        Source = new PlaybackSource { Container = "mkv", VideoCodec = "hevc", AudioCodec = "eac3", Width = 1920, Height = 1080 },
        Stream = new StreamV3 { Container = "mkv", Protocol = "http_progressive" },
        SelectedTracks = new SelectedTracksV3
        {
            Audio = new TrackIdentityV3 { Id = "file:file:audio:1", Index = 1 },
            Subtitle = new TrackIdentityV3 { Id = "file:file:subtitle:3", Index = 3 },
        },
    };
}
