using Bale.Core.Playback;
using Bale.Core.Settings;

namespace Bale.Core.Tests;

/// <summary>
/// The player's skip markers: Android TV's intro pill and manual recap/credits pill (TvPlayerScreen.kt
/// tvManualMarkerSkipTarget, MarkerSkipTarget.kt), and tvOS's credits setting (CreditsAutoSkipPolicy).
/// </summary>
public class PlaybackSkipMarkersTests
{
    private static readonly MarkerTimeline Markers = MarkerTimeline.Resolve(
    [
        (MarkerKind.Recap, 0, 25), (MarkerKind.Intro, 30, 90),
        (MarkerKind.Credits, 2975, 3160), (MarkerKind.Credits, 3189, 3300),
    ]);

    private static SkipMarkerFacts Facts(double position, IntroSkipMode mode = IntroSkipMode.Ask, bool autoCredits = false,
        PromptActivity activity = PromptActivity.Playing, bool presented = true, bool loading = false, long now = 0,
        string? file = "file-1") => new()
        {
            Position = position, Markers = Markers, FileKey = file, Mode = mode, AutoSkipCredits = autoCredits,
            Activity = activity, Presented = presented, Loading = loading, NowMs = now,
        };

    [Fact]
    public void TheIntroOffersSkipIntroWhichTakesFocusAndRunsATimer()
    {
        var skips = new PlaybackSkipMarkers();
        Assert.Null(skips.Update(Facts(35)));
        var pill = Assert.IsType<SkipPill>(skips.Pill);
        Assert.Equal((SkipPillKind.SkipIntro, "Skip Intro", (string?)null), (pill.Kind, pill.Label, pill.Caption));
        Assert.True(pill.Timed);
        Assert.True(pill.TakesFocus);
        Assert.Equal(0, pill.Progress);
        skips.Update(Facts(37, now: 2_500));
        Assert.Equal(0.5, skips.Pill!.Progress, 6);
        Assert.Null(skips.Update(Facts(39, now: 5_000)));
        Assert.Null(skips.Pill);
        // Select on the offer seeks to the intro's end.
        skips.Update(Facts(95, now: 5_100));
        skips.Update(Facts(35, now: 5_200));
        Assert.Equal(new SkipMarkerSeek(90, SkipPillKind.SkipIntro, false), skips.Select(Facts(35, now: 5_300)));
        Assert.Null(skips.Pill);
    }

    [Fact]
    public void AlwaysSkipsTheIntroAndOffersWatchIntroUnderItsCaption()
    {
        var skips = new PlaybackSkipMarkers();
        Assert.Equal(new SkipMarkerSeek(90, SkipPillKind.SkipIntro, true), skips.Update(Facts(35, IntroSkipMode.Always)));
        var pill = skips.Pill!;
        Assert.Equal((SkipPillKind.WatchIntro, "Watch Intro", "Intro skipped"), (pill.Kind, pill.Label, pill.Caption));
        Assert.True(pill.TakesFocus);
        // Pinned to the intro while playback runs on past it; Watch Intro goes back to the start.
        Assert.Null(skips.Update(Facts(91, IntroSkipMode.Always, now: 1_000)));
        Assert.Equal(SkipPillKind.WatchIntro, skips.Pill!.Kind);
        Assert.Equal(new SkipMarkerSeek(30, SkipPillKind.WatchIntro, false), skips.Select(Facts(92, IntroSkipMode.Always)));
        Assert.Null(skips.Update(Facts(35, IntroSkipMode.Always, now: 2_000)));
        Assert.Null(skips.Pill);
    }

    [Fact]
    public void TheUndoSurvivesTheReloadItsOwnSkipCauses()
    {
        // An HLS or remux seek remounts: the new mount is not presented for a moment, and must not take the undo down.
        var skips = new PlaybackSkipMarkers();
        skips.Update(Facts(35, IntroSkipMode.Always));
        skips.Update(Facts(90, IntroSkipMode.Always, activity: PromptActivity.Stalled, presented: false, loading: true, now: 500));
        Assert.Equal(SkipPillKind.WatchIntro, skips.Pill?.Kind);
        skips.Update(Facts(84, IntroSkipMode.Always, now: 1_000));
        Assert.Equal(SkipPillKind.WatchIntro, skips.Pill?.Kind);
    }

