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
