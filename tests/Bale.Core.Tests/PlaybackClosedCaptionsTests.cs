using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

/// <summary>
/// In-band closed captions in the player (<see cref="PlaybackClosedCaptions"/>, docs/closed-captions.md): which mounts get a
/// decoder, when the subtitle lists offer CC, what picking it does, what a remount keeps and that nothing is remembered
/// (TvSubtitleIdentity.kt, TvSubtitleHudState.kt, TvSubtitleTransactionAdapter.kt, TvPlayerViewModel.kt
/// selectedSubtitleTrackIndex). How the player page uses it is PlayerClosedCaptionsWiringTests.
/// </summary>
public class PlaybackClosedCaptionsTests
{
    private static readonly PlaybackSelectorOption Off = new("Off", "No subtitles", true, TrackIndex: -1);
    private static readonly PlaybackSelectorOption English = new("English", "SRT", false, TrackIndex: 0);

    [Fact]
    public void OnlyFfmpegMountsOfTheCodecsTheTapReadsGetADecoder()
    {
        foreach (var codec in new[] { "h264", "hevc", "mpeg2video" })
            Assert.True(PlaybackClosedCaptions.Taps(Route(codec)), codec);
        foreach (var codec in new[] { "vp9", "av1", "vc1", "mpeg4", null })
            Assert.False(PlaybackClosedCaptions.Taps(Route(codec)), codec ?? "none");
        // Windows' own source has no packet tap.
        Assert.False(PlaybackClosedCaptions.Taps(Route("h264", ffmpeg: false)));
        // The route's codec names are the ones the decide step normalizes to.
        Assert.True(PlaybackClosedCaptions.Taps(PlaybackRoutePolicy.Decide(PlaybackRoutePolicyTests.Plan("original_http", "avc", "aac"), false)));
        Assert.True(PlaybackClosedCaptions.Taps(PlaybackRoutePolicy.Decide(PlaybackRoutePolicyTests.Plan("original_http", "h265", "aac"), false)));
    }

    [Fact]
    public void CcIsOfferedOnceCc1CarriedCaptionsAndStaysForTheAttempt()
    {
        var captions = new PlaybackClosedCaptions();
        IReadOnlyList<PlaybackSelectorOption> options = [Off, English];
        Assert.False(captions.Offered);
        Assert.Same(options, captions.WithEntry(options));
        // Padding, another channel or CEA-708 data alone are no CC1 captions (the one track Media3 shows decodes CC1).
        Assert.False(captions.Observe(0));
        Assert.False(captions.Observe(ClosedCaptions.SeenCcData));
        Assert.False(captions.Observe(ClosedCaptions.SeenCc3 | ClosedCaptions.SeenDtvcc | ClosedCaptions.SeenCcData));
        Assert.False(captions.Offered);

        Assert.True(captions.Observe(ClosedCaptions.SeenCc1 | ClosedCaptions.SeenCcData));
        Assert.True(captions.Discovered);
        // Once: a remount's new decoder starts unseen, and the row stays.
        Assert.False(captions.Observe(ClosedCaptions.SeenCc1));
        Assert.True(captions.Offered);

        // TvSubtitleHudState.kt buildTvSubtitleHudOptions: player-discovered tracks after the server's rows; nothing picks it.
        var listed = captions.WithEntry(options);
        Assert.Equal([Off, English, new PlaybackSelectorOption("CC", string.Empty, false, TrackIndex: PlaybackClosedCaptions.TrackIndex)], listed);
        Assert.False(captions.Selected);
        Assert.Equal(1u, PlaybackClosedCaptions.Channel);
        Assert.Equal(ClosedCaptions.SeenCc1, PlaybackClosedCaptions.SeenChannel);
    }