    [Fact]
    public void NothingIsOfferedOrSkippedBeforeTheMountPresents()
    {
        var skips = new PlaybackSkipMarkers();
        Assert.Null(skips.Update(Facts(35, IntroSkipMode.Always, presented: false)));
        Assert.Null(skips.Pill);
        Assert.Null(skips.Update(Facts(3000, autoCredits: true, presented: false)));
        Assert.Null(skips.Pill);
        Assert.Null(skips.Update(Facts(10, presented: false)));
        Assert.Null(skips.Pill);
        Assert.NotNull(skips.Update(Facts(35, IntroSkipMode.Always)));
    }

    [Fact]
    public void RecapAndCreditsGetAManualPillWithNoTimerThatNeverTakesFocus()
    {
        var skips = new PlaybackSkipMarkers();
        skips.Update(Facts(10));
        Assert.Equal(new SkipPill(SkipPillKind.SkipRecap, "Skip Recap", null, 0, false, false, 0), skips.Pill);
        skips.Update(Facts(3000, now: 60_000));
        Assert.Equal(new SkipPill(SkipPillKind.SkipCredits, "Skip Credits", null, 0, false, false, 0), skips.Pill);
        // No timer: still up minutes later, gone in the gap between two credits ranges, back in the second.
        skips.Update(Facts(3150, now: 600_000));
        Assert.Equal(SkipPillKind.SkipCredits, skips.Pill?.Kind);
        skips.Update(Facts(3170));
        Assert.Null(skips.Pill);
        skips.Update(Facts(3200));
        Assert.Equal(SkipPillKind.SkipCredits, skips.Pill?.Kind);
        // Each occurrence skips to its own end, never over the scene between them.
        Assert.Equal(new SkipMarkerSeek(3300, SkipPillKind.SkipCredits, false), skips.Select(Facts(3200)));
        skips.Update(Facts(3000));
        Assert.Equal(new SkipMarkerSeek(3160, SkipPillKind.SkipCredits, false), skips.Select(Facts(3000)));
        Assert.Null(skips.Pill);
        Assert.False(skips.Back());
    }

    [Fact]
    public void TheMarkerPillWaitsWhileTheIntroPillShowsAndHidesDuringAReload()
    {
        var overlapping = MarkerTimeline.Resolve([(MarkerKind.Intro, 0, 60), (MarkerKind.Recap, 10, 40)]);
        var skips = new PlaybackSkipMarkers();
        skips.Update(Facts(15) with { Markers = overlapping });
        Assert.Equal(SkipPillKind.SkipIntro, skips.Pill?.Kind);
        Assert.True(skips.Back());
        skips.Update(Facts(16) with { Markers = overlapping });
        Assert.Equal(SkipPillKind.SkipRecap, skips.Pill?.Kind);
        skips.Update(Facts(17, loading: true) with { Markers = overlapping });
        Assert.Null(skips.Pill);
        skips.Update(Facts(18, file: null) with { Markers = overlapping });
        Assert.Null(skips.Pill);
    }

    [Fact]
    public void BackDismissesOnlyTheIntroPill()
    {
        var skips = new PlaybackSkipMarkers();
        skips.Update(Facts(35));
        Assert.True(skips.Back());
        Assert.Null(skips.Pill);
        Assert.False(skips.Back());
        // Resolved for this attempt; a reset (a new attempt) offers it again.
        skips.Update(Facts(36, now: 100));
        Assert.Null(skips.Pill);
        skips.Reset();
        skips.Update(Facts(37, now: 200));
        Assert.Equal(SkipPillKind.SkipIntro, skips.Pill?.Kind);
    }

    [Fact]
    public void SkipCreditsOnSkipsEachCreditsOccurrenceOnceWhilePlaying()
    {
        var skips = new PlaybackSkipMarkers();
        // Not while paused or seeking: entering the credits that way skips once playback runs there.
        Assert.Null(skips.Update(Facts(2980, autoCredits: true, activity: PromptActivity.Paused)));
        Assert.Equal(SkipPillKind.SkipCredits, skips.Pill?.Kind);
        Assert.Null(skips.Update(Facts(2980, autoCredits: true, activity: PromptActivity.Stalled)));
        Assert.Equal(new SkipMarkerSeek(3160, SkipPillKind.SkipCredits, true), skips.Update(Facts(2980, autoCredits: true)));
        Assert.Null(skips.Pill);
        // Scrubbing back into the same credits does not skip again: the manual pill offers it.
        Assert.Null(skips.Update(Facts(3000, autoCredits: true)));
        Assert.Equal(SkipPillKind.SkipCredits, skips.Pill?.Kind);
        // The second occurrence is its own.
        Assert.Equal(new SkipMarkerSeek(3300, SkipPillKind.SkipCredits, true), skips.Update(Facts(3190, autoCredits: true)));
        // Off (the default): only the manual pill.
        var off = new PlaybackSkipMarkers();
        Assert.Null(off.Update(Facts(2980)));
        Assert.Equal(SkipPillKind.SkipCredits, off.Pill?.Kind);
    }

