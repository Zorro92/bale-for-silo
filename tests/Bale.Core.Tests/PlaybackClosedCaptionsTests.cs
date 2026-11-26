using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

/// <summary>
/// In-band closed captions in the player (<see cref="PlaybackClosedCaptions"/>, docs/closed-captions.md): which mounts get a
/// decoder, which caption channels the subtitle lists offer (every one found, owner 2026-11-26), what picking one does, what a
/// remount keeps and that nothing is remembered (TvSubtitleIdentity.kt, TvSubtitleHudState.kt, TvSubtitleTransactionAdapter.kt,
/// TvPlayerViewModel.kt selectedSubtitleTrackIndex). How the player page uses it is PlayerClosedCaptionsWiringTests.
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
    public void ALoneCc1IsOfferedAsTheOneCcRowAndStaysForTheAttempt()
    {
        var captions = new PlaybackClosedCaptions();
        IReadOnlyList<PlaybackSelectorOption> options = [Off, English];
        Assert.False(captions.Offered);
        Assert.Same(options, captions.WithEntries(options));
        // Padding, Text or CEA-708 data alone are no caption channel.
        Assert.Equal(0u, captions.Observe(0));
        Assert.Equal(0u, captions.Observe(ClosedCaptions.SeenCcData));
        Assert.Equal(0u, captions.Observe(ClosedCaptions.SeenDtvcc | ClosedCaptions.SeenCcData));
        Assert.False(captions.Offered);

        Assert.Equal(ClosedCaptions.SeenCc1, captions.Observe(ClosedCaptions.SeenCc1 | ClosedCaptions.SeenCcData));
        Assert.True(captions.Discovered);
        // Once: a remount's new decoder starts unseen, and the row stays.
        Assert.Equal(0u, captions.Observe(ClosedCaptions.SeenCc1));
        Assert.True(captions.Offered);
        Assert.False(captions.AllFound);

        // TvSubtitleHudState.kt buildTvSubtitleHudOptions: player-discovered tracks after the server's rows; nothing picks it.
        // CC1 alone keeps Android's one "CC" row (owner, 2026-11-26), on CC1.
        var listed = captions.WithEntries(options);
        Assert.Equal([Off, English, new PlaybackSelectorOption("CC", string.Empty, false, TrackIndex: PlaybackClosedCaptions.TrackIndex)], listed);
        Assert.False(captions.Selected);
        Assert.Equal(1u, captions.Channel);
        Assert.Equal(ClosedCaptions.SeenCc1 | ClosedCaptions.SeenCc2 | ClosedCaptions.SeenCc3 | ClosedCaptions.SeenCc4,
            PlaybackClosedCaptions.SeenChannels);
    }

    [Fact]
    public void EveryChannelFoundGetsItsOwnRowInChannelOrder()
    {
        // Deliberate deviation (owner, 2026-11-26): Media3 shows one CC1 track; Bale lists every channel the stream carries.
        var captions = new PlaybackClosedCaptions();
        Assert.Equal(ClosedCaptions.SeenCc3, captions.Observe(ClosedCaptions.SeenCc3 | ClosedCaptions.SeenCcData));
        // A channel other than CC1 alone is named by its number.
        Assert.Equal([Off, Row("CC3", 3)], captions.WithEntries([Off]));
        Assert.False(captions.Offers(1));
        Assert.True(captions.Offers(3));

        // A channel found later adds its row, in channel order; CC1 is "CC1" once it is not alone.
        Assert.Equal(ClosedCaptions.SeenCc1, captions.Observe(ClosedCaptions.SeenCc1 | ClosedCaptions.SeenCc3));
        Assert.Equal([Off, English, Row("CC1", 1), Row("CC3", 3)], captions.WithEntries([Off, English]));
        Assert.Equal(ClosedCaptions.SeenCc2 | ClosedCaptions.SeenCc4, captions.Observe(ClosedCaptions.SeenCc2 | ClosedCaptions.SeenCc4 | ClosedCaptions.SeenDtvcc));
        Assert.Equal([Off, Row("CC1", 1), Row("CC2", 2), Row("CC3", 3), Row("CC4", 4)], captions.WithEntries([Off]));
        Assert.True(captions.AllFound);
        Assert.Equal("cc1,cc2,cc3,cc4", PlaybackClosedCaptions.Names(captions.Found));
        Assert.Equal("cc3", PlaybackClosedCaptions.Names(ClosedCaptions.SeenCc3 | ClosedCaptions.SeenCcData));
        Assert.Equal(string.Empty, PlaybackClosedCaptions.Names(0));
    }

    [Fact]
    public void EachChannelHasItsOwnTrackIndexThatNoServerTrackUses()
    {
        // -1 is Off and 0 and up are the server's combined subtitle indexes: the CC rows take -2 to -5.
        Assert.Equal([-2, -3, -4, -5], new uint[] { 1, 2, 3, 4 }.Select(PlaybackClosedCaptions.TrackIndexOf));
        Assert.Equal(PlaybackClosedCaptions.TrackIndex, PlaybackClosedCaptions.TrackIndexOf(1));
        foreach (var channel in new uint[] { 1, 2, 3, 4 })
            Assert.Equal(channel, PlaybackClosedCaptions.ChannelOf(PlaybackClosedCaptions.TrackIndexOf(channel)));
        foreach (var index in new[] { -6, -1, 0, 1, 2, int.MinValue, int.MaxValue })
            Assert.Null(PlaybackClosedCaptions.ChannelOf(index));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackClosedCaptions.TrackIndexOf(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackClosedCaptions.TrackIndexOf(5));
        Assert.False(new PlaybackClosedCaptions().Offers(0));
        Assert.False(Found(ClosedCaptions.SeenCc1).Offers(5));
    }

    [Fact]
    public void PickingAChannelChecksItsRowAndEveryMountDecodesIt()
    {
        var captions = Found(ClosedCaptions.SeenCc1 | ClosedCaptions.SeenCc3);
        Assert.False(captions.Pick(3, PlanPlaying(subtitle: null)));
        Assert.True(captions.Selected);
        Assert.Equal(3u, captions.Channel);
        Assert.Equal(["CC3"], captions.WithEntries([Off, English]).Where(option => option.IsSelected).Select(option => option.Title));
        // A remount (recovery, seek reanchor, HLS remount, quality change) keeps CC on that channel.
        Assert.True(captions.PlanPlays(PlanPlaying(subtitle: null)));
        Assert.Equal(3u, captions.Channel);

        // Another channel while one is on: the row moves, CC stays on.
        Assert.False(captions.Pick(1, PlanPlaying(subtitle: null)));
        Assert.Equal((true, 1u), (captions.Selected, captions.Channel));
        Assert.Equal(["CC1"], captions.WithEntries([Off]).Where(option => option.IsSelected).Select(option => option.Title));

        // Off ends CC but keeps the channel, so a later mount's decoder decodes what the viewer picked last.
        captions.Pick(3, PlanPlaying(subtitle: null));
        Assert.True(captions.PickTrack(-1));
        Assert.Equal((false, 3u), (captions.Selected, captions.Channel));
        Assert.DoesNotContain(captions.WithEntries([Off]), option => option.TrackIndex < -1 && option.IsSelected);
        Assert.False(captions.PlanPlays(PlanPlaying(subtitle: null)));
        Assert.Equal(3u, captions.Channel);

        Assert.Throws<ArgumentOutOfRangeException>(() => captions.Pick(0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => captions.Pick(5, null));
        Assert.Equal((false, 3u), (captions.Selected, captions.Channel));
    }

    [Fact]
    public void ALoneCc1RowStaysCcWhilePickedUntilAnotherChannelIsFound()
    {
        var captions = Found();
        captions.Pick(1, PlanPlaying(subtitle: null));
        Assert.Equal(["CC"], captions.WithEntries([Off]).Where(option => option.IsSelected).Select(option => option.Title));
        // CC3 appears later: the checked row keeps its track index and is named CC1 now (an open menu shows it when next opened).
        captions.Observe(ClosedCaptions.SeenCc3);
        Assert.Equal([Off with { IsSelected = false }, Row("CC1", 1, selected: true), Row("CC3", 3)], captions.WithEntries([Off]));
    }

    [Fact]
    public void PickingCcWithoutAServerSubtitleNeedsNoServer()
    {
        var captions = Found();
        Assert.False(captions.Pick(1, PlanPlaying(subtitle: null)));
        Assert.True(captions.Selected);
        // The CC row is checked, and the plan's Off is not.
        var listed = captions.WithEntries([Off, English]);
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
        Assert.True(captions.Pick(1, playing));
        Assert.True(captions.Selected);
        // The Off answer's mount (or the answer adopted in place) draws CC.
        Assert.True(captions.PlanPlays(PlanPlaying(subtitle: null)));

        // An Off the server refused (or a player busy with another change): its subtitle stays, CC goes off again.
        var refused = Found(ClosedCaptions.SeenCc1 | ClosedCaptions.SeenCc4);
        Assert.True(refused.Pick(4, playing));
        Assert.False(refused.PlanPlays(playing));
        Assert.False(refused.Selected);
        // The channel stays the picked one: the decoder was switched at the pick.
        Assert.Equal(4u, refused.Channel);
    }

    [Fact]
    public void OffEndsCcAtOnceAndAServerSubtitleOnceAPlanPlaysIt()
    {
        var captions = Found();
        captions.Pick(1, PlanPlaying(subtitle: null));
        // A server subtitle: CC stays while the change is in flight, so a refused change leaves it on.
        Assert.False(captions.PickTrack(0));
        Assert.True(captions.Selected);
        Assert.True(captions.PlanPlays(PlanPlaying(subtitle: null)));
        // The plan that plays it ends CC, as on a remount or an answer adopted in place.
        Assert.False(captions.PlanPlays(PlanPlaying(subtitle: 0)));
        Assert.False(captions.Selected);
        Assert.Equal(["English"], captions.WithEntries([Off with { IsSelected = false }, English with { IsSelected = true }])
            .Where(option => option.IsSelected).Select(option => option.Title));

        // Off: at once (true: detach now).
        captions.Pick(1, PlanPlaying(subtitle: null));
        Assert.True(captions.PickTrack(-1));
        Assert.False(captions.Selected);
        Assert.False(captions.PickTrack(-1));
        // The row stays offered after it was picked off.
        Assert.True(captions.Offered);
        foreach (var channel in new uint[] { 1, 2, 3, 4 })
            Assert.Throws<ArgumentOutOfRangeException>(() => captions.PickTrack(PlaybackClosedCaptions.TrackIndexOf(channel)));
    }

    [Fact]
    public void ARecoveryOrSeekReplanKeepsCcOn()
    {
        // The viewer started with a server subtitle and picked CC: the Off path's request names no subtitle, and the start
        // the session now plays carries none either (PlaybackReplan.WithTracks).
        var start = Start(new PlaybackSelection(SubtitleIndex: 2));
        var playing = PlanPlaying(subtitle: 2);
        var captions = Found(ClosedCaptions.SeenCc1 | ClosedCaptions.SeenCc3);
        Assert.True(captions.Pick(3, playing));
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
        // On the picked channel: every new mount's decoder decodes it.
        Assert.Equal(3u, captions.Channel);
    }

    [Fact]
    public void AutoNeverPicksCc()
    {
        // The Detail page's Auto ranks the catalog's tracks, which never list in-band captions: finding them picks nothing,
        // and no plan does.
        var captions = Found(PlaybackClosedCaptions.SeenChannels);
        Assert.False(captions.Selected);
        Assert.False(captions.PlanPlays(PlanPlaying(subtitle: null)));
        Assert.False(captions.PlanPlays(PlanPlaying(subtitle: 0)));
        Assert.DoesNotContain(captions.WithEntries([Off, English]), option => PlaybackClosedCaptions.ChannelOf(option.TrackIndex ?? -1) is not null && option.IsSelected);
        Assert.Equal(1u, captions.Channel);
    }

    [Fact]
    public void TheCcRowsAreTracksOfTheOptionsPanelWithoutAttributes()
    {
        var captions = Found();
        var rows = PlaybackOptionsTabs.TrackRows(captions.WithEntries([Off]));
        Assert.Equal([new PlaybackTrackRow("Off", null, true, -1), new PlaybackTrackRow("CC", null, false, PlaybackClosedCaptions.TrackIndex)], rows);
        // TvPlayerHud.kt "Tracks · N" counts every row but Off, and a file with only captions still shows the Subtitles tab.
        Assert.Equal(1, PlaybackOptionsTabs.TrackCount(rows));
        Assert.Contains(PlaybackOptionsTab.Subtitles, PlaybackOptionsTabs.Visible(false, 1, PlaybackOptionsTabs.TrackCount(rows), 0));

        // Each channel is a track of its own.
        captions.Observe(ClosedCaptions.SeenCc2 | ClosedCaptions.SeenCc4);
        rows = PlaybackOptionsTabs.TrackRows(captions.WithEntries([Off, English]));
        Assert.Equal(["Off", "English", "CC1", "CC2", "CC4"], rows.Select(row => row.Name));
        Assert.Equal([-1, 0, -2, -3, -5], rows.Select(row => row.TrackIndex));
        Assert.All(rows.Where(row => row.TrackIndex < -1), row => Assert.Null(row.Attributes));
        Assert.Equal(4, PlaybackOptionsTabs.TrackCount(rows));
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

    /// <summary>An attempt whose mounts found <paramref name="seen"/>'s channels (CC1 alone by default).</summary>
    private static PlaybackClosedCaptions Found(uint seen = ClosedCaptions.SeenCc1)
    {
        var captions = new PlaybackClosedCaptions();
        Assert.Equal(seen & PlaybackClosedCaptions.SeenChannels, captions.Observe(seen));
        return captions;
    }

    /// <summary>The CC row of <paramref name="channel"/> as the lists show it.</summary>
    private static PlaybackSelectorOption Row(string title, uint channel, bool selected = false) =>
        new(title, string.Empty, selected, TrackIndex: PlaybackClosedCaptions.TrackIndexOf(channel));

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