    [Fact]
    public void PickingCcWithoutAServerSubtitleNeedsNoServer()
    {
        var captions = Found();
        Assert.False(captions.Pick(PlanPlaying(subtitle: null)));
        Assert.True(captions.Selected);
        // The CC row is checked, and the plan's Off is not.
        var listed = captions.WithEntry([Off, English]);
        Assert.Equal(["CC"], listed.Where(option => option.IsSelected).Select(option => option.Title));
        Assert.True(captions.PlanPlays(PlanPlaying(subtitle: null)));
    }

    [Fact]
    public void PickingCcOverAServerSubtitleTurnsItOffFirst()
    {
        var captions = Found();
        var playing = PlanPlaying(subtitle: 0);
        Assert.True(PlaybackClosedCaptions.ServerSubtitleShows(playing));
        // True: the Off path first, so the server stops its sidecar or burn-in.
        Assert.True(captions.Pick(playing));
        Assert.True(captions.Selected);
        // The Off answer's mount (or the answer adopted in place) draws CC.
        Assert.True(captions.PlanPlays(PlanPlaying(subtitle: null)));

        // An Off the server refused (or a player busy with another change): its subtitle stays, CC goes off again.
        var refused = Found();
        Assert.True(refused.Pick(playing));
        Assert.False(refused.PlanPlays(playing));
        Assert.False(refused.Selected);
    }

    [Fact]
    public void OffEndsCcAtOnceAndAServerSubtitleOnceAPlanPlaysIt()
    {
        var captions = Found();
        captions.Pick(PlanPlaying(subtitle: null));
        // A server subtitle: CC stays while the change is in flight, so a refused change leaves it on.
        Assert.False(captions.PickTrack(0));
        Assert.True(captions.Selected);
        Assert.True(captions.PlanPlays(PlanPlaying(subtitle: null)));
        // The plan that plays it ends CC, as on a remount or an answer adopted in place.
        Assert.False(captions.PlanPlays(PlanPlaying(subtitle: 0)));
        Assert.False(captions.Selected);
        Assert.Equal(["English"], captions.WithEntry([Off with { IsSelected = false }, English with { IsSelected = true }])
            .Where(option => option.IsSelected).Select(option => option.Title));

        // Off: at once (true: detach now).
        captions.Pick(PlanPlaying(subtitle: null));
        Assert.True(captions.PickTrack(-1));
        Assert.False(captions.Selected);
        Assert.False(captions.PickTrack(-1));
        // The row stays offered after it was picked off.
        Assert.True(captions.Offered);
        Assert.Throws<ArgumentOutOfRangeException>(() => captions.PickTrack(PlaybackClosedCaptions.TrackIndex));
    }

    [Fact]
    public void ARecoveryOrSeekReplanKeepsCcOn()
    {
        // The viewer started with a server subtitle and picked CC: the Off path's request names no subtitle, and the start
        // the session now plays carries none either (PlaybackReplan.WithTracks).
        var start = Start(new PlaybackSelection(SubtitleIndex: 2));
        var playing = PlanPlaying(subtitle: 2);
        var captions = Found();
        Assert.True(captions.Pick(playing));
        var off = PlaybackReplan.ForTrackChange(start, playing, "plan-attempt", [], 1, 10, audioIndex: null, subtitleIndex: -1);
        Assert.Null(off.SelectedTracks.Subtitle);
        start = PlaybackReplan.WithTracks(start, off.SelectedTracks);
        var answer = PlanPlaying(subtitle: null);
        Assert.True(captions.PlanPlays(answer));

        // TvPlayerViewModel.kt selectedSubtitleTrackIndex keeps a player-discovered CEA-608 track as keep-current: the
        // recovery, the seek reanchor and the HLS remount ask the server for no subtitle, and their mounts draw CC again.
        var recovery = PlaybackReplan.ForFailure(start, answer, "plan-attempt", [], 1, 30,
            PlaybackReplan.Classify(unchecked((int)0xC00D36B4), isDecoderError: true, isNetworkError: false));
        Assert.Null(recovery.SelectedTracks.Subtitle);
        Assert.Null(PlaybackReplan.ForSeek(start, answer, "plan-attempt", [], 1, 30).SelectedTracks.Subtitle);
        Assert.True(captions.PlanPlays(PlanPlaying(subtitle: null)));
        Assert.True(captions.PlanPlays(PlanPlaying(subtitle: null)));
        Assert.True(captions.Selected);
    }