    [Fact]
    public void AManualSkipCreditsMarksItsOccurrenceAsSkipped()
    {
        // tvOS skipCredits inserts the occurrence's key too: seeking back in later does not skip it by itself.
        var skips = new PlaybackSkipMarkers();
        skips.Update(Facts(3000));
        Assert.Equal(3160, skips.Select(Facts(3000))?.Target);
        Assert.Null(skips.Update(Facts(3001, autoCredits: true)));
        Assert.Equal(SkipPillKind.SkipCredits, skips.Pill?.Kind);
        skips.Reset();
        Assert.Equal(3160, skips.Update(Facts(3001, autoCredits: true))?.Target);
    }

    [Fact]
    public void SelectWithNoPillDoesNothing()
    {
        var skips = new PlaybackSkipMarkers();
        Assert.Null(skips.Select(Facts(3000)));
        skips.Update(Facts(200));
        Assert.Null(skips.Pill);
        Assert.Null(skips.Select(Facts(200)));
        // The pill is re-read at the press: credits that ended meanwhile give nothing.
        skips.Update(Facts(3000));
        Assert.Null(skips.Select(Facts(3170)));
    }

    [Theory]
    // The console's case: closing credits that run to the end of a 2652 s file skip to its duration.
    [InlineData(2652, true)]
    [InlineData(2651.4, true)]
    // At or within a second of the end, or past it.
    [InlineData(2651, true)]
    [InlineData(2700, true)]
    // Anything earlier still plays: a scene after the credits, or credits before the last minutes.
    [InlineData(2650.9, false)]
    [InlineData(2600, false)]
    [InlineData(0, false)]
    public void ASkipToTheEndOfTheFileEndsPlaybackInsteadOfSeeking(double target, bool ends) =>
        Assert.Equal(ends, PlaybackSkipMarkers.EndsPlayback(target, 2652));

    [Fact]
    public void WithoutAKnownDurationASkipAlwaysSeeks()
    {
        foreach (var duration in new double?[] { null, 0, -5, double.NaN, double.PositiveInfinity })
            Assert.False(PlaybackSkipMarkers.EndsPlayback(2652, duration), $"{duration}");
        Assert.False(PlaybackSkipMarkers.EndsPlayback(double.NaN, 2652));
        Assert.True(PlaybackSkipMarkers.EndsPlayback(double.PositiveInfinity, 2652));
    }

    [Fact]
    public void ClosingCreditsThatRunToTheEndSkipToIt()
    {
        // Manual and automatic Skip Credits both target the occurrence's end, here the file's.
        var markers = MarkerTimeline.Resolve([(MarkerKind.Credits, 2560, 2652)]);
        SkipMarkerFacts At(double position, bool autoCredits = false) => new()
        {
            Position = position, Markers = markers, FileKey = "file-1", Mode = IntroSkipMode.Ask, AutoSkipCredits = autoCredits,
            Activity = PromptActivity.Playing, Presented = true,
        };
        var manual = new PlaybackSkipMarkers();
        manual.Update(At(2600));
        Assert.Equal(SkipPillKind.SkipCredits, manual.Pill?.Kind);
        var press = Assert.IsType<SkipMarkerSeek>(manual.Select(At(2600)));
        Assert.True(PlaybackSkipMarkers.EndsPlayback(press.Target, 2652));
        var automatic = Assert.IsType<SkipMarkerSeek>(new PlaybackSkipMarkers().Update(At(2561, autoCredits: true)));
        Assert.True(automatic.Automatic);
        Assert.True(PlaybackSkipMarkers.EndsPlayback(automatic.Target, 2652));
    }
}