    [Fact]
    public void AutoNeverPicksCc()
    {
        // The Detail page's Auto ranks the catalog's tracks, which never list in-band captions: finding them picks nothing,
        // and no plan does.
        var captions = Found();
        Assert.False(captions.Selected);
        Assert.False(captions.PlanPlays(PlanPlaying(subtitle: null)));
        Assert.False(captions.PlanPlays(PlanPlaying(subtitle: 0)));
        Assert.DoesNotContain(captions.WithEntry([Off, English]), option => option.TrackIndex == PlaybackClosedCaptions.TrackIndex && option.IsSelected);
    }

    [Fact]
    public void TheCcRowIsATrackOfTheOptionsPanelWithoutAttributes()
    {
        var captions = Found();
        var rows = PlaybackOptionsTabs.TrackRows(captions.WithEntry([Off]));
        Assert.Equal([new PlaybackTrackRow("Off", null, true, -1), new PlaybackTrackRow("CC", null, false, PlaybackClosedCaptions.TrackIndex)], rows);
        // TvPlayerHud.kt "Tracks · N" counts every row but Off, and a file with only captions still shows the Subtitles tab.
        Assert.Equal(1, PlaybackOptionsTabs.TrackCount(rows));
        Assert.Contains(PlaybackOptionsTab.Subtitles, PlaybackOptionsTabs.Visible(false, 1, PlaybackOptionsTabs.TrackCount(rows), 0));
    }

    [Fact]
    public void AServerSubtitleShowsWhenTheSelectedTracksNameOne()
    {
        Assert.False(PlaybackClosedCaptions.ServerSubtitleShows(null));
        Assert.False(PlaybackClosedCaptions.ServerSubtitleShows(new PlaybackPlan()));
        Assert.False(PlaybackClosedCaptions.ServerSubtitleShows(PlanPlaying(subtitle: null)));
        Assert.False(PlaybackClosedCaptions.ServerSubtitleShows(new PlaybackPlan
        {
            SelectedTracks = new SelectedTracksV3 { Subtitle = new TrackIdentityV3 { Index = -1 } },
        }));
        Assert.True(PlaybackClosedCaptions.ServerSubtitleShows(PlanPlaying(subtitle: 0)));
        // A burn-in names its track too.
        Assert.True(PlaybackClosedCaptions.ServerSubtitleShows(new PlaybackPlan
        {
            SelectedTracks = new SelectedTracksV3 { Subtitle = new TrackIdentityV3 { Id = "file:file:subtitle:3" } },
            Subtitle = new SubtitleDecisionV3 { Mode = "burn_in" },
        }));
    }

    private static PlaybackClosedCaptions Found()
    {
        var captions = new PlaybackClosedCaptions();
        Assert.True(captions.Observe(ClosedCaptions.SeenCc1));
        return captions;
    }

    private static PlaybackRouteDecision Route(string? codec, bool ffmpeg = true) => new(ffmpeg, false, codec, "aac", 0, 1920, 1080);

    /// <summary>A direct play of file "file" with its selected subtitle (a combined index), or none.</summary>
    private static PlaybackPlan PlanPlaying(int? subtitle)
    {
        var plan = PlaybackRoutePolicyTests.Plan("original_http", "h264", "aac");
        plan.Source!.MediaFileId = "file";
        plan.SelectedTracks!.Subtitle = subtitle is { } index ? new TrackIdentityV3 { Id = $"file:file:subtitle:{index}", Index = index } : null;
        plan.Subtitle = new SubtitleDecisionV3 { Mode = subtitle is null ? "off" : "render" };
        return plan;
    }
}
